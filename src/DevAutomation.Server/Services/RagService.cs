using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using DevAutomation.Models;
using Microsoft.Extensions.Options;

namespace DevAutomation.Services;

public class RagService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly QdrantClient _qdrant;
    private readonly IOptions<OllamaSettings> _ollamaSettings;
    private readonly RagIndexerService _indexer;
    private readonly ILogger<RagService> _logger;

    public RagService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        QdrantClient qdrant,
        IOptions<OllamaSettings> ollamaSettings,
        RagIndexerService indexer,
        ILogger<RagService> logger)
    {
        _embedder = embedder;
        _qdrant   = qdrant;
        _ollamaSettings = ollamaSettings;
        _indexer  = indexer;
        _logger   = logger;
    }

    public async Task<List<RagChunk>> QueryAsync(string texto, int topK = 5, string[]? filtrosProjeto = null, string[]? filtrosTipo = null, float scoreThreshold = 0.2f)
    {
        if (!_indexer.IsReady)
        {
            _logger.LogWarning("RagIndexerService ainda não está pronto");
            return [];
        }

        try
        {
            var embeddingOptions = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["num_ctx"] = _ollamaSettings.Value.NumCtx } };
            var embeddingResult = await _embedder.GenerateAsync([texto], embeddingOptions);
            var embedding = embeddingResult[0].Vector;

            Filter? filter = BuildFilter(filtrosProjeto, filtrosTipo);

            var results = await _qdrant.SearchAsync(
                _indexer.CollectionName,
                embedding.ToArray(),
                filter: filter,
                limit: (ulong)topK,
                scoreThreshold: scoreThreshold);

            return results.Select(r => new RagChunk
            {
                Conteudo = r.Payload.GetValueOrDefault("conteudo")?.StringValue ?? "",
                Fonte    = r.Payload.GetValueOrDefault("fonte")?.StringValue    ?? "",
                Projeto  = r.Payload.GetValueOrDefault("projeto")?.StringValue  ?? "",
                Tipo     = r.Payload.GetValueOrDefault("tipo")?.StringValue     ?? ""
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro na busca RAG para query: {Texto}", texto[..Math.Min(80, texto.Length)]);
            return [];
        }
    }

    public async Task<string> BuildContextAsync(string texto, int topK = 5, string[]? filtrosProjeto = null)
    {
        // busca memória sem threshold — sempre entra no contexto independente do score
        var chunksMemoria = await QueryAsync(texto, topK: 5, filtrosTipo: ["memory"], scoreThreshold: 0f);
        var chunksCode    = await QueryAsync(texto, topK: topK, filtrosProjeto: filtrosProjeto, filtrosTipo: ["code", "config", "devrequest"]);

        var todos = chunksMemoria.Concat(chunksCode).ToList();
        if (todos.Count == 0) return "";

        const int limiteTotal = 6000;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- CONTEXTO DO PROJETO ---");

        for (int i = 0; i < todos.Count; i++)
        {
            var c      = todos[i];
            var header = $"[{i + 1}] Fonte: {c.Fonte} | Projeto: {c.Projeto} | Tipo: {c.Tipo}";
            var espaco = limiteTotal - sb.Length - header.Length - 4;
            if (espaco <= 0) break;

            var conteudo = c.Conteudo.Length <= espaco ? c.Conteudo : c.Conteudo[..espaco] + "...";
            sb.AppendLine(header);
            sb.AppendLine(conteudo);
            sb.AppendLine();
        }

        sb.AppendLine("--- FIM DO CONTEXTO ---");
        return sb.ToString();
    }

    private static Filter? BuildFilter(string[]? filtrosProjeto, string[]? filtrosTipo)
    {
        var filter = new Filter();

        if (filtrosProjeto is { Length: 1 })
        {
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition { Key = "projeto", Match = new Match { Text = filtrosProjeto[0] } }
            });
        }
        else if (filtrosProjeto is { Length: > 1 })
        {
            var sub = new Filter();
            sub.Should.AddRange(filtrosProjeto.Select(p => new Condition
            {
                Field = new FieldCondition { Key = "projeto", Match = new Match { Text = p } }
            }));
            filter.Must.Add(new Condition { Filter = sub });
        }

        if (filtrosTipo is { Length: 1 })
        {
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition { Key = "tipo", Match = new Match { Text = filtrosTipo[0] } }
            });
        }
        else if (filtrosTipo is { Length: > 1 })
        {
            var sub = new Filter();
            sub.Should.AddRange(filtrosTipo.Select(t => new Condition
            {
                Field = new FieldCondition { Key = "tipo", Match = new Match { Text = t } }
            }));
            filter.Must.Add(new Condition { Filter = sub });
        }

        return filter.Must.Count == 0 ? null : filter;
    }
}
