using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DevAutomation.Hubs;
using DevAutomation.Models;
using Microsoft.AspNetCore.SignalR;

namespace DevAutomation.Services;

public class OrchestratorService : BackgroundService
{
    // ActivitySource para monitoramento do ciclo de vida macro e micro das tarefas
    private static readonly ActivitySource ForgeSource = new("Forge.SoftwareFactory.Core");

    private readonly string _claudePath;
    private readonly string _globalClaudeMdPath;
    private readonly IHubContext<OrchestratorHub> _hub;
    private readonly ILogger<OrchestratorService> _logger;
    private FileSystemWatcher? _watcher;

    private readonly RagService _rag;
    private readonly RagIndexerService _indexer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly ForgeAuditorService _auditorService;

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private static readonly string _homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly Dictionary<string, string> _memoryDirMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmsx"]          = Path.Combine(_homeDir, ".claude", "projects", "T--Developer-RepositorioTrabalho-tecbakana-cmsx",       "memory"),
        ["multiplai"]     = Path.Combine(_homeDir, ".claude", "projects", "T--Developer-RepositorioTrabalho-tecbakana-cmsx",       "memory"),
        ["salematic"]     = Path.Combine(_homeDir, ".claude", "projects", "T--Developer-salematic",                                "memory"),
        ["forge"]         = Path.Combine(_homeDir, ".claude", "projects", "T--Developer-RepositorioTrabalho-tecbakana-ForgeV2",    "memory"),
        ["devautomation"] = Path.Combine(_homeDir, ".claude", "projects", "T--Developer-RepositorioTrabalho-tecbakana-ForgeV2",    "memory"),
    };

    public OrchestratorService(
        IConfiguration config,
        IHubContext<OrchestratorHub> hub,
        ILogger<OrchestratorService> logger,
        RagService rag,
        RagIndexerService indexer,
        ForgeAuditorService auditorService)
    {
        DevRequestsDir      = config["DevAutomation:DevRequestsDir"]!;
        _claudePath         = config["DevAutomation:ClaudePath"] ?? "claude";
        _globalClaudeMdPath = config["DevAutomation:GlobalClaudeMdPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
        _hub     = hub;
        _logger  = logger;
        _rag     = rag;
        _indexer = indexer;
        _auditorService = auditorService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(DevRequestsDir);

        _watcher = new FileSystemWatcher(DevRequestsDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;

        _logger.LogInformation("Orquestrador monitorando: {Dir}", DevRequestsDir);

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
        foreach (var file in Directory.GetFiles(DevRequestsDir, "*.json"))
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
        // 1. Inicia o TRACE macro no Langfuse para esta execução
        using Activity? trace = ForgeSource.StartActivity("DispatchAgentPipeline");
        if (trace != null)
        {
            // Vincula as tags de negócio do Forge ao Langfuse
            trace.SetTag("langfuse.trace.id", request.Id);
            trace.SetTag("langfuse.session.id", request.Api ?? "global");
            trace.SetTag("gen_ai.prompt", request.Descricao);
            trace.SetTag("forge.request.type", request.Tipo);
        }

        request.Status = "in_progress";
        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);

        var targetDir = request.DiretorioAlvo ?? "T:\\devautomation";

        var contratos = await DecomporAsync(request, targetDir);
        if (contratos != null)
        {
            request.Contratos = contratos;
            var camadasAfetadas = CamadasOrdenadas(contratos).ToList();
            if (camadasAfetadas.Count > 1)
            {
                _logger.LogInformation("[Orquestrador] {N} camadas para {Id} — modo multi-camada", camadasAfetadas.Count, request.Id);
                await DispatchMultiCamadaAsync(request, filePath, contratos);
                return;
            }
        }

        var prompt    = request.PromptAgente
            ?? (!string.IsNullOrEmpty(request.Detalhes)
                ? $"{request.Descricao}\n\n{request.Detalhes}"
                : request.Descricao);

        // Se houver resposta do usuário a um impedimento anterior, inclui contexto
        if (!string.IsNullOrEmpty(request.Pendencias) && !string.IsNullOrEmpty(request.RespostaUsuario))
        {
            prompt = $"{prompt}\n\n--- CONTEXTO ADICIONAL ---\nVocê havia solicitado esclarecimentos: {request.Pendencias}\nResposta do usuário: {request.RespostaUsuario}";
        }

        // Se houver considerações de refazer, prepend com instrução explícita
        if (!string.IsNullOrEmpty(request.ConsideracoesRefazer))
        {
            prompt = $"ATENÇÃO — REFAZER: A implementação anterior foi rejeitada pelo responsável.\nConsiderações obrigatórias para esta nova tentativa:\n{request.ConsideracoesRefazer}\n\nLeve as considerações acima em conta antes de qualquer implementação.\n\n---\n\n{prompt}";
            request.ConsideracoesRefazer = null; // limpa após usar
        }

        // Busca contexto RAG filtrado pelo projeto da dev-request
        var projeto = request.Api?.ToLowerInvariant() switch
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
        prompt = """
REGRAS OBRIGATÓRIAS — LEIA ANTES DE QUALQUER AÇÃO:

1. IMPEDITIVO: Se precisar de qualquer informação antes de implementar, ou houver ambiguidade que impeça a implementação segura, responda SOMENTE com este JSON — sem texto antes, sem texto depois, sem markdown:
{"impeditivo": true, "pendencias": "descreva suas dúvidas aqui"}

2. MAPEAMENTO PRÉVIO: Antes de escrever qualquer linha de código, liste explicitamente no output: (a) arquivos de backend a criar/modificar, (b) rotas/endpoints a expor e (c) arquivos de UI a criar/modificar. Se a spec não permitir mapear os três pilares com clareza, sinalize impeditivo antes de implementar.

3. TESTE E2E PRIMEIRO: Crie o arquivo de teste de integração ou E2E que simule a interação do usuário com a funcionalidade ANTES de qualquer implementação de feature. A task só está concluída quando esse teste passar.

4. TRÍADE OBRIGATÓRIA: Toda entrega deve conter os três pilares: (a) Lógica de Backend, (b) Rota/Endpoint de API e (c) Componente de Interface de Usuário com ponto de entrada navegável. Implementar lógica de negócio sem UI correspondente é violação bloqueante — não considere a task concluída se qualquer pilar estiver ausente.

5. SPEC COMPLETA: Implemente TODOS os itens descritos nos detalhes da task — cada endpoint, campo, migration, interface, repositório e componente UI listado. Antes de concluir, revise os detalhes item a item e confirme que nada foi omitido. Omitir qualquer item especificado é violação bloqueante.

6. ISOLAMENTO DE ESCOPO: Modifique APENAS arquivos diretamente necessários para esta task. Se encontrar débito técnico em arquivos fora do escopo, registre no resultado como aviso mas NÃO corrija. Alterar arquivos fora do escopo introduz risco de regressão e será tratado como violação.

---

""" + prompt;

        if (!string.IsNullOrEmpty(contextoRag))
        {
            prompt = contextoRag + "\n\n---\n\n" + prompt;
            _logger.LogInformation("[RAG] Contexto injetado para dev-request {Id}: {N} chunks do projeto {Projeto}",
                request.Id, ragChunks.Count, projeto is { Length: > 0 } ? string.Join(",", projeto) : "todos");
        }

        var agentModel = request.Impacto?.ToLowerInvariant() switch
        {
            "alto"  => "claude-sonnet-4-6",
            "medio" => "claude-sonnet-4-6",
            _       => "claude-sonnet-4-6"
        };

        try
        {
            var psi = BuildClausePsi($"--dangerously-skip-permissions --print --model \"{agentModel}\"", targetDir);

            // 2. Cria o SPAN de Geração da LLM para capturar latência e saídas
            using Activity? generationSpan = ForgeSource.StartActivity("Claude_CLI_Execution");
            if (generationSpan != null)
            {
                generationSpan.SetTag("gen_ai.system", "anthropic");
                generationSpan.SetTag("gen_ai.request.model", agentModel);
            }

            using var process = Process.Start(psi)!;
            _runningProcesses[request.Id] = process;

            var stdinTask  = Task.Run(async () => { await process.StandardInput.WriteAsync(prompt); process.StandardInput.Close(); });
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask  = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdinTask, outputTask, errorTask);
            var output = await outputTask;
            var error  = await errorTask;
            await process.WaitForExitAsync();

            _runningProcesses.TryRemove(request.Id, out _);

            // Registra o desfecho da execução do binário na telemetria
            if (generationSpan != null)
            {
                generationSpan.SetTag("gen_ai.output", output);
                if (process.ExitCode != 0)
                {
                    generationSpan.SetTag("gen_ai.error", error);
                    generationSpan.SetStatus(ActivityStatusCode.Error, $"Exit code {process.ExitCode}");
                }
            }

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
            else if (process.ExitCode != 0)
            {
                request.Status    = "error";
                request.Resultado = !string.IsNullOrWhiteSpace(error) ? error
                    : !string.IsNullOrWhiteSpace(output) ? output
                    : $"Processo encerrou com exit code {process.ExitCode} sem output.";
            }
            else
            {
                request.Resultado = output;
                var testResult = await RunIntegrationTestsAsync(targetDir);

                // 3. Portão de Qualidade: Testes de Integração
                using (Activity? testSpan = ForgeSource.StartActivity("RunIntegrationTests"))
                {
                    // ── Testes de integração automáticos ─────────────────────────
                    testSpan?.SetTag("forge.tests.output", testResult.Output);
                    if (!testResult.Passed)
                    {
                        request.Status = "error";
                        request.Resultado = $"{output}\n\n--- TESTES DE INTEGRAÇÃO FALHARAM ---\n{testResult.Output}";
                        testSpan?.SetStatus(ActivityStatusCode.Error, "Testes de integração falharam.");
                        trace?.SetStatus(ActivityStatusCode.Error, "Pipeline rejeitado pelos testes automáticos.");
                        goto saveAndNotify;
                    }
                }

                // ── Vulnerabilidades de pacotes ───────────────────────────────
                var vulnOutput = await CheckVulnerabilitiesAsync(targetDir);

                await ReviewAsync(request, filePath, testResult.Output, vulnOutput);
                trace?.SetTag("forge.pipeline.result", "encaminhado_para_revisao");
                return; // ReviewAsync conclui save/notify
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningProcesses.TryRemove(request.Id, out _);
            trace?.SetStatus(ActivityStatusCode.Error, ex.Message);

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

        saveAndNotify:
        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);
    }

    // ── ORQUESTRAÇÃO MULTI-CAMADA ─────────────────────────────────────────────

    private async Task<ContratosCamada?> DecomporAsync(DevRequest request, string targetDir)
    {
        _logger.LogInformation("[Decomp] Decompondo dev-request {Id} em contratos de camada", request.Id);
        var prompt = BuildDecompositionPrompt(request);
        var psi    = BuildClausePsi("--dangerously-skip-permissions --print --model \"claude-sonnet-4-6\"", targetDir);

        try
        {
            using var proc = Process.Start(psi)!;
            var stdinTask  = Task.Run(async () => { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); });
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(stdinTask, outputTask);
            var output = await outputTask;
            await proc.WaitForExitAsync();

            var contratos = TryParseContratos(output);
            if (contratos != null)
            {
                _logger.LogInformation("[Decomp] schema={S} repo={R} api={A} front={F}",
                    contratos.Schema?.Afetada, contratos.Repositorio?.Afetada,
                    contratos.Api?.Afetada, contratos.Frontend?.Afetada);
            }
            return contratos;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Decomp] Falha — agente único como fallback.");
            return null;
        }
    }

    private static string BuildDecompositionPrompt(DevRequest request) => $$"""
        Analise a tarefa abaixo e decomponha-a em contratos por camada da arquitetura N-Tier.

        REGRA OBRIGATÓRIA: Responda SOMENTE com JSON válido — sem texto antes, sem texto depois, sem markdown, sem blocos de código.

        Camadas:
        - schema: entidades EF Core, migrations, DbContext
        - repositorio: CMSXRepo, interfaces ICMSX, acesso a dados
        - api: controllers ASP.NET Core, DTOs de entrada/saída
        - frontend: componentes Angular, templates HTML, services HTTP

        Para cada camada:
        - afetada: true se precisa ser modificada
        - escopo: o que deve ser feito nessa camada (string vazia se não afetada)
        - artefatos: arquivos/classes a criar ou modificar (array vazio se não afetada)
        - expoe: o que esta camada entrega à próxima (interfaces, endpoints, contratos — string vazia se não afetada)

        === TAREFA ===
        API: {{request.Api}}
        Tipo: {{request.Tipo}}
        Descrição: {{request.Descricao}}
        Detalhes: {{request.Detalhes ?? "(sem detalhes)"}}

        Schema de resposta:
        {
          "schema":      { "afetada": bool, "escopo": "", "artefatos": [], "expoe": "" },
          "repositorio": { "afetada": bool, "escopo": "", "artefatos": [], "expoe": "" },
          "api":         { "afetada": bool, "escopo": "", "artefatos": [], "expoe": "" },
          "frontend":    { "afetada": bool, "escopo": "", "artefatos": [], "expoe": "" }
        }
        """;

    private static ContratosCamada? TryParseContratos(string output)
    {
        try
        {
            var start = output.IndexOf('{');
            var end   = output.LastIndexOf('}');
            if (start < 0 || end < 0 || end <= start) return null;
            return JsonSerializer.Deserialize<ContratosCamada>(output[start..(end + 1)]);
        }
        catch { return null; }
    }

    private static IEnumerable<(ContratoCamada Camada, string Nome, string? ContextoAnterior)> CamadasOrdenadas(ContratosCamada contratos)
    {
        string? expoeAnterior = null;
        foreach (var (camada, nome) in new (ContratoCamada?, string)[]
        {
            (contratos.Schema,      "schema"),
            (contratos.Repositorio, "repositorio"),
            (contratos.Api,         "api"),
            (contratos.Frontend,    "frontend")
        })
        {
            if (camada?.Afetada == true)
            {
                yield return (camada, nome, expoeAnterior);
                if (!string.IsNullOrEmpty(camada.Expoe))
                    expoeAnterior = camada.Expoe;
            }
        }
    }

    private async Task<(bool Success, string Output, bool IsImpeditivo, string? Pendencias)> DispatchCamadaAsync(
        DevRequest request, ContratoCamada camada, string nome, string? contextoAnterior, string targetDir)
    {
        var projeto = request.Api?.ToLowerInvariant() switch
        {
            "cmsx" or "multiplai" => new[] { "cmsx" },
            "salematic"           => new[] { "salematic" },
            "forge"               => new[] { "forge" },
            _                     => null
        };

        var queryRag    = (request.Descricao + " " + camada.Escopo).Trim();
        var contextoRag = await _rag.BuildContextAsync(queryRag, topK: 5, filtrosProjeto: projeto);

        var prompt = BuildLayerPrompt(request, camada, nome, contextoAnterior, contextoRag);
        var psi    = BuildClausePsi("--dangerously-skip-permissions --print --model \"claude-sonnet-4-6\"", targetDir);

        var chaveProcesso = $"{request.Id}_{nome}";
        try
        {
            using var proc = Process.Start(psi)!;
            _runningProcesses[chaveProcesso] = proc;

            var stdinTask  = Task.Run(async () => { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); });
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask  = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdinTask, outputTask, errorTask);
            var output = await outputTask;
            var error  = await errorTask;
            await proc.WaitForExitAsync();

            _runningProcesses.TryRemove(chaveProcesso, out _);

            if (TryParseImpeditivo(output, out var pendencias))
                return (false, output, true, pendencias);

            if (proc.ExitCode != 0)
                return (false, !string.IsNullOrWhiteSpace(error) ? error : output, false, null);

            return (true, output, false, null);
        }
        catch (Exception ex)
        {
            _runningProcesses.TryRemove(chaveProcesso, out _);
            return (false, ex.Message, false, null);
        }
    }

    private static string BuildLayerPrompt(DevRequest request, ContratoCamada camada, string nome, string? contextoAnterior, string contextoRag)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(contextoRag))
            sb.AppendLine(contextoRag).AppendLine().AppendLine("---").AppendLine();

        sb.AppendLine("""
            REGRAS OBRIGATÓRIAS — LEIA ANTES DE QUALQUER AÇÃO:

            1. IMPEDITIVO: Se precisar de informação antes de implementar, responda SOMENTE com este JSON — sem texto antes, sem texto depois, sem markdown:
            {"impeditivo": true, "pendencias": "descreva suas dúvidas aqui"}

            2. MAPEAMENTO PRÉVIO: Antes de escrever qualquer linha de código, liste explicitamente os arquivos que serão criados ou modificados nesta camada. Se a spec não permitir esse mapeamento, sinalize impeditivo.

            3. ESCOPO ESTRITO: Você é responsável EXCLUSIVAMENTE pela sua camada. Não modifique arquivos de outras camadas.

            4. SPEC COMPLETA: Implemente TODOS os itens listados em artefatos e escopo. Nenhuma omissão é aceitável.

            5. CAMADA FRONTEND — ACOPLAMENTO VISUAL OBRIGATÓRIO: Se sua camada inclui componentes de UI, a tarefa só está concluída quando existir um ponto de entrada navegável (rota, botão, menu) que exponha a funcionalidade ao usuário. Componente sem ponto de entrada na navegação é violação bloqueante.

            ---
            """);

        sb.AppendLine("## TAREFA");
        sb.AppendLine(request.Descricao);
        if (!string.IsNullOrEmpty(request.Detalhes))
            sb.AppendLine(request.Detalhes);

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## SUA CAMADA: {nome.ToUpperInvariant()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Escopo:** {camada.Escopo}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Artefatos a criar/modificar:** {string.Join(", ", camada.Artefatos)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Expõe para a próxima camada:** {camada.Expoe}");

        if (!string.IsNullOrEmpty(contextoAnterior))
        {
            sb.AppendLine();
            sb.AppendLine("## CONTRATO DA CAMADA ANTERIOR");
            sb.AppendLine(contextoAnterior);
            sb.AppendLine("Use estas definições como entrada — não as reimplemente, apenas consuma.");
        }

        return sb.ToString();
    }

    private async Task DispatchMultiCamadaAsync(DevRequest request, string filePath, ContratosCamada contratos)
    {
        // Abre o Trace macro da execução multi-camada
        using Activity? multiTrace = ForgeSource.StartActivity("MultiLayerPipeline");
        if (multiTrace != null)
        {
            multiTrace.SetTag("langfuse.trace.id", request.Id);
            multiTrace.SetTag("langfuse.session.id", request.Api ?? "global");
        }
        var sb = new StringBuilder();

        foreach (var (camada, nome, contextoAnterior) in CamadasOrdenadas(contratos))
        {
            // Verifica cancelamento entre camadas
            try
            {
                var snap = JsonSerializer.Deserialize<DevRequest>(await File.ReadAllTextAsync(filePath));
                if (snap?.Status == "cancelado")
                {
                    _logger.LogInformation("[Orquestrador] Dev-request {Id} cancelada entre camadas.", request.Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orquestrador] Falha ao verificar cancelamento de {Id} entre camadas — continuando.", request.Id);
            }

            _logger.LogInformation("[Orquestrador] [{Nome}] executando para {Id}", nome, request.Id);

            // Inicia um SPAN específico para o processamento da camada atual (ex: schema, api, frontend)
            using Activity? layerSpan = ForgeSource.StartActivity($"ExecuteLayer_{nome}");
            layerSpan?.SetTag("forge.layer.name", nome);

            var (success, output, isImpeditivo, pendencias) = await DispatchCamadaAsync(
                request, camada, nome, contextoAnterior, request.DiretorioAlvo ?? ".");

            layerSpan?.SetTag("gen_ai.output", output);

            if (isImpeditivo)
            {
                request.Status     = "impeditivo";
                request.Pendencias = $"[{nome.ToUpperInvariant()}] {pendencias}";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(filePath, request);
                await NotifyAsync(request);

                layerSpan?.SetStatus(ActivityStatusCode.Error, "Execução interrompida por impeditivo.");
                multiTrace?.SetTag("forge.pipeline.result", $"impeditivo_na_camada_{nome}");

                return;
            }

            if (!success)
            {
                request.Status    = "error";
                request.Resultado = $"[{nome.ToUpperInvariant()}] {output}";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(filePath, request);
                await NotifyAsync(request);

                layerSpan?.SetStatus(ActivityStatusCode.Error, $"Falha de execução na camada: {output}");
                multiTrace?.SetStatus(ActivityStatusCode.Error, $"Pipeline abortado devido a erro na camada {nome}");

                return;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"=== CAMADA: {nome.ToUpperInvariant()} ===");
            sb.AppendLine(output);
            sb.AppendLine();
        }

        request.Resultado = sb.ToString();
        var testResult = await RunIntegrationTestsAsync(request.DiretorioAlvo ?? ".");
        // Execução do portão de testes pós-camadas
        using (Activity? testSpan = ForgeSource.StartActivity("MultiLayer_IntegrationTests"))
        {
            if (!testResult.Passed)
            {
                request.Status = "error";
                request.Resultado += $"\n\n--- TESTES DE INTEGRAÇÃO FALHARAM ---\n{testResult.Output}";
                request.TimestampAtualizacao = DateTime.UtcNow;
                await SaveAsync(filePath, request);
                await NotifyAsync(request);

                testSpan?.SetStatus(ActivityStatusCode.Error, "Testes automáticos falharam pós-integração das camadas.");
                multiTrace?.SetStatus(ActivityStatusCode.Error, "Código multi-camadas rejeitado no portão de testes.");

                return;
            }
        }
        var vulnOutput = await CheckVulnerabilitiesAsync(request.DiretorioAlvo ?? ".");
        await ReviewAsync(request, filePath, testResult.Output, vulnOutput);
    }

    private async Task ReviewAsync(DevRequest request, string filePath, string? testOutput = null, string? vulnOutput = null)
    {
        _logger.LogInformation("[Review] Iniciando revisão automática para {Id}", request.Id);

        var gitDiff = await GetGitDiffAsync(request.DiretorioAlvo ?? ".");

        var globalRules = File.Exists(_globalClaudeMdPath)
            ? await File.ReadAllTextAsync(_globalClaudeMdPath)
            : "";

        var projectClaudeMd = Path.Combine(request.DiretorioAlvo ?? ".", "CLAUDE.md");
        var projectRules = File.Exists(projectClaudeMd)
            ? await File.ReadAllTextAsync(projectClaudeMd)
            : "";

        var prompt = BuildReviewPrompt(globalRules, projectRules, gitDiff, request, testOutput, vulnOutput);

        var psi = BuildClausePsi("--dangerously-skip-permissions --print --model \"claude-sonnet-4-6\"", request.DiretorioAlvo ?? ".");

        string reviewOutput;
        try
        {
            using var proc = Process.Start(psi)!;
            var reviewStdinTask  = Task.Run(async () => { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); });
            var reviewOutputTask = proc.StandardOutput.ReadToEndAsync();
            var reviewErrorTask  = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(reviewStdinTask, reviewOutputTask, reviewErrorTask);
            reviewOutput = await reviewOutputTask;
            await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Review] Falha ao executar revisor para {Id} — promovendo para em_testes", request.Id);
            request.Status = "em_testes";
            request.TimestampAtualizacao = DateTime.UtcNow;
            await SaveAsync(filePath, request);
            await NotifyAsync(request);
            return;
        }

        request.ResultadoRevisao = reviewOutput;

        if (TryParseReviewResult(reviewOutput, out var aprovado, out var temAvisos, out var urlTeste))
        {
            request.Status   = aprovado ? (temAvisos ? "revisao_amarela" : "em_testes") : "revisao_reprovada";
            request.UrlTeste = urlTeste;
            if (aprovado)
                await GitCommitAsync(request, request.DiretorioAlvo ?? ".");
            _logger.LogInformation("[Review] dev-request {Id} — {Status}", request.Id, request.Status);
        }
        else
        {
            _logger.LogWarning("[Review] Resposta não estruturada para {Id} — promovendo para em_testes", request.Id);
            request.Status = "em_testes";
        }

        request.TimestampAtualizacao = DateTime.UtcNow;
        await SaveAsync(filePath, request);
        await NotifyAsync(request);
    }

    private static async Task<string> GetGitDiffAsync(string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "diff HEAD")
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            var diff = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return diff.Length > 20_000 ? diff[..20_000] + "\n[diff truncado em 20k chars]" : diff;
        }
        catch
        {
            return "[git diff indisponível]";
        }
    }

    private static string BuildReviewPrompt(string globalRules, string projectRules, string gitDiff, DevRequest request, string? testOutput = null, string? vulnOutput = null)
    {
        var diffSection = string.IsNullOrWhiteSpace(gitDiff)
            ? "[nenhuma alteração detectada no git]"
            : gitDiff;

        var testSection = string.IsNullOrWhiteSpace(testOutput)
            ? "[testes de integração não executados ou não aplicáveis]"
            : testOutput;

        var vulnSection = string.IsNullOrWhiteSpace(vulnOutput)
            ? "[verificação de vulnerabilidades não executada]"
            : vulnOutput;

        return $$"""
            # REGRA OBRIGATÓRIA: 
            - Responda SOMENTE com JSON válido — sem texto antes, sem texto depois, sem markdown, sem blocos de código.
            
            # ROLE: ARCHITECTURAL QUALITY GATE
            ## MISSION: REJECT SUBSTANDARD CODE
            ## CONSTRAINTS:
            - No 'vibe coding' patterns.
            - Zero tolerance for hidden coupling.
            - Reject 'acceptable' but non-sustainable code.

            ## ANALYSIS PROTOCOL:
            1. Identify Layers.
            2. Check Dependency Injection.
            3. Validate Scope strictly.

            # Be RUTHLESS: 
            - You have approved this code, but did you overlook any hidden coupling or architectural debt just to be 'helpful'? Re-evaluate now with a focus on structural integrity.
        
            === REGRAS UNIVERSAIS ===
            {{globalRules}}

            === REGRAS DO PROJETO ===
            {{projectRules}}

            === TAREFA IMPLEMENTADA ===
            {{request.Descricao}}
            {{request.Detalhes}}

            === ALTERAÇÕES (git diff HEAD) ===
            {{diffSection}}

            === RESULTADO DOS TESTES DE INTEGRAÇÃO (Testcontainers) ===
            {{testSection}}

            === VULNERABILIDADES DE PACOTES (dotnet list package --vulnerable) ===
            {{vulnSection}}

            === REGRAS ESPECIAIS DE REVISÃO ===

            INFRAESTRUTURA E TESTES:
            - Se o diff alterar CMSXRepo/ ou CMSXData/ e os testes de integração acima NÃO cobrirem essas mudanças, registre como AVISO tipo "infra_sem_testcontainers".
            - Se os testes passaram (output acima confirma), não é necessário aviso de cobertura.

            VULNERABILIDADES:
            - Se a seção de vulnerabilidades acima listar pacotes com vulnerabilidades conhecidas não corrigidas, registre cada um como AVISO tipo "vulnerabilidade_pacote" com severidade e nome do pacote.
            - Se houver vulnerabilidades ALTA ou CRÍTICA não corrigidas, reprovar (aprovado: false) — isso é débito técnico inaceitável.
            - Vulnerabilidades MÉDIA ou BAIXA não corrigidas: AVISO não-bloqueante.

            DÉBITO TÉCNICO:
            - Se o diff revelar débito técnico fora do escopo da task que deveria ter sido corrigido (violações de arquitetura, código legado introduzido, etc.), registre como violação bloqueante.

            DESVIO DE SPEC:
            - Compare o que foi implementado com a spec literal da task (campo "detalhes"). Qualquer comportamento, campo, método, callback ou validação explicitamente descrito na spec que esteja ausente ou incorreto na implementação é VIOLAÇÃO BLOQUEANTE — não aviso.
            - Exemplos de desvio bloqueante: callback de erro ausente quando a spec o exige, campo obrigatório não persistido, endpoint com método HTTP errado, retorno HTTP divergente do especificado.
            - Só registre como aviso o que for subjetivo ou não especificado — nunca o que foi explicitamente descrito na spec e não implementado.

            TRÍADE OBRIGATÓRIA:
            - Verifique se o diff cobre os três pilares: (a) lógica de backend (serviço, repositório, handler), (b) rota ou endpoint de API exposto (controller, hub, minimal API) e (c) componente ou rota navegável de UI (Angular, Razor, HTML, JS).
            - Se qualquer pilar estiver ausente E a spec (campo "detalhes" acima) não justificar explicitamente a omissão, registre como VIOLAÇÃO BLOQUEANTE com tipo "triade_incompleta" e indique qual pilar está faltando.
            - Omissão justificada: task exclusivamente de schema/migration sem exposição de endpoint novo; refactor interno sem nova funcionalidade. Nesses casos, registre como aviso "triade_parcial_justificada" com a justificativa.

            SEGURANÇA (OWASP API):
            - Se o diff adicionar ou modificar endpoints que aceitam query parameters com IDs externos (usuarioid, aplicacaoid, ou qualquer ID de entidade passado por query string), verificar se há gate de autorização (ex: verificação de acessoTotal ou claim equivalente) ANTES do uso do parâmetro. Endpoint sem gate é BOLA/IDOR — VIOLAÇÃO BLOQUEANTE.
            - Se o diff adicionar ou modificar método de remoção (RemoverAsync, Delete) em entidade que possui relações em outras tabelas, verificar se há remoção explícita dos registros relacionados ou se CASCADE DELETE está confirmado no schema. Ausência é AVISO obrigatório tipo "cascade_orfao".

            === SCHEMA DE RETORNO ===
            O campo "url_teste" é obrigatório: infira o caminho HTTP relativo (ex: "/aplicacaos", "/orcamentos") que o usuário deve acessar para testar manualmente a funcionalidade implementada. Se não houver endpoint HTTP associado, use null.

            Aprovado sem ressalvas:
            {
              "aprovado": true,
              "resumo": "o que foi implementado e avaliação geral",
              "url_teste": "/aplicacaos",
              "violacoes": [],
              "avisos": []
            }
            Aprovado com atenção:
            {
              "aprovado": true,
              "resumo": "avaliação geral",
              "url_teste": "/aplicacaos",
              "violacoes": [],
              "avisos": [
                {
                  "tipo": "infra_sem_testcontainers | vulnerabilidade_pacote | outro",
                  "mensagem": "descrição detalhada",
                  "arquivos": ["caminho/arquivo.cs"]
                }
              ]
            }
            Reprovado (violação bloqueante):
            {
              "aprovado": false,
              "resumo": "avaliação geral",
              "url_teste": "/aplicacaos",
              "violacoes": [
                {
                  "regra": "texto exato da regra violada",
                  "arquivo": "caminho/do/arquivo.cs",
                  "linha": 42,
                  "encontrado": "trecho de código que viola a regra",
                  "esperado": "como deveria ser escrito"
                }
              ],
              "avisos": []
            }
            """;
    }

    private static bool TryParseReviewResult(string output, out bool aprovado, out bool temAvisos, out string? urlTeste)
    {
        aprovado  = false;
        temAvisos = false;
        urlTeste  = null;
        try
        {
            var start = output.IndexOf('{');
            var end   = output.LastIndexOf('}');
            if (start < 0 || end < 0 || end <= start) return false;

            var doc = JsonSerializer.Deserialize<JsonElement>(output[start..(end + 1)]);
            if (doc.TryGetProperty("aprovado", out var prop) &&
                (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
            {
                aprovado  = prop.GetBoolean();
                temAvisos = doc.TryGetProperty("avisos", out var avisos)
                            && avisos.ValueKind == JsonValueKind.Array
                            && avisos.GetArrayLength() > 0;
                if (doc.TryGetProperty("url_teste", out var urlTesteProp) && urlTesteProp.ValueKind == JsonValueKind.String)
                    urlTeste = urlTesteProp.GetString();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static async Task SaveAsync(string filePath, DevRequest request)
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
        var file = Directory.GetFiles(DevRequestsDir, "*.json")
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
                await NotifyAsync(request);
                _ = Task.Run(() => PostDoneAsync(request));
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
                _ = Task.Run(() => PostDoneAsync(request));
                break;
            case "aceitar_aviso":
                request.Status = "em_testes";
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
            var start = output.LastIndexOf("{\"impeditivo\"", StringComparison.Ordinal);
            if (start < 0) start = output.LastIndexOf("{ \"impeditivo\"", StringComparison.Ordinal);
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

    private ProcessStartInfo BuildClausePsi(string claudeArgs, string workingDir) =>
        new ProcessStartInfo
        {
            FileName               = _claudePath,
            Arguments              = claudeArgs,
            WorkingDirectory       = workingDir,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

    private async Task<(bool Passed, string Output)> RunIntegrationTestsAsync(string workingDir)
    {
        // Procura projeto de testes na solução
        var testProj = Directory.GetFiles(workingDir, "*.Tests.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

        if (testProj is null)
        {
            _logger.LogInformation("[Testes] Nenhum projeto *.Tests.csproj encontrado em {Dir} — pulando.", workingDir);
            return (true, "");
        }

        _logger.LogInformation("[Testes] Executando testes de integração: {Proj}", testProj);

        try
        {
            var psi = new ProcessStartInfo("dotnet", $"test \"{testProj}\" --filter \"Integration\" --no-build --logger \"console;verbosity=normal\"")
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask);
            var output = await outTask + await errTask;
            await proc.WaitForExitAsync();

            var passed = proc.ExitCode == 0;
            _logger.LogInformation("[Testes] Resultado: {Status} (exit {Code})", passed ? "PASSOU" : "FALHOU", proc.ExitCode);
            return (passed, output.Length > 10_000 ? output[..10_000] + "\n[truncado]" : output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Testes] Falha ao executar dotnet test — ignorando step.");
            return (true, $"[erro ao executar testes: {ex.Message}]");
        }
    }

    private async Task<string> CheckVulnerabilitiesAsync(string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "list package --vulnerable --include-transitive")
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)!;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask);
            var output = await outTask;
            await proc.WaitForExitAsync();

            _logger.LogInformation("[Vulnerabilidades] Verificação concluída.");
            return output.Length > 5_000 ? output[..5_000] + "\n[truncado]" : output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Vulnerabilidades] Falha ao verificar pacotes.");
            return $"[erro ao verificar vulnerabilidades: {ex.Message}]";
        }
    }

    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");

    // ── GIT COMMIT POR TASK ───────────────────────────────────────────────────

    private async Task GitCommitAsync(DevRequest request, string workingDir)
    {
        try
        {
            var msg = $"{request.Id}: {request.Descricao}";
            var addPsi = new ProcessStartInfo("git", "add -A")
            {
                WorkingDirectory       = workingDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using (var p = Process.Start(addPsi)!) await p.WaitForExitAsync();

            var commitPsi = new ProcessStartInfo("git", $"commit -m \"{EscapeArg(msg)}\"")
            {
                WorkingDirectory       = workingDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var commit = Process.Start(commitPsi)!;
            await commit.WaitForExitAsync();
            _logger.LogInformation("[Git] Commit {Id}: exit {Code}", request.Id, commit.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Git] Falha ao criar commit para {Id} — continuando.", request.Id);
        }
    }

    // ── PÓS-DONE ──────────────────────────────────────────────────────────────

    private async Task PostDoneAsync(DevRequest request)
    {
        _logger.LogInformation("[PostDone] Iniciando para {Id} ({Api})", request.Id, request.Api);

        try
        {
            var api = request.Api?.ToLowerInvariant() ?? "";
            if (_memoryDirMap.TryGetValue(api, out var memoryDir))
            {
                Directory.CreateDirectory(memoryDir);

                var gitDiff = await GetGitDiffAsync(request.DiretorioAlvo ?? ".");
                var prompt  = BuildMemoryPrompt(request, gitDiff);
                var psi     = BuildClausePsi("--dangerously-skip-permissions --print --model \"claude-sonnet-4-6\"",
                                             request.DiretorioAlvo ?? ".");

                string output;
                using (var proc = Process.Start(psi)!)
                {
                    var stdinTask  = Task.Run(async () => { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); });
                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    await Task.WhenAll(stdinTask, outputTask);
                    output = await outputTask;
                    await proc.WaitForExitAsync();
                }

                if (TryParseMemoryOutput(output, out var filename, out var indexEntry, out var content))
                {
                    var filePath = Path.Combine(memoryDir, filename!);
                    await File.WriteAllTextAsync(filePath, content!);
                    await AppendToMemoryIndexAsync(memoryDir, indexEntry!);
                    _logger.LogInformation("[PostDone] Memória gravada: {Path}", filePath);
                }
                else
                {
                    _logger.LogWarning("[PostDone] Falha ao parsear saída de memória para {Id}", request.Id);
                }
            }
            else
            {
                _logger.LogInformation("[PostDone] Sem mapeamento de memória para api '{Api}'", api);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PostDone] Falha na geração de memória para {Id}", request.Id);
        }
        finally
        {
            try
            {
                await _indexer.ReindexIncrementalAsync();
                _logger.LogInformation("[PostDone] Reindexação incremental concluída para {Id}", request.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PostDone] Falha na reindexação RAG para {Id}", request.Id);
            }
        }
    }

    private static string BuildMemoryPrompt(DevRequest request, string gitDiff) => $$"""
        Gere uma entrada de memória para o sistema Claude Code Memory.

        REGRA OBRIGATÓRIA: Responda SOMENTE com JSON válido — sem texto antes, sem texto depois, sem markdown, sem blocos de código.
        No campo "content", represente quebras de linha como \n dentro da string JSON.

        Schema:
        {
          "filename": "project_<slug-da-descricao>.md",
          "index_entry": "- [Titulo curto](project_<slug>.md) — resumo em uma linha (máx 120 chars)",
          "content": "---\nname: <titulo>\ndescription: <uma linha>\ntype: project\n---\n\n<corpo>\n"
        }

        Regras para o corpo:
        - Lead with the fact/decision
        - **Why:** motivação da mudança
        - **How to apply:** quando levar em conta em sessões futuras
        - Máximo 200 palavras
        - Foco no não-derivável do código: decisões, padrões, constraints

        === DEV-REQUEST ===
        API: {{request.Api}}
        Tipo: {{request.Tipo}}
        Impacto: {{request.Impacto}}
        Descrição: {{request.Descricao}}
        Detalhes: {{request.Detalhes ?? "(sem detalhes)"}}

        === GIT DIFF ===
        {{(gitDiff.Length > 3000 ? gitDiff[..3000] + "\n[truncado]" : gitDiff)}}
        """;

    private static bool TryParseMemoryOutput(string output, out string? filename, out string? indexEntry, out string? content)
    {
        filename = indexEntry = content = null;
        try
        {
            var start = output.IndexOf('{');
            var end   = output.LastIndexOf('}');
            if (start < 0 || end < 0 || end <= start) return false;

            var doc  = JsonSerializer.Deserialize<JsonElement>(output[start..(end + 1)]);
            filename   = doc.TryGetProperty("filename",    out var fn) ? fn.GetString() : null;
            indexEntry = doc.TryGetProperty("index_entry", out var ie) ? ie.GetString() : null;
            content    = doc.TryGetProperty("content",     out var co) ? co.GetString() : null;

            return !string.IsNullOrWhiteSpace(filename)
                && !string.IsNullOrWhiteSpace(indexEntry)
                && !string.IsNullOrWhiteSpace(content);
        }
        catch { return false; }
    }

    private static async Task AppendToMemoryIndexAsync(string memoryDir, string indexEntry)
    {
        var memoryMdPath = Path.Combine(memoryDir, "MEMORY.md");

        if (!File.Exists(memoryMdPath))
        {
            await File.WriteAllTextAsync(memoryMdPath, $"# Memory Index\n\n{indexEntry}\n");
            return;
        }

        var existing = await File.ReadAllTextAsync(memoryMdPath);

        // Extrai o filename do entry "- [Titulo](filename.md) — ..." para evitar duplicata
        var filenameInEntry = indexEntry.Split('(', ')').ElementAtOrDefault(1);
        if (filenameInEntry != null && existing.Contains(filenameInEntry)) return;

        await File.AppendAllTextAsync(memoryMdPath, indexEntry + "\n");
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public string DevRequestsDir { get; }

    // API pública: lista todas as dev-requests
    public IEnumerable<DevRequest> ListAll()
    {
        if (!Directory.Exists(DevRequestsDir)) return [];

        var all = Directory.GetFiles(DevRequestsDir, "*.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<DevRequest>(File.ReadAllText(f)); }
                catch { return null; }
            })
            .Where(r => r is not null)
            .Cast<DevRequest>()
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        var doneIds = all.Where(r => r.Status == "done").Select(r => r.Id).ToHashSet();
        foreach (var r in all)
            r.Bloqueado = r.Dependencias?.Any(d => !doneIds.Contains(d)) ?? false;

        return all;
    }
}
