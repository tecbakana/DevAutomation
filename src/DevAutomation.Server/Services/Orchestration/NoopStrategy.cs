namespace DevAutomation.Services.Orchestration;

public class NoopStrategy : IOrchestrationStrategy
{
    public string Name => "noop";

    public bool IsAvailable() => false;

    public Task<StrategyResult> ExecuteAsync(string prompt, string workDir, string? model = null, CancellationToken ct = default)
        => Task.FromResult(new StrategyResult("Nenhum orquestrador configurado — implemente manualmente.", "", 0));
}
