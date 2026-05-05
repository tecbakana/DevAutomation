using DevAutomation.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DevAutomation.Services;

public class RagIndexerService : BackgroundService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly QdrantClient _qdrant;
    private readonly OllamaSettings _ollamaSettings;
    private readonly ILogger<RagIndexerService> _logger;

    private const string Collection = "forge_rag";
    private const ulong  VectorSize = 768;

    private static readonly (string Root, string Projeto, string[] Exts)[] _sources =
    [
        (@"T:\Developer\RepositorioTrabalho\tecbakana\ForgeV2\src", "forge",    [".cs"]),
        (@"T:\Developer\RepositorioTrabalho\tecbakana\cmsx",         "cmsx",     [".cs", ".ts"]),
        (@"T:\Developer\salematic",                                   "salematic",[".cs"]),
    ];

    private static readonly string[] _ignoredSegments = ["bin", "obj", "node_modules"];
    private static readonly string[] _ignoredSuffixes  = [".Designer.cs"];
    private static readonly string[] _ignoredContains  = ["Migrations"];

    public bool   IsReady        { get; private set; }
    public string CollectionName => Collection;

    public RagIndexerService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        QdrantClient qdrant,
        IOptions<OllamaSettings> options,
        ILogger<RagIndexerService> logger)
    {
        _embedder = embedder;
        _qdrant   = qdrant;
        _ollamaSettings = options.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        var count = await _qdrant.CountAsync(Collection, cancellationToken: ct);
        if (count == 0)
            await ReindexAsync(ct);
        else
        {
            IsReady = true;
            _logger.LogInformation("Qdrant: {Count} vetores já indexados", count);
        }
    }

    public async Task ReindexAsync(CancellationToken ct = default)
    {
        IsReady = false;
        var existing = await _qdrant.ListCollectionsAsync(ct);
        if (existing.Contains(Collection))
            await _qdrant.DeleteCollectionAsync(Collection, cancellationToken: ct);
        await EnsureCollectionAsync(ct);

        var batch = new List<PointStruct>();
        int total = 0;

        foreach (var (root, projeto, exts) in _sources)
        {
            if (!Directory.Exists(root))
            {
                _logger.LogWarning("Diretório não encontrado: {Root}", root);
                continue;
            }

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !_ignoredSegments.Any(seg => f.Split(Path.DirectorySeparatorChar).Contains(seg)))
                .Where(f => !_ignoredSuffixes.Any(suf => f.EndsWith(suf, StringComparison.OrdinalIgnoreCase)))
                .Where(f => !_ignoredContains.Any(part => f.Contains(part, StringComparison.OrdinalIgnoreCase)));

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) return;

                var lines    = await File.ReadAllLinesAsync(file, ct);
                var relativo = Path.GetRelativePath(root, file);
                var tipo     = DeterminaTipo(file, projeto);

#pragma warning disable SKEXP0050
                var chunks = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph: 400, overlapTokens: 40);
#pragma warning restore SKEXP0050
                _logger.LogInformation("Indexando [{Projeto}] {Rel} → {N} chunks", projeto, relativo, chunks.Count);

                foreach (var chunk in chunks)
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        var embeddingOptions = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["num_ctx"] = _ollamaSettings.NumCtx } };
                        var embeddingResult = await _embedder.GenerateAsync([chunk], embeddingOptions, cancellationToken: ct);
                var embedding = embeddingResult[0].Vector;
                        batch.Add(new PointStruct
                        {
                            Id      = new PointId { Uuid = Guid.NewGuid().ToString() },
                            Vectors = embedding.ToArray(),
                            Payload =
                            {
                                ["conteudo"] = chunk,
                                ["fonte"]    = relativo,
                                ["projeto"]  = projeto,
                                ["tipo"]     = tipo
                            }
                        });
                        total++;

                        if (batch.Count >= 100)
                        {
                            await _qdrant.UpsertAsync(Collection, batch, cancellationToken: ct);
                            batch.Clear();
                            _logger.LogInformation("Checkpoint: {Total} chunks enviados ao Qdrant", total);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao gerar embedding: {File}", file);
                    }
                }''
            }
        }

        if (batch.Count > 0)
            await _qdrant.UpsertAsync(Collection, batch, cancellationToken: ct);

        IsReady = true;
        _logger.LogInformation("Indexação concluída: {Total} chunks no Qdrant", total);
    }

    public async Task<(int TotalChunks, Dictionary<string, int> PorProjeto)> GetStatsAsync(CancellationToken ct = default)
    {
        var total     = (int)await _qdrant.CountAsync(Collection, cancellationToken: ct);
        var projetos  = _sources.Select(s => s.Projeto).Distinct();
        var porProjeto = new Dictionary<string, int>();

        foreach (var p in projetos)
        {
            var filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition { Key = "projeto", Match = new Match { Text = p } }
                    }
                }
            };
            porProjeto[p] = (int)await _qdrant.CountAsync(Collection, filter: filter, cancellationToken: ct);
        }

        return (total, porProjeto);
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existing = await _qdrant.ListCollectionsAsync(ct);
        if (!existing.Contains(Collection))
            await _qdrant.CreateCollectionAsync(Collection,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
    }

    private static string DeterminaTipo(string path, string projeto)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "config";
        if (projeto == "forge" && path.Contains("dev-requests")) return "devrequest";
        return "code";
    }
}
