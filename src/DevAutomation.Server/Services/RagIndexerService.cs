using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using DevAutomation.Models;
using Microsoft.Extensions.Hosting;

namespace DevAutomation.Services;

public class RagIndexerService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RagIndexerService> _logger;

    private const string OllamaUrl   = "http://localhost:11434/api/embeddings";
    private const string OllamaModel = "nomic-embed-text";

    private List<RagChunk> _index = [];
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private static readonly (string Root, string Projeto, string[] Exts)[] _sources =
    [
        (@"T:\Developer\RepositorioTrabalho\tecbakana\ForgeV2\src", "forge",    [".cs"]),
        (@"T:\Developer\RepositorioTrabalho\tecbakana\cmsx",         "cmsx",     [".cs", ".ts"]),
        (@"T:\Developer\salematic",                                   "salematic",[".cs"]),
    ];

    private static readonly string[] _ignoredSegments = ["bin", "obj", "node_modules"];
    private static readonly string[] _ignoredSuffixes  = [".Designer.cs"];
    private static readonly string[] _ignoredContains  = ["Migrations"];

    public bool IsReady => _index.Count > 0;

    public List<RagChunk> GetChunks() => _index;

    public (int TotalChunks, Dictionary<string, int> PorProjeto) GetStats()
    {
        var porProjeto = _index.GroupBy(c => c.Projeto).ToDictionary(g => g.Key, g => g.Count());
        return (_index.Count, porProjeto);
    }

    public RagIndexerService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<RagIndexerService> logger)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var indexPath = GetIndexPath();
        bool stale = !File.Exists(indexPath) ||
                     (DateTime.UtcNow - File.GetLastWriteTimeUtc(indexPath)).TotalHours > 24;

        if (stale)
            await ReindexAsync(ct);
        else
            await LoadIndexAsync(indexPath);
    }

    public async Task ReindexAsync(CancellationToken ct = default)
    {
        var chunks = new List<RagChunk>();

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

                var lines = await File.ReadAllLinesAsync(file, ct);
                var relativo = Path.GetRelativePath(root, file);
                var tipo = DeterminaTipo(file, projeto);

                _logger.LogInformation("Indexando [{Projeto}] {Rel} ({N} linhas)", projeto, relativo, lines.Length);

                var blocos = Chunkar(lines);
                foreach (var bloco in blocos)
                {
                    try
                    {
                        var vetor = await GetEmbeddingAsync(bloco, ct);
                        chunks.Add(new RagChunk
                        {
                            Conteudo = bloco,
                            Fonte    = relativo,
                            Tipo     = tipo,
                            Projeto  = projeto,
                            Vetor    = vetor
                        });
                        if (chunks.Count % 50 == 0)
                        {
                            var ip = GetIndexPath();
                            Directory.CreateDirectory(Path.GetDirectoryName(ip)!);
                            await File.WriteAllTextAsync(ip, JsonSerializer.Serialize(chunks, _json), ct);
                            _logger.LogInformation("Checkpoint: {Total} chunks salvos", chunks.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao gerar embedding: {File}", file);
                    }
                }
            }
        }

        var indexPath = GetIndexPath();
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(chunks, _json), ct);

        _index = chunks;
        _logger.LogInformation("Indexação concluída: {Total} chunks", _index.Count);
    }

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var http  = _httpFactory.CreateClient();
        var body  = JsonSerializer.Serialize(new { model = OllamaModel, prompt = text });
        var resp  = await http.PostAsync(OllamaUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var raw   = await resp.Content.ReadAsStringAsync(ct);
        var doc   = JsonNode.Parse(raw);
        var vals  = doc?["embedding"]?.AsArray();
        if (vals == null) throw new InvalidOperationException($"Resposta Ollama inválida: {raw}");
        return [.. vals.Select(v => v!.GetValue<float>())];
    }

    private async Task LoadIndexAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            _index = JsonSerializer.Deserialize<List<RagChunk>>(json) ?? [];
            _logger.LogInformation("Índice RAG carregado: {Total} chunks", _index.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar índice RAG");
        }
    }

    private string GetIndexPath()
    {
        var root = _config["DevAutomation:RootPath"]
            ?? @"T:\Developer\RepositorioTrabalho\tecbakana\ForgeV2";
        return Path.Combine(root, "data", "rag-index.json");
    }

    private static IEnumerable<string> Chunkar(string[] lines)
    {
        if (lines.Length <= 300)
        {
            yield return string.Join('\n', lines);
            yield break;
        }

        const int tamanho = 150;
        const int overlap  = 20;
        int inicio = 0;
        while (inicio < lines.Length)
        {
            var fim = Math.Min(inicio + tamanho, lines.Length);
            yield return string.Join('\n', lines[inicio..fim]);
            inicio = fim - overlap;
            if (inicio >= lines.Length - overlap) break;
        }
    }

    private static string DeterminaTipo(string path, string projeto)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "config";
        if (projeto == "forge" && path.Contains("dev-requests")) return "devrequest";
        return "code";
    }
}
