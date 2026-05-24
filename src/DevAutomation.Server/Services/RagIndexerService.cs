using DevAutomation.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Security.Cryptography;

namespace DevAutomation.Services;

public class RagIndexerService : BackgroundService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly QdrantClient _qdrant;
    private readonly OllamaSettings _ollamaSettings;
    private readonly ILogger<RagIndexerService> _logger;

    private const string Collection = "forge_rag";
    private const ulong  VectorSize = 1024;

    private static readonly (string Root, string Projeto, string[] Exts)[] _sources =
    [
        (@"T:\Developer\RepositorioTrabalho\tecbakana\ForgeV2\src",                                                             "forge",           [".cs"]),
        (@"T:\Developer\RepositorioTrabalho\tecbakana\cmsx",                                                                    "cmsx",            [".cs", ".ts"]),
        (@"T:\Developer\salematic",                                                                                              "salematic",       [".cs"]),
        (@"C:\Users\mvoze\.claude\projects\T--Developer-RepositorioTrabalho-tecbakana-cmsx\memory",                            "memory_cmsx",     [".md"]),
        (@"C:\Users\mvoze\.claude\projects\T--Developer-RepositorioTrabalho-tecbakana-ForgeV2\memory",                         "memory_forge",    [".md"]),
        (@"C:\Users\mvoze\.claude\projects\T--Developer-salematic\memory",                                                      "memory_salematic",[".md"]),
    ];

    private static readonly string[] _ignoredSegments = ["bin", "obj", "node_modules"];
    private static readonly string[] _ignoredSuffixes  = [".Designer.cs"];
    private static readonly string[] _ignoredContains  = ["Migrations", "worktrees"];

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureCollectionAsync(stoppingToken);
        var count = await _qdrant.CountAsync(Collection, cancellationToken: stoppingToken);
        if (count == 0)
            await ReindexAsync(stoppingToken);
        else
            await ReindexIncrementalAsync(stoppingToken);
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

            foreach (var file in EnumerarArquivos(root, exts))
            {
                if (ct.IsCancellationRequested) return;
                var hash     = await ComputeHashAsync(file, ct);
                var relativo = Path.GetRelativePath(root, file);
                var tipo     = DeterminaTipo(file, projeto);
                total       += await IndexarArquivoAsync(file, relativo, projeto, tipo, hash, batch, ct);
            }
        }

        if (batch.Count > 0)
            await _qdrant.UpsertAsync(Collection, batch, cancellationToken: ct);

        IsReady = true;
        _logger.LogInformation("Indexação completa: {Total} chunks no Qdrant", total);
    }

    public async Task ReindexIncrementalAsync(CancellationToken ct = default)
    {
        IsReady = false;
        _logger.LogInformation("Iniciando reindexação incremental...");

        var hashMap       = await BuildHashMapAsync(ct);
        var processados   = new HashSet<string>();
        var batch         = new List<PointStruct>();
        int adicionados   = 0;
        int ignorados     = 0;
        int removidos     = 0;

        foreach (var (root, projeto, exts) in _sources)
        {
            if (!Directory.Exists(root))
            {
                _logger.LogWarning("Diretório não encontrado: {Root}", root);
                continue;
            }

            foreach (var file in EnumerarArquivos(root, exts))
            {
                if (ct.IsCancellationRequested) return;

                var relativo = Path.GetRelativePath(root, file);
                var chave    = $"{projeto}|{relativo}";
                processados.Add(chave);

                var hash = await ComputeHashAsync(file, ct);

                if (hashMap.TryGetValue(chave, out var hashExistente) && hashExistente == hash)
                {
                    ignorados++;
                    continue;
                }

                if (hashMap.ContainsKey(chave))
                    await DeletarPorFonteAsync(projeto, relativo, ct);

                var tipo = DeterminaTipo(file, projeto);
                adicionados += await IndexarArquivoAsync(file, relativo, projeto, tipo, hash, batch, ct);
            }
        }

        if (batch.Count > 0)
            await _qdrant.UpsertAsync(Collection, batch, cancellationToken: ct);

        // remove chunks de arquivos que não existem mais
        foreach (var chave in hashMap.Keys.Where(k => !processados.Contains(k)))
        {
            var partes = chave.Split('|', 2);
            await DeletarPorFonteAsync(partes[0], partes[1], ct);
            removidos++;
        }

        IsReady = true;
        _logger.LogInformation(
            "Reindexação incremental: +{Add} chunks | {Skip} ignorados | {Rem} arquivos removidos",
            adicionados, ignorados, removidos);
    }

    public async Task<(int TotalChunks, Dictionary<string, int> PorProjeto)> GetStatsAsync(CancellationToken ct = default)
    {
        var total      = (int)await _qdrant.CountAsync(Collection, cancellationToken: ct);
        var projetos   = _sources.Select(s => s.Projeto).Distinct();
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

    // retorna número de chunks gerados
    private async Task<int> IndexarArquivoAsync(
        string file, string relativo, string projeto, string tipo, string hash,
        List<PointStruct> batch, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(file, ct);

#pragma warning disable SKEXP0050
        var chunks = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph: 400, overlapTokens: 40);
#pragma warning restore SKEXP0050

        _logger.LogInformation("Indexando [{Projeto}] {Rel} → {N} chunks", projeto, relativo, chunks.Count);

        int count = 0;
        foreach (var chunk in chunks)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var opts   = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["num_ctx"] = _ollamaSettings.NumCtx } };
                var result = await _embedder.GenerateAsync([chunk], opts, cancellationToken: ct);
                var vector = result[0].Vector;

                batch.Add(new PointStruct
                {
                    Id      = new PointId { Uuid = Guid.NewGuid().ToString() },
                    Vectors = vector.ToArray(),
                    Payload =
                    {
                        ["conteudo"] = chunk,
                        ["fonte"]    = relativo,
                        ["projeto"]  = projeto,
                        ["tipo"]     = tipo,
                        ["hash"]     = hash
                    }
                });
                count++;

                if (batch.Count >= 100)
                {
                    await _qdrant.UpsertAsync(Collection, batch, cancellationToken: ct);
                    batch.Clear();
                    _logger.LogInformation("Checkpoint: {Count} chunks enviados", count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao gerar embedding: {File}", file);
            }
        }

        return count;
    }

    private async Task<Dictionary<string, string>> BuildHashMapAsync(CancellationToken ct)
    {
        var map    = new Dictionary<string, string>();
        PointId? offset = null;

        do
        {
            var page = await _qdrant.ScrollAsync(
                Collection, offset: offset, limit: 250, payloadSelector: true,
                cancellationToken: ct);

            foreach (var point in page.Result)
            {
                var fonte   = point.Payload.GetValueOrDefault("fonte")?.StringValue;
                var projeto = point.Payload.GetValueOrDefault("projeto")?.StringValue;
                var hash    = point.Payload.GetValueOrDefault("hash")?.StringValue;
                if (fonte != null && projeto != null && hash != null)
                    map.TryAdd($"{projeto}|{fonte}", hash);
            }

            offset = page.NextPageOffset;
        }
        while (offset != null);

        return map;
    }

    private async Task DeletarPorFonteAsync(string projeto, string fonte, CancellationToken ct)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition { Field = new FieldCondition { Key = "projeto", Match = new Match { Text = projeto } } },
                new Condition { Field = new FieldCondition { Key = "fonte",   Match = new Match { Text = fonte   } } }
            }
        };
        await _qdrant.DeleteAsync(Collection, filter, cancellationToken: ct);
    }

    private static IEnumerable<string> EnumerarArquivos(string root, string[] exts) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !_ignoredSegments.Any(seg => f.Split(Path.DirectorySeparatorChar).Contains(seg)))
            .Where(f => !_ignoredSuffixes.Any(suf => f.EndsWith(suf, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !_ignoredContains.Any(part => f.Contains(part, StringComparison.OrdinalIgnoreCase)));

    private static async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
#pragma warning disable CA5351 // MD5 usado para deduplicação de conteúdo, não para segurança
        return Convert.ToHexString(MD5.HashData(bytes));
#pragma warning restore CA5351
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existing = await _qdrant.ListCollectionsAsync(ct);
        if (!existing.Contains(Collection))
        {
            await _qdrant.CreateCollectionAsync(Collection,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    private static string DeterminaTipo(string path, string projeto)
    {
        if (path.EndsWith(".md",   StringComparison.OrdinalIgnoreCase)) return "memory";
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "config";
        if (projeto == "forge" && path.Contains("dev-requests")) return "devrequest";
        return "code";
    }
}
