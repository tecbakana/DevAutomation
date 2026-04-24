using System.Diagnostics;
using System.Text.Json;
using DevAutomation.Hubs;
using DevAutomation.Models;
using Microsoft.AspNetCore.SignalR;

namespace DevAutomation.Services;

public class OrchestratorService : BackgroundService
{
    private readonly string _devRequestsDir;
    private readonly string _claudePath;
    private readonly IHubContext<OrchestratorHub> _hub;
    private readonly ILogger<OrchestratorService> _logger;
    private FileSystemWatcher? _watcher;

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public OrchestratorService(
        IConfiguration config,
        IHubContext<OrchestratorHub> hub,
        ILogger<OrchestratorService> logger)
    {
        _devRequestsDir = config["DevAutomation:DevRequestsDir"]!;
        _claudePath     = config["DevAutomation:ClaudePath"] ?? "claude";
        _hub     = hub;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_devRequestsDir);

        _watcher = new FileSystemWatcher(_devRequestsDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;

        _logger.LogInformation("Orquestrador monitorando: {Dir}", _devRequestsDir);

        // Processa qualquer pendente que já exista ao iniciar
        await ProcessPendingAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        Task.Run(() => ProcessFileAsync(e.FullPath));
    }

    private async Task ProcessPendingAsync()
    {
        foreach (var file in Directory.GetFiles(_devRequestsDir, "*.json"))
            await ProcessFileAsync(file);
    }

    private async Task ProcessFileAsync(string filePath)
    {
        await Task.Delay(200); // aguarda o arquivo estar completamente escrito

        DevRequest? request;
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            request  = JsonSerializer.Deserialize<DevRequest>(json);
        }
        catch
        {
            return;
        }

        if (request is null || request.Status != "pending") return;

        _logger.LogInformation("Nova dev-request: {Id} — {Descricao}", request.Id, request.Descricao);

        // Toda request vai para backlog — aguarda aprovação manual
        request.Status = "backlog";
        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);
        _logger.LogInformation("Dev-request {Id} movida para backlog.", request.Id);
    }

    private async Task DispatchAsync(DevRequest request, string filePath)
    {
        request.Status = "in_progress";
        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);

        var targetDir = request.DiretorioAlvo ?? "T:\\devautomation";
        var prompt    = request.PromptAgente ?? request.Descricao;

        _logger.LogInformation("Despachando Claude para: {Dir} | {Prompt}", targetDir, prompt);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = _claudePath,
                Arguments              = $"--dangerously-skip-permissions --print -p \"{EscapeArg(prompt)}\"",
                WorkingDirectory       = targetDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            request.Status    = process.ExitCode == 0 ? "em_testes" : "error";
            request.Resultado = process.ExitCode == 0 ? output : error;
        }
        catch (Exception ex)
        {
            request.Status    = "error";
            request.Resultado = ex.Message;
            _logger.LogError(ex, "Erro ao executar Claude para dev-request {Id}", request.Id);
        }

        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);
    }

    private async Task SaveAsync(string filePath, DevRequest request)
    {
        var json = JsonSerializer.Serialize(request, _jsonOpts);
        await File.WriteAllTextAsync(filePath, json);
    }

    private async Task NotifyAsync(DevRequest request)
    {
        await _hub.Clients.All.SendAsync("devRequestUpdate", request);
    }

    public async Task<bool> ProcessActionAsync(string id, string action)
    {
        var file = Directory.GetFiles(_devRequestsDir, "*.json")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == id);

        if (file is null) return false;

        DevRequest? request;
        try { request = JsonSerializer.Deserialize<DevRequest>(await File.ReadAllTextAsync(file)); }
        catch { return false; }

        if (request is null) return false;

        switch (action)
        {
            case "implementar":
                await DispatchAsync(request, file);
                break;
            case "aprovar":
                request.Status = "aguardando";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(file, request);
                await NotifyAsync(request);
                await DispatchAsync(request, file);
                break;
            case "completar":
                request.Status = "done";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(file, request);
                break;
            case "cancelar":
                request.Status = "cancelado";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(file, request);
                break;
            case "aprovar_testes":
                request.Status = "done";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(file, request);
                await NotifyAsync(request);
                break;
            case "refazer":
                request.Status = "in_progress";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(file, request);
                await NotifyAsync(request);
                await DispatchAsync(request, file);
                break;
            case "ignorar":
                File.Delete(file);
                break;
        }

        return true;
    }

    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    public string DevRequestsDir => _devRequestsDir;

    // API pública: lista todas as dev-requests
    public IEnumerable<DevRequest> ListAll()
    {
        if (!Directory.Exists(_devRequestsDir)) return [];

        return Directory.GetFiles(_devRequestsDir, "*.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<DevRequest>(File.ReadAllText(f)); }
                catch { return null; }
            })
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .OrderByDescending(r => r.Timestamp);
    }
}
