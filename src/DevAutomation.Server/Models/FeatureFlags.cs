namespace DevAutomation.Models;

public class FeatureFlags
{
    public bool RagEnabled              { get; set; }
    public bool QdrantAvailable         { get; set; }
    public bool OllamaAvailable         { get; set; }
    public bool OrchestratorAvailable   { get; set; }
    public bool DevAgentGeminiEnabled   { get; set; }
    public bool DevAgentClaudeEnabled   { get; set; }
}
