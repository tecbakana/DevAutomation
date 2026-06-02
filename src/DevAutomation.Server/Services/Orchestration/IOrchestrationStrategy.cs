namespace DevAutomation.Services.Orchestration;

public sealed record StrategyResult(string Output, string Error, int ExitCode);

public interface IOrchestrationStrategy
{
    public string Name { get; }
    public bool IsAvailable();
    public Task<StrategyResult> ExecuteAsync(string prompt, string workDir, string? model = null, CancellationToken ct = default);
}
