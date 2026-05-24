using DevAutomation.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DevAutomation.Services;

public class RevisorService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ILogger<RagService> _logger;

    public RevisorService(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<RagService> logger)
    {
        _embedder = embedder;
        _logger   = logger;
    }
}
