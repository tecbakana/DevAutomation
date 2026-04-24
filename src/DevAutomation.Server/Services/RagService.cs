using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevAutomation.Models;

namespace DevAutomation.Services;

public class RagService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly RagIndexerService _indexer;
    private readonly ILogger<RagService> _logger;

    private const string OllamaUrl   = "http://localhost:11434/api/embeddings";
    private const string OllamaModel = "nomic-embed-text";

    public RagService(IHttpClientFactory httpFactory, RagIndexerService indexer, ILogger<RagService> logger)
    {
        _httpFactory = httpFactory;
        _indexer     = indexer;
        _logger      = logger;
    }

    public async Task<List<RagChunk>> QueryAsync(string texto, int topK = 5, string[]? filtrosProjeto = null)
    {
        if (!_indexer.IsReady)
        {
            _logger.LogWarning("RagIndexerService ainda não está pronto — retornando lista vazia");
            return [];
        }

        try
        {
            var vetor = await GetEmbeddingAsync(texto);

            var (total, _) = _indexer.GetStats();
            // Acessa os chunks via reflexão não é ideal; usamos o método público exposto pelo indexer
            var chunks = GetChunks();

            if (filtrosProjeto is { Length: > 0 })
                chunks = chunks.Where(c => filtrosProjeto.Contains(c.Projeto, StringComparer.OrdinalIgnoreCase)).ToList();

            return chunks
                .Select(c => (Chunk: c, Score: CosineSimilarity(vetor, c.Vetor)))
                .Where(x => x.Score >= 0.5f)
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Chunk)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro na busca RAG para query: {Texto}", texto[..Math.Min(80, texto.Length)]);
            return [];
        }
    }

    public async Task<string> BuildContextAsync(string texto, int topK = 5, string[]? filtrosProjeto = null)
    {
        var chunks = await QueryAsync(texto, topK, filtrosProjeto);
        if (chunks.Count == 0) return "";

        const int limiteTotal = 6000;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- CONTEXTO DO CODEBASE ---");

        int usados = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var header  = $"[{i + 1}] Fonte: {c.Fonte} | Projeto: {c.Projeto}";
            var espaco  = limiteTotal - sb.Length - header.Length - 4;
            if (espaco <= 0) break;

            var conteudo = c.Conteudo.Length <= espaco ? c.Conteudo : c.Conteudo[..espaco] + "...";
            sb.AppendLine(header);
            sb.AppendLine(conteudo);
            sb.AppendLine();
            usados++;
        }

        sb.AppendLine("--- FIM DO CONTEXTO ---");
        return sb.ToString();
    }

    private List<RagChunk> GetChunks() => _indexer.GetChunks();

    private async Task<float[]> GetEmbeddingAsync(string text)
    {
        var http = _httpFactory.CreateClient();
        var body = JsonSerializer.Serialize(new { model = OllamaModel, prompt = text });
        var resp = await http.PostAsync(OllamaUrl, new StringContent(body, Encoding.UTF8, "application/json"));
        var raw  = await resp.Content.ReadAsStringAsync();
        var doc  = JsonNode.Parse(raw);
        var vals = doc?["embedding"]?.AsArray();
        if (vals == null) throw new InvalidOperationException($"Resposta Ollama inválida: {raw}");
        return [.. vals.Select(v => v!.GetValue<float>())];
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom == 0 ? 0 : dot / denom;
    }
}
