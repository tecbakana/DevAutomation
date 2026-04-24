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

    private readonly RagService _rag;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _runningProcesses = new();

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public OrchestratorService(
        IConfiguration config,
        IHubContext<OrchestratorHub> hub,
        ILogger<OrchestratorService> logger,
        RagService rag)
    {
        _devRequestsDir = config["DevAutomation:DevRequestsDir"]!;
        _claudePath     = config["DevAutomation:ClaudePath"] ?? "claude";
        _hub     = hub;
        _logger  = logger;
        _rag     = rag;
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

        // Se houver resposta do usuário a um impedimento anterior, inclui contexto
        if (!string.IsNullOrEmpty(request.Pendencias) && !string.IsNullOrEmpty(request.RespostaUsuario))
        {
            prompt = $"{prompt}\n\n--- CONTEXTO ADICIONAL ---\nVocê havia solicitado esclarecimentos: {request.Pendencias}\nResposta do usuário: {request.RespostaUsuario}";
        }

        // Busca contexto RAG filtrado pelo projeto da dev-request
        var projeto = request.Api?.ToLower() switch
        {
            "cmsx" or "multiplai" => new[] { "cmsx" },
            "salematic"           => new[] { "salematic" },
            "forge"               => new[] { "forge" },
            _                     => null
        };

        var queryRag     = (request.Descricao + " " + request.Detalhes).Trim();
        var ragChunks    = await _rag.QueryAsync(queryRag, topK: 5, filtrosProjeto: projeto);
        var contextoRag  = ragChunks.Count > 0
            ? await _rag.BuildContextAsync(queryRag, topK: 5, filtrosProjeto: projeto)
            : "";

        // Instrui Claude a sinalizar impeditivos de forma estruturada
        prompt = "REGRA OBRIGATÓRIA: Se você precisar de qualquer informação antes de implementar, ou se houver qualquer ambiguidade que impeça a implementação segura, você DEVE responder SOMENTE com este JSON — sem texto antes, sem texto depois, sem markdown:\n{\"impeditivo\": true, \"pendencias\": \"descreva suas dúvidas aqui\"}\nNão escreva código, não escreva explicação, não use markdown. Somente o JSON acima.\n\n---\n\n" + prompt;

        if (!string.IsNullOrEmpty(contextoRag))
        {
            prompt = contextoRag + "\n\n---\n\n" + prompt;
            _logger.LogInformation("[RAG] Contexto injetado para dev-request {Id}: {N} chunks do projeto {Projeto}",
                request.Id, ragChunks.Count, projeto is { Length: > 0 } ? string.Join(",", projeto) : "todos");
        }

        var agentModel = new Dictionary<string, string>
        {
            { "alto","claude-sonnet-4-6" },
            { "medio", "claude-sonnet-4-6" },
            { "baixo", "claude-haiku-4-5-20251001" }
        }.FirstOrDefault(kv => kv.Key.Equals(request.Impacto, StringComparison.OrdinalIgnoreCase)).Value ?? "claude-haiku-4-5-20251001";

        _logger.LogInformation("Despachando Claude para: {Dir} | {Prompt}", targetDir, prompt);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = _claudePath,
                Arguments              = $"--dangerously-skip-permissions --print --model \"{agentModel}\"",
                WorkingDirectory       = targetDir,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(psi)!;
            _runningProcesses[request.Id] = process;

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            _runningProcesses.TryRemove(request.Id, out _);

            // Recarrega o arquivo para verificar se foi cancelado durante a execução
            var currentJson = await File.ReadAllTextAsync(filePath);
            var currentReq  = JsonSerializer.Deserialize<DevRequest>(currentJson);
            if (currentReq?.Status == "cancelado")
            {
                _logger.LogInformation("Dev-request {Id} foi cancelada durante execução.", request.Id);
                return;
            }

            if (TryParseImpeditivo(output, out var pendencias))
            {
                request.Status     = "impeditivo";
                request.Pendencias = pendencias;
                request.Resultado  = null;
            }
            else if (process.ExitCode == 0 && LooksLikeImpeditivo(output))
            {
                request.Status     = "impeditivo";
                request.Pendencias = output.Trim();
                request.Resultado  = null;
            }
            else
            {
                request.Status    = process.ExitCode == 0 ? "em_testes" : "error";
                request.Resultado = process.ExitCode == 0 ? output : error;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningProcesses.TryRemove(request.Id, out _);

            // Verifica se foi cancelado
            try
            {
                var currentJson = await File.ReadAllTextAsync(filePath);
                var currentReq  = JsonSerializer.Deserialize<DevRequest>(currentJson);
                if (currentReq?.Status == "cancelado") return;
            }
            catch { }

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
                await NotifyAsync(request);
                if (_runningProcesses.TryRemove(request.Id, out var runningProcess))
                {
                    try
                    {
                        if (!runningProcess.HasExited)
                        {
                            runningProcess.Kill(entireProcessTree: true);
                            _logger.LogInformation("Processo Claude encerrado para dev-request {Id}", request.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Não foi possível encerrar o processo da dev-request {Id}", request.Id);
                    }
                }
                break;
            case "retomar":
                await DispatchAsync(request, file);
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

    private static bool LooksLikeImpeditivo(string output)
    {
        var trimmed = output.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        // Última linha termina com '?' — agente está perguntando
        var lastLine = trimmed.Split('\n').Last(l => !string.IsNullOrWhiteSpace(l));
        return lastLine.TrimEnd().EndsWith('?');
    }

    private static bool TryParseImpeditivo(string output, out string? pendencias)
    {
        pendencias = null;
        try
        {
            var start = output.LastIndexOf("{\"impeditivo\"");
            if (start < 0) start = output.LastIndexOf("{ \"impeditivo\"");
            if (start < 0) return false;

            var end = output.IndexOf('}', start);
            if (end < 0) return false;

            var doc = JsonSerializer.Deserialize<JsonElement>(output[start..(end + 1)]);
            if (doc.TryGetProperty("impeditivo", out var imp) && imp.ValueKind == JsonValueKind.True)
            {
                pendencias = doc.TryGetProperty("pendencias", out var p) ? p.GetString() : null;
                return true;
            }
        }
        catch { }
        return false;
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
