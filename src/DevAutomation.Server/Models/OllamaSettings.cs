using System.ComponentModel.DataAnnotations;

namespace DevAutomation.Models;

public class OllamaSettings
{
    public const string SectionName = "OllamaSets";
    public string ModelName { get; init; } = string.Empty;
    public string Endpoint { get; init; } = "http://localhost:11434";

    [Range(1024, 32768, ErrorMessage = "O contexto deve estar entre 1024 e 32768 tokens.")]
    public int NumCtx { get; init; } = 4096;
    public int KeepAlive { get; init; } = -1;
}
