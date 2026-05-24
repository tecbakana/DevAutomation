using System.Text.Json.Serialization;

namespace DevAutomation.Models;

public record RevisorResponseModel
    (
        [property: JsonPropertyName("agent_engine")] string AgentEngine,
        [property: JsonPropertyName("approved")] bool Approved,
        [property: JsonPropertyName("score")] float Score,
        [property: JsonPropertyName("critique")] string Critique,
        [property: JsonPropertyName("refactoring_needed")] string? RefactoringNeeded
    );
