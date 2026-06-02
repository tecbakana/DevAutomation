using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevAutomation.Services.Orchestration;

public class ClaudeCliStrategy : IOrchestrationStrategy
{
    private readonly string _claudePath;
    private readonly ILogger<ClaudeCliStrategy> _logger;

    public string Name => "claude";

    public ClaudeCliStrategy(string claudePath, ILogger<ClaudeCliStrategy> logger)
    {
        _claudePath = claudePath;
        _logger     = logger;
    }

    public bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments              = _claudePath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<StrategyResult> ExecuteAsync(string prompt, string workDir, string? model = null, CancellationToken ct = default)
    {
        var psi = BuildPsi(workDir, model);

        using var process = Process.Start(psi)!;
        await using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Falha ao encerrar processo Claude via CancellationToken."); }
        });

        var stdinTask  = Task.Run(async () => { await process.StandardInput.WriteAsync(prompt); process.StandardInput.Close(); }, ct);
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask  = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdinTask, outputTask, errorTask);

        var output = await outputTask;
        var error  = await errorTask;
        await process.WaitForExitAsync(ct);

        return new StrategyResult(output, error, process.ExitCode);
    }

    private ProcessStartInfo BuildPsi(string workDir, string? model)
    {
        var args = new List<string>
        {
            "--dangerously-skip-permissions",
            "--print"
        };
        if (model is not null) { args.Add("--model"); args.Add(model); }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        ProcessStartInfo psi;

        // No Windows, claude é um .cmd e precisa de cmd.exe /c para ser resolvido pelo PATH
        if (isWindows && !_claudePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = workDir
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(_claudePath);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName               = _claudePath,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = workDir
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        return psi;
    }
}
