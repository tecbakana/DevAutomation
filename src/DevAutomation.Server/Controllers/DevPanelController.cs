using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DevAutomation.Models;
using DevAutomation.Services;
using DevAutomation.Services.Store;
using Microsoft.AspNetCore.Mvc;

namespace DevAutomation.Controllers;

[ApiController]
[Route("api")]
public class DevPanelController : ControllerBase
{
    private readonly ConfigService _config;
    private readonly GeminiService _gemini;
    private readonly ClaudeService _claude;
    private readonly OrchestratorService _orchestrator;
    private readonly RagIndexerService _ragIndexer;
    private readonly RagService _ragService;
    private readonly FeatureFlags _featureFlags;
    private readonly IDevRequestStore _store;
    private readonly string _templatesDir;
    private readonly string _switchScript;
    private readonly ILogger<DevPanelController> _logger;
    private readonly IConfiguration _cfg;

    private static readonly JsonSerializerOptions _jsonWriteOpts = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _jsonReadCiOpts = new() { PropertyNameCaseInsensitive = true };


    public DevPanelController(
        ConfigService config,
        GeminiService gemini,
        ClaudeService claude,
        OrchestratorService orchestrator,
        RagIndexerService ragIndexer,
        RagService ragService,
        FeatureFlags featureFlags,
        IDevRequestStore store,
        IConfiguration cfg,
        ILogger<DevPanelController> logger)
    {
        _config       = config;
        _gemini       = gemini;
        _claude       = claude;
        _orchestrator = orchestrator;
        _ragIndexer   = ragIndexer;
        _ragService   = ragService;
        _featureFlags = featureFlags;
        _store        = store;
        _cfg          = cfg;
        _templatesDir = cfg["DevAutomation:TemplatesDir"]!;
        _switchScript = cfg["DevAutomation:SwitchScript"]!;
        _logger       = logger;
    }

    // ── HEALTH ────────────────────────────────────────────────────────────────

    [HttpGet("health")]
    public IActionResult Health() =>
        Ok(new { status = "ok", timestamp = DateTime.UtcNow.ToString("O") });

    // ── PLATFORM ──────────────────────────────────────────────────────────────

    [HttpGet("platform")]
    public IActionResult Platform()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux   = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isMacOS   = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        var os = isWindows ? "windows" : isLinux ? "linux" : isMacOS ? "macos" : "unknown";

        var wtAvailable = isWindows && IsCommandAvailable("wt");

        return Ok(new
        {
            os,
            features = new
            {
                visualStudio          = isWindows,
                serverPullConfig      = isWindows,
                browseDialog          = isWindows,
                startApps             = wtAvailable,
                restartServer         = true,
                ragEnabled            = _featureFlags.RagEnabled,
                orchestratorAvailable = _featureFlags.OrchestratorAvailable,
                devAgentGeminiEnabled = _featureFlags.DevAgentGeminiEnabled,
                devAgentClaudeEnabled = _featureFlags.DevAgentClaudeEnabled
            },
            services = new
            {
                qdrant    = _featureFlags.QdrantAvailable,
                ollama    = _featureFlags.OllamaAvailable,
                claudeCli = _featureFlags.OrchestratorAvailable
            }
        });
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments              = command,
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

    // ── CONFIG ────────────────────────────────────────────────────────────────

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var cfg = _config.LoadConfig();
        return Ok(cfg);
    }

    // ── STATUS ────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var cfg   = _config.LoadConfig();
        var state = _config.LoadState();
        var seen  = new HashSet<string>();
        var result = new List<object>();

        foreach (var api in cfg.Apis)
        {
            var branch = "?";
            if (!string.IsNullOrEmpty(api.GitRepo) && !seen.Contains(api.GitRepo))
            {
                branch = RunGit("rev-parse --abbrev-ref HEAD", api.GitRepo);
                seen.Add(api.GitRepo);
            }
            else if (seen.Contains(api.GitRepo))
            {
                branch = result.OfType<dynamic>().FirstOrDefault(r => r.name == api.Name)?.branch ?? "?";
            }

            result.Add(new
            {
                name   = api.Name,
                branch = branch.Trim(),
                client = state.TryGetValue(api.Name, out var c) ? c : "default"
            });
        }

        return Ok(result);
    }

    // ── SWITCH ────────────────────────────────────────────────────────────────

    [HttpPost("switch")]
    public IActionResult Switch([FromBody] SwitchRequest body)
    {
        var messages = new List<string>();
        var cfg      = _config.LoadConfig();

        var apiList = cfg.Apis.AsEnumerable();
        if (!string.IsNullOrEmpty(body.Api) && body.Api != "all")
        {
            var filter = body.Api.Split(',').Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            apiList = apiList.Where(a => filter.Contains(a.Name));
        }
        var apis = apiList.ToList();

        // Passo 1 — Fechar IDE (Windows only)
        if (body.CloseVisualStudio)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var p in Process.GetProcessesByName("devenv"))
                {
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(15000)) p.Kill();
                        messages.Add($"[VS] Fechado: {p.MainWindowTitle}");
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            else
            {
                messages.Add("[VS] Fechar IDE ignorado (não suportado nesta plataforma)");
            }
        }

        // Passo 2 — Git pull
        if (body.GitPull)
        {
            var seenRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var api in apis)
            {
                if (string.IsNullOrEmpty(api.GitRepo) || seenRepos.Contains(api.GitRepo)) continue;
                seenRepos.Add(api.GitRepo);

                if (!Directory.Exists(api.GitRepo))
                {
                    messages.Add($"[GIT] Repo não encontrado: {api.GitRepo}");
                    continue;
                }

                foreach (var a in cfg.Apis.Where(a => !string.IsNullOrEmpty(a.ConfigFile)))
                    RunGit($"checkout -- \"{a.ConfigFile}\"", api.GitRepo);

                RunGit("fetch origin", api.GitRepo);
                RunGit($"reset --hard origin/{body.Environment}", api.GitRepo);
                messages.Add($"[GIT] {api.GitRepo} → {body.Environment}");
            }
        }

        // Passo 3 — Aplicar configurações
        foreach (var api in apis)
        {
            if (string.IsNullOrEmpty(api.ConfigFile)) continue;

            var ext      = api.ConfigType == "json" ? "json" : "xml";
            var client   = body.Client ?? "default";
            var template = Path.Combine(_templatesDir, api.Name, body.Environment, $"{client}.{ext}");

            if (!System.IO.File.Exists(template))
            {
                var fallback = Path.Combine(_templatesDir, api.Name, body.Environment, $"default.{ext}");
                if (System.IO.File.Exists(fallback))
                {
                    template = fallback;
                    messages.Add($"[CONFIG] {api.Name}: usando template default");
                }
                else
                {
                    messages.Add($"[CONFIG] {api.Name}: template não encontrado — {template}");
                    continue;
                }
            }

            try
            {
                if (api.ConfigType == "json")
                    ApplyJsonConfig(api.ConfigFile, template);
                else
                    ApplyWebConfig(api.ConfigFile, template);

                messages.Add($"[CONFIG] {api.Name}: OK");
            }
            catch (Exception ex)
            {
                messages.Add($"[CONFIG] {api.Name}: ERRO — {ex.Message}");
            }
        }

        // Passo 4 — Abrir IDE
        if (body.OpenVisualStudio)
        {
            var seenSln = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var api in apis)
            {
                if (string.IsNullOrEmpty(api.SolutionPath) || seenSln.Contains(api.SolutionPath)) continue;
                seenSln.Add(api.SolutionPath);
                OpenIde(api.SolutionPath, messages);
            }
        }

        foreach (var api in apis)
            _config.SetState(api.Name, body.Client ?? "default");

        return Ok(new { success = true, messages });
    }

    private static void ApplyJsonConfig(string targetFile, string templateFile) =>
        System.IO.File.Copy(templateFile, targetFile, overwrite: true);

    private static void ApplyWebConfig(string targetFile, string templateFile)
    {
        var target   = new System.Xml.XmlDocument();
        var template = new System.Xml.XmlDocument();
        target.Load(targetFile);
        template.Load(templateFile);

        MergeXmlSection(target, template, "//appSettings",    "add", "key",  "value");
        MergeXmlSection(target, template, "//connectionStrings", "add", "name", "connectionString");

        var settings = new System.Xml.XmlWriterSettings
        {
            Indent      = true,
            IndentChars = "  ",
            Encoding    = System.Text.Encoding.UTF8
        };
        using var writer = System.Xml.XmlWriter.Create(targetFile, settings);
        target.Save(writer);
    }

    private static void MergeXmlSection(
        System.Xml.XmlDocument target, System.Xml.XmlDocument template,
        string xpath, string nodeName, string keyAttr, string valueAttr)
    {
        var targetSection   = target.SelectSingleNode(xpath);
        var templateSection = template.SelectSingleNode(xpath);
        if (targetSection is null || templateSection is null) return;

        foreach (System.Xml.XmlNode node in templateSection.SelectNodes(nodeName)!)
        {
            var key      = node.Attributes?[keyAttr]?.Value;
            if (key is null) continue;
            var existing = targetSection.SelectSingleNode($"{nodeName}[@{keyAttr}='{key}']");
            if (existing?.Attributes?[valueAttr] != null)
                existing.Attributes[valueAttr]!.Value = node.Attributes![valueAttr]?.Value ?? "";
            else
                targetSection.AppendChild(target.ImportNode(node, true));
        }
    }

    private static void OpenIde(string solutionPath, List<string> messages)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var devenv = FindDevEnvViaVswhere();
            if (devenv != null)
            {
                Process.Start(new ProcessStartInfo { FileName = devenv, Arguments = $"\"{solutionPath}\"", UseShellExecute = true });
                messages.Add($"[IDE] Abrindo VS: {Path.GetFileName(solutionPath)}");
                return;
            }
            messages.Add("[IDE] devenv.exe não encontrado via vswhere — verifique a instalação do Visual Studio");
            return;
        }

        foreach (var editor in new[] { "code", "rider" })
        {
            if (!IsCommandAvailable(editor)) continue;
            Process.Start(new ProcessStartInfo { FileName = editor, Arguments = $"\"{solutionPath}\"", UseShellExecute = false });
            messages.Add($"[IDE] Abrindo {editor}: {Path.GetFileName(solutionPath)}");
            return;
        }

        messages.Add($"[IDE] Nenhum IDE encontrado para: {Path.GetFileName(solutionPath)}");
    }

    private static string? FindDevEnvViaVswhere()
    {
        // vswhere.exe é instalado pelo Visual Studio Installer em qualquer localização
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (!System.IO.File.Exists(vswhere)) return null;

        var installPath = RunProcess(vswhere, "-latest -property installationPath").Trim();
        if (string.IsNullOrEmpty(installPath)) return null;

        var devenv = Path.Combine(installPath, "Common7", "IDE", "devenv.exe");
        return System.IO.File.Exists(devenv) ? devenv : null;
    }

    // ── TEMPLATE ──────────────────────────────────────────────────────────────

    [HttpGet("template")]
    public IActionResult GetTemplate([FromQuery] string api, [FromQuery] string env, [FromQuery] string? client)
    {
        client ??= "default";
        var cfg     = _config.LoadConfig();
        var apiCfg  = cfg.Apis.FirstOrDefault(a => a.Name == api);
        var ext     = apiCfg?.ConfigType == "json" ? "json" : "xml";
        var path    = Path.Combine(_templatesDir, api, env, $"{client}.{ext}");

        if (!System.IO.File.Exists(path))
            return Ok(new { content = "", path, notFound = true });

        var content = System.IO.File.ReadAllText(path);
        return Ok(new { content, path });
    }

    [HttpPost("template")]
    public IActionResult SaveTemplate([FromBody] SaveTemplateRequest body)
    {
        var cfg    = _config.LoadConfig();
        var apiCfg = cfg.Apis.FirstOrDefault(a => a.Name == body.Api);
        var ext    = apiCfg?.ConfigType == "json" ? "json" : "xml";
        var dir    = Path.Combine(_templatesDir, body.Api, body.Env);
        var path   = Path.Combine(dir, $"{body.Client ?? "default"}.{ext}");

        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(path, body.Content ?? "");
        return Ok(new { success = true, path });
    }

    // ── GIT ───────────────────────────────────────────────────────────────────

    [HttpGet("git/status")]
    public IActionResult GitStatus([FromQuery] string? api)
    {
        var cfg  = _config.LoadConfig();
        var seen = new HashSet<string>();
        var results = new List<object>();

        foreach (var a in cfg.Apis)
        {
            if (!string.IsNullOrEmpty(api) && a.Name != api) continue;
            if (string.IsNullOrEmpty(a.GitRepo) || seen.Contains(a.GitRepo)) continue;
            seen.Add(a.GitRepo);

            var files = GetGitStatusFiles(a.GitRepo);
            var branch = RunGit("rev-parse --abbrev-ref HEAD", a.GitRepo).Trim();
            results.Add(new { name = a.Name, repo = a.GitRepo, status = new { branch, files, count = files.Count } });
        }

        return Ok(results);
    }

    [HttpGet("git/aheadbehind")]
    public IActionResult GitAheadBehind()
    {
        var cfg  = _config.LoadConfig();
        var seen = new HashSet<string>();
        var results = new List<object>();

        foreach (var a in cfg.Apis)
        {
            if (string.IsNullOrEmpty(a.GitRepo) || seen.Contains(a.GitRepo)) continue;
            seen.Add(a.GitRepo);

            RunGit("fetch origin", a.GitRepo, timeoutMs: 5000);
            var branch = RunGit("rev-parse --abbrev-ref HEAD", a.GitRepo).Trim();
            var ab     = RunGit($"rev-list --left-right --count origin/{branch}...HEAD", a.GitRepo).Trim();
            var parts  = ab.Split('\t');
            var behind = parts.Length > 0 && int.TryParse(parts[0], out var b) ? b : 0;
            var ahead  = parts.Length > 1 && int.TryParse(parts[1], out var ah) ? ah : 0;
            var last   = RunGit("log -1 --format=\"%h — %s — %ar\"", a.GitRepo).Trim();
            var status = ahead == 0 && behind == 0 ? "synced" : behind > 0 ? "behind" : "ahead";

            results.Add(new { name = a.Name, repo = a.GitRepo, aheadBehind = new { branch, ahead, behind, lastCommit = last, status } });
        }

        return Ok(results);
    }

    [HttpPost("git/commit")]
    public IActionResult GitCommit([FromBody] GitCommitRequest body)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => a.Name == body.Api);
        if (api is null) return BadRequest(new { success = false, error = "API não encontrada" });

        RunGit("add -A", api.GitRepo);
        var output = RunGit($"commit -m \"{EscapeArg(body.Message ?? "")}\"", api.GitRepo);
        return Ok(new { success = true, results = new[] { new { name = api.Name, result = new { output } } } });
    }

    [HttpPost("git/discard")]
    public IActionResult GitDiscard([FromBody] GitApiRequest body)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => a.Name == body.Api);
        if (api is null) return BadRequest(new { success = false });

        RunGit("checkout -- .", api.GitRepo);
        RunGit("clean -fd", api.GitRepo);
        return Ok(new { success = true });
    }

    // ── SERVER PULL CONFIG ────────────────────────────────────────────────────

    [HttpPost("server/pullconfig")]
    public IActionResult ServerPullConfig([FromBody] ServerPullRequest body)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return StatusCode(501, new { success = false, error = "Pull de config via SMB não disponível nesta plataforma." });

        var cfg       = _config.LoadConfig();
        var serverCfg = cfg.Servers?.GetValueOrDefault(body.Environment);
        if (serverCfg is null)
            return BadRequest(new { success = false, error = $"Nenhum servidor configurado para '{body.Environment}'" });

        RunProcess("net", $"use \"{serverCfg.Host}\" /user:\"{serverCfg.User}\" \"{serverCfg.Password}\"");

        var results = new List<object>();
        try
        {
            var apisToProcess = serverCfg.Apis.AsEnumerable();
            if (!string.IsNullOrEmpty(body.Api) && body.Api != "all")
                apisToProcess = apisToProcess.Where(a => a.Name == body.Api);

            foreach (var api in apisToProcess)
            {
                var uncPath  = Path.Combine(serverCfg.Host, api.ConfigPath);
                var ext      = api.ConfigType == "json" ? "json" : "xml";
                var destDir  = Path.Combine(_templatesDir, api.Name, body.Environment);
                var destFile = Path.Combine(destDir, $"{body.Client ?? "default"}.{ext}");
                Directory.CreateDirectory(destDir);

                if (System.IO.File.Exists(uncPath))
                {
                    System.IO.File.Copy(uncPath, destFile, overwrite: true);
                    results.Add(new { name = api.Name, success = true, dest = destFile });
                }
                else
                {
                    results.Add(new { name = api.Name, success = false, error = $"Não encontrado: {uncPath}" });
                }
            }
        }
        finally
        {
            RunProcess("net", $"use \"{serverCfg.Host}\" /delete");
        }

        return Ok(new { success = true, results });
    }

    // ── RESTART ───────────────────────────────────────────────────────────────

    [HttpPost("restart")]
    public IActionResult Restart()
    {
        var rootPath = _cfg["DevAutomation:RootPath"]!;
        var csproj   = Path.Combine(rootPath, "src", "DevAutomation.Server", "DevAutomation.Server.csproj");

        Task.Run(async () =>
        {
            await Task.Delay(500);
            Process.Start(new ProcessStartInfo
            {
                FileName         = "dotnet",
                Arguments        = $"run --project \"{csproj}\" --configuration Release",
                UseShellExecute  = true,
                WorkingDirectory = rootPath
            });
            Environment.Exit(0);
        });
        return Ok(new { success = true, message = "Reiniciando..." });
    }

    // ── AGENT ─────────────────────────────────────────────────────────────────

    [HttpGet("agent/config")]
    public IActionResult AgentConfig()
    {
        var cfg        = _config.LoadConfig();
        var geminiKey  = cfg.Agent?.ApiKey ?? _cfg["agent:apiKey"] ?? _cfg["Agent:apiKey"] ?? "";
        var claudeAvailable = _featureFlags.DevAgentClaudeEnabled;

        return Ok(new
        {
            llms = new[]
            {
                new { id = "gemini", label = "Gemini", available = !string.IsNullOrEmpty(geminiKey),
                      hint = (string?)null,
                      models = new[] {
                          new { id = "gemini-2.5-flash", label = "Gemini Flash" },
                          new { id = "gemini-2.5-pro",   label = "Gemini Pro"   }
                      }},
                new { id = "claude", label = "Claude", available = claudeAvailable,
                      hint = claudeAvailable ? null : "Claude Code CLI não encontrado",
                      models = new[] {
                          new { id = "claude-sonnet-4-6",         label = "Claude Sonnet" },
                          new { id = "claude-haiku-4-5-20251001", label = "Claude Haiku"  }
                      }}
            }
        });
    }

    [HttpPost("agent")]
    public async Task<IActionResult> Agent([FromBody] AgentRequest body)
    {
        var cfg   = _config.LoadConfig();
        var agent = cfg.Agent;

        // fallback: lê do appsettings.json se environments.json não tiver agent configurado
        if (agent is null || string.IsNullOrEmpty(agent.ApiKey))
        {
            var key   = _cfg["agent:apiKey"] ?? _cfg["Agent:apiKey"] ?? "";
            var model = _cfg["agent:model"] ?? _cfg["Agent:model"] ?? "gemini-2.5-flash";
            var url   = _cfg["agent:url"]   ?? _cfg["Agent:url"]   ?? "v1beta";
            if (string.IsNullOrEmpty(key))
                return BadRequest(new { type = "error", text = "AgentConfig não configurado." });
            agent = new Models.AgentConfig { ApiKey = key, Model = model, Url = url };
        }

        var state    = _config.LoadState();
        var apiNames = string.Join(", ", cfg.Apis.Select(a => a.Name));


        var systemCtx = $"""
            Você é o DevAgent — um Arquiteto de Sistemas Sênior com ampla experiência em back-end,
            distribuídos e boas práticas de engenharia de software.
            Responda sempre em português brasileiro, de forma concisa e direta.

            ## IDENTIDADE E POSTURA

            - Você atua como arquiteto e copiloto técnico, não como executor cego de pedidos.
            - Antes de qualquer ação, entenda o projeto envolvido: stack, padrões arquiteturais,
              contexto de negócio e histórico recente. Use as ferramentas disponíveis para isso
              (ler_arquivo, git_log, listar_arquivos).
            - Nunca inicie nada com dúvidas sobre o que deve ser feito ou sobre os padrões do projeto.
              Se houver ambiguidade técnica ou de requisito, faça perguntas até ter clareza total.
            - Se o solicitante demonstrar dúvidas sobre o que quer ou sobre como o projeto funciona,
              oriente-o a se aprofundar antes de registrar uma solicitação. Não registre dev-requests
              baseadas em requisitos vagos ou mal compreendidos.

            ## FLUXO PARA CRIAR DEV-REQUESTS

            1. Pergunte em qual projeto/API a alteração se aplica.
            2. Use git_log e ler_arquivo (CLAUDE.md, README) para entender o contexto atual.
            3. Verifique o backlog existente para evitar duplicatas.
            4. Faça as perguntas necessárias até ter clareza sobre: o que fazer, por que fazer
               e como validar que está correto.
            5. Somente então use solicitar_desenvolvimento com uma descrição e detalhes precisos.
            6. Se o desenvolvedor já implementou a feature, marque implementado_pelo_usuario = true
               para que vá direto para testes sem passar pelo agente.

            ## FORMATO DE DEV-REQUEST

            - tipo: feature | bugfix | config | refactor
            - impacto: baixo | medio | alto
            - descricao: frase clara no imperativo ("Adicionar endpoint X", "Corrigir validação Y")
            - detalhes: contexto técnico, arquivos envolvidos, comportamento esperado, critérios de aceite

            ## ESTADO ATUAL DO AMBIENTE

            - Projetos gerenciados: {apiNames}
            - Estado: {JsonSerializer.Serialize(state)}

            ## MODELO DE DEV-REQUEST (referência de formato — não é um exemplo real)

            {JsonSerializer.Serialize(new Models.DevRequest
            {
                Api                     = "nome-do-projeto",
                Tipo                    = "feature | bugfix | config | refactor",
                Impacto                 = "baixo | medio | alto",
                Descricao               = "Verbo no imperativo + o que + onde",
                Detalhes                = "Contexto técnico: camadas afetadas, arquivos relevantes, comportamento esperado, critérios de aceite, edge cases.",
                Status                  = "pendente",
                ImplementadoPeloUsuario = false
            }, _jsonWriteOpts)}

            ## ENTENDIMENTO DO PROJETO ALVO

            Antes de criar uma dev-request, use as ferramentas para entender o projeto destino:
            - git_log(api) — histórico recente, o que foi entregue, padrões de commit
            - ler_arquivo(CLAUDE.md do projeto) — stack, arquitetura, regras do projeto
            - listar_arquivos(api, glob: "*.md") — documentação existente

            Faça isso SOMENTE para o projeto alvo da solicitação, não para outros projetos.
            O objetivo é minimizar tokens: busque apenas o que for necessário para entender
            o contexto e evitar conflitos com o que já foi implementado.

            ## REGRAS GERAIS

            - Ao executar ações, confirme resumidamente o que foi feito.
            - Para switch de ambiente sem especificar APIs, use all.
            - Nunca invente dados — use sempre as ferramentas para buscar informações reais.
            - Nunca registre uma dev-request sem antes confirmar com o solicitante o que será registrado.
            - Quando a solicitação afetar layout ou UI, inclua nos detalhes: qual componente/tela é
              afetado e se há alguma dev-request anterior que serviu de referência de layout e que
              deve ser atualizada para refletir o novo padrão.
            """;

        var history    = body.History?.Select(h => new GeminiMessage(h.Role, h.Parts)).ToList();
        var modelToUse = !string.IsNullOrWhiteSpace(body.Model) ? body.Model : agent.Model;
        var llm        = body.Llm ?? "gemini";

        var claudePath = _cfg["DevAutomation:ClaudePath"] ?? "claude";

        GeminiResponse resp;
        var forgeRoot = _cfg["DevAutomation:RootPath"];

        if (llm == "claude")
        {
            resp = await _claude.SendAsync(claudePath, modelToUse, body.Message ?? "", systemCtx, history, forgeRoot);
        }
        else
        {
            resp = await _gemini.SendAsync(
                agent.ApiKey, modelToUse, agent.Url,
                body.Message ?? "", systemCtx, history,
                imageBase64: body.ImageBase64, imageMimeType: body.ImageMimeType);
        }

        if (resp.Type == "error")
            return Ok(new { type = "error", text = resp.Text });

        if (resp.Type == "toolCall")
        {
            var toolName = resp.ToolName!;
            var args     = resp.ToolArgs;
            object? result = null;

            switch (toolName)
            {
                case "switch_environment":
                    var envArg   = args?["environment"]?.GetValue<string>() ?? "developer";
                    var clientArg = args?["client"]?.GetValue<string>() ?? "default";
                    var apisArg  = args?["apis"]?.GetValue<string>() ?? "all";
                    var pull     = args?["gitPull"]?.GetValue<bool>() ?? false;
                    var openVS   = args?["openVS"]?.GetValue<bool>() ?? false;
                    var closeVS  = args?["closeVS"]?.GetValue<bool>() ?? false;
                    var switchResp = Switch(new SwitchRequest
                    {
                        Environment = envArg, Client = clientArg, Api = apisArg,
                        GitPull = pull, OpenVisualStudio = openVS, CloseVisualStudio = closeVS
                    });
                    result = switchResp is OkObjectResult ok ? ok.Value : new { error = "switch failed" };
                    break;

                case "get_git_status":
                    result = (GitStatus(args?["api"]?.GetValue<string>()) as OkObjectResult)?.Value;
                    break;

                case "get_git_ahead_behind":
                    result = (GitAheadBehind() as OkObjectResult)?.Value;
                    break;

                case "get_current_status":
                    result = _config.LoadState();
                    break;

                case "solicitar_desenvolvimento":
                    var devReq = new Models.DevRequest
                    {
                        Id                      = Guid.NewGuid().ToString(),
                        Api                     = args?["api"]?.GetValue<string>() ?? "devautomation",
                        Tipo                    = args?["tipo"]?.GetValue<string>() ?? "feature",
                        Impacto                 = args?["impacto"]?.GetValue<string>() ?? "medio",
                        Descricao               = args?["descricao"]?.GetValue<string>() ?? "",
                        Detalhes                = args?["detalhes"]?.GetValue<string>(),
                        Status                  = "pendente",
                        ImplementadoPeloUsuario = args?["implementado_pelo_usuario"]?.GetValue<bool>() ?? false,
                        DiretorioAlvo           = Path.Combine(_cfg["DevAutomation:RootPath"]!, "src", "DevAutomation.Server"),
                        Timestamp               = DateTime.UtcNow
                    };
                    await _store.SaveAsync(devReq);
                    result = new { mensagem = "Solicitação registrada com sucesso.", id = devReq.Id };
                    break;

                case "ler_arquivo":
                    result = LerArquivo(args?["caminho"]?.GetValue<string>() ?? "");
                    break;

                case "git_log":
                    result = GitLog(args?["api"]?.GetValue<string>() ?? "", args?["quantidade"]?.GetValue<int>() ?? 15);
                    break;

                case "listar_arquivos":
                    result = ListarArquivos(args?["api"]?.GetValue<string>() ?? "", args?["subdir"]?.GetValue<string>(), args?["glob"]?.GetValue<string>());
                    break;

                default:
                    result = new { error = $"Tool '{toolName}' não implementada." };
                    break;
            }

            GeminiResponse finalResp;
            if (llm == "claude")
            {
                var updatedHistory = new List<GeminiMessage>(history ?? [])
                    { new("user", [new { text = body.Message ?? "" }]) };
                finalResp = await _claude.SendToolResultAsync(
                    claudePath, modelToUse, systemCtx, updatedHistory, toolName, result ?? new { }, forgeRoot);
            }
            else
            {
                var updatedHistory = new List<GeminiMessage>(history ?? [])
                {
                    new("user",  [new { text = body.Message ?? "" }]),
                    new("model", [new { functionCall = new { name = toolName, args = args } }])
                };
                finalResp = await _gemini.SendToolResultAsync(
                    agent.ApiKey, modelToUse, agent.Url,
                    systemCtx, updatedHistory, toolName, result ?? new { });
            }

            return Ok(new { type = "text", text = finalResp.Text, action = toolName });
        }

        return Ok(new { type = "text", text = resp.Text });
    }

    // ── ROADMAP ───────────────────────────────────────────────────────────────

    [HttpPost("roadmap/promote")]
    public async Task<IActionResult> RoadmapPromote([FromBody] RoadmapPromoteRequest body)
    {
        var panelDir     = _cfg["DevAutomation:PanelDir"]!;
        var projectsPath = Path.Combine(panelDir, "projects.json");

        if (!System.IO.File.Exists(projectsPath))
            return NotFound(new { success = false, error = "projects.json não encontrado" });

        var json = System.IO.File.ReadAllText(projectsPath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var projects = root["projects"]!.AsArray();

        var project = projects.FirstOrDefault(p => p!["id"]?.GetValue<string>() == body.ProjectId);
        if (project is null)
            return NotFound(new { success = false, error = "Projeto não encontrado" });

        var roadmap = project["roadmap"]?.AsArray();
        var item    = roadmap?.FirstOrDefault(r => r!["id"]?.GetValue<string>() == body.RoadmapItemId);
        if (item is null)
            return NotFound(new { success = false, error = "Item de roadmap não encontrado" });

        var devReq = new DevRequest
        {
            Id        = Guid.NewGuid().ToString(),
            Api       = project["internalName"]?.GetValue<string>()
                        ?? project["id"]?.GetValue<string>() ?? body.ProjectId,
            Tipo      = "feature",
            Impacto   = item["impacto"]?.GetValue<string>() ?? "medio",
            Descricao = item["titulo"]?.GetValue<string>() ?? "",
            Detalhes  = item["descricao"]?.GetValue<string>(),
            Status    = "pendente",
            Timestamp = DateTime.UtcNow
        };

        await _store.SaveAsync(devReq);

        // Atualiza status do item para in_progress
        item["status"] = "in_progress";
        System.IO.File.WriteAllText(projectsPath, root.ToJsonString(_jsonWriteOpts));

        return Ok(new { success = true, devRequestId = devReq.Id });
    }

    [HttpPost("roadmap/update-status")]
    public IActionResult RoadmapUpdateStatus([FromBody] RoadmapStatusRequest body)
    {
        var panelDir     = _cfg["DevAutomation:PanelDir"]!;
        var projectsPath = Path.Combine(panelDir, "projects.json");

        if (!System.IO.File.Exists(projectsPath))
            return NotFound(new { success = false, error = "projects.json não encontrado" });

        var json = System.IO.File.ReadAllText(projectsPath);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var projects = root["projects"]!.AsArray();

        var project = projects.FirstOrDefault(p => p!["id"]?.GetValue<string>() == body.ProjectId);
        if (project is null)
            return NotFound(new { success = false, error = "Projeto não encontrado" });

        var roadmap = project["roadmap"]?.AsArray();
        var item    = roadmap?.FirstOrDefault(r => r!["id"]?.GetValue<string>() == body.RoadmapItemId);
        if (item is null)
            return NotFound(new { success = false, error = "Item de roadmap não encontrado" });

        item["status"] = body.Status;
        System.IO.File.WriteAllText(projectsPath, root.ToJsonString(_jsonWriteOpts));

        return Ok(new { success = true });
    }

    // ── DEV-REQUESTS ─────────────────────────────────────────────────────────

    [HttpGet("devrequests")]
    public async Task<IActionResult> GetDevRequests()
    {
        return Ok(await _store.ListAllAsync());
    }

    [HttpGet("devrequests/stats")]
    public async Task<IActionResult> GetDevRequestStats()
    {
        var all = await _store.ListAllAsync();
        var porStatus = all
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new { total = porStatus.Values.Sum(), por_status = porStatus });
    }

    [HttpPost("devrequests")]
    public async Task<IActionResult> CreateDevRequest([FromBody] DevRequest request)
    {
        request.Id        = Guid.NewGuid().ToString();
        request.Status    = "pendente";
        request.Timestamp = DateTime.UtcNow;
        await _store.SaveAsync(request);
        return Ok(new { success = true, id = request.Id });
    }

    [HttpPut("devrequests/{id}")]
    public async Task<IActionResult> EditDevRequest(string id, [FromBody] DevRequestEditBody body)
    {
        var req = await _store.GetByIdAsync(id);
        if (req is null)
            return NotFound(new { success = false, error = "Dev request não encontrada." });

        req.Api                     = body.Api                  ?? req.Api;
        req.Tipo                    = body.Tipo                 ?? req.Tipo;
        req.Impacto                 = body.Impacto              ?? req.Impacto;
        req.Descricao               = body.Descricao            ?? req.Descricao;
        req.Detalhes                = body.Detalhes             ?? req.Detalhes;
        req.DiretorioAlvo           = body.DiretorioAlvo        ?? req.DiretorioAlvo;
        req.ComentariosTeste        = body.ComentariosTeste     ?? req.ComentariosTeste;
        req.ConsideracoesRefazer    = body.ConsideracoesRefazer ?? req.ConsideracoesRefazer;
        if (body.ImplementadoPeloUsuario.HasValue)
            req.ImplementadoPeloUsuario = body.ImplementadoPeloUsuario.Value;
        req.TimestampAtualizacao = DateTime.UtcNow;
        await _store.SaveAsync(req);
        return Ok(new { success = true });
    }

    [HttpPost("devrequests/action")]
    public async Task<IActionResult> DevRequestAction([FromBody] DevRequestActionBody body)
    {
        var result = await _orchestrator.ProcessActionAsync(body.Id!, body.Action!);
        return Ok(new { success = result });
    }

    [HttpPost("devrequests/responder")]
    public async Task<IActionResult> DevRequestResponder([FromBody] DevRequestResponderBody body)
    {
        var req = await _store.GetByIdAsync(body.Id ?? "");
        if (req is null)
            return NotFound(new { success = false, error = "Dev request não encontrada." });

        req.RespostaUsuario      = body.Resposta;
        req.Status               = "pendente";
        req.TimestampAtualizacao = DateTime.UtcNow;
        await _store.SaveAsync(req);
        return Ok(new { success = true });
    }

    // ── RAG ───────────────────────────────────────────────────────────────────

    [HttpGet("rag/stats")]
    public async Task<IActionResult> RagStats()
    {
        var (total, porProjeto) = await _ragIndexer.GetStatsAsync();
        return Ok(new
        {
            ready       = _ragIndexer.IsReady,
            totalChunks = total,
            porProjeto  = porProjeto
        });
    }

    [HttpPost("rag/reindex")]
    public async Task<IActionResult> RagReindex()
    {
        await _ragIndexer.ReindexAsync();
        var (total, porProjeto) = await _ragIndexer.GetStatsAsync();
        return Ok(new { ok = true, totalChunks = total, porProjeto = porProjeto });
    }

    [HttpGet("rag/search")]
    public async Task<IActionResult> RagSearch([FromQuery] string q, [FromQuery] int limit = 5, [FromQuery] string? project = null, [FromQuery] string? tipo = null)
    {
        var filtrosProjeto = project != null ? new[] { project } : null;
        var filtrosTipo    = tipo    != null ? new[] { tipo }    : null;
        var chunks  = await _ragService.QueryAsync(q, limit, filtrosProjeto, filtrosTipo);
        return Ok(chunks.Select(c => new
        {
            c.Fonte,
            c.Projeto,
            c.Tipo,
            conteudo = c.Conteudo[..Math.Min(300, c.Conteudo.Length)]
        }));
    }

    // ── OTEL DIAGNOSTIC ──────────────────────────────────────��───────────────

    [HttpGet("otel/diag")]
    public IActionResult OtelDiag()
    {
        var source = new System.Diagnostics.ActivitySource("Forge.SoftwareFactory.Core");
        var hasListeners = source.HasListeners();
        using var activity = source.StartActivity("DiagnosticProbe");

        var pubKey  = _cfg["Langfuse:PublicKey"] ?? "";
        var secKey  = _cfg["Langfuse:SecretKey"] ?? "";
        var baseUrl = _cfg["Langfuse:BaseUrl"] ?? "https://cloud.langfuse.com";

        return Ok(new
        {
            hasListeners,
            activityCreated   = activity != null,
            activityId        = activity?.Id,
            langfuseEnabled   = !string.IsNullOrEmpty(pubKey) && !string.IsNullOrEmpty(secKey),
            langfuseEndpoint  = $"{baseUrl}/api/public/otel/v1/traces",
            langfusePublicKey = pubKey.Length > 0 ? pubKey[..Math.Min(8, pubKey.Length)] + "..." : "(não configurado)"
        });
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    private static string RunGit(string args, string workDir, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "git",
                Arguments              = args,
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            using var p = Process.Start(psi)!;
            var output  = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            if (!p.HasExited) p.Kill();
            return output;
        }
        catch { return ""; }
    }

    private static List<object> GetGitStatusFiles(string repoPath)
    {
        var lines = RunGit("status --porcelain", repoPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var files = new List<object>();
        foreach (var line in lines)
        {
            if (line.Length < 3) continue;
            var xy   = line[..2].Trim();
            var path = line[3..].Trim();
            var type = xy switch
            {
                var s when s.StartsWith('M') => "modified",
                var s when s.StartsWith('A') => "added",
                var s when s.StartsWith('D') => "deleted",
                var s when s.StartsWith('R') => "renamed",
                "??"                         => "untracked",
                _                            => xy
            };

            var fullPath = Path.Combine(repoPath, path);
            var diff = type == "modified"
                ? RunGit($"diff HEAD -- \"{path}\"", repoPath)
                : type is "added" or "untracked" && System.IO.File.Exists(fullPath)
                    ? System.IO.File.ReadAllText(fullPath)
                    : "";

            files.Add(new { path, type, diff });
        }

        return files;
    }

    private static string EscapeArg(string s) => s.Replace("\"", "\\\"");

    [HttpPost("stop-apps")]
    public async Task<IActionResult> StopApps([FromBody] StartAppsRequest body)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, body.Api, StringComparison.OrdinalIgnoreCase));
        if (api is null) return NotFound(new { message = $"API '{body.Api}' não encontrada." });
        if (api.RunTargets is null || api.RunTargets.Count == 0)
            return Ok(new { message = "Nenhum runTarget para parar." });

        var killed = KillDotnetInTargets(api.RunTargets.Select(t => t.Dir).ToList());
        await Task.Delay(500);
        return Ok(new { message = $"{killed} processo(s) encerrado(s).", targets = api.RunTargets.Select(t => t.Name) });
    }

    [HttpPost("start-apps")]
    public async Task<IActionResult> StartApps([FromBody] StartAppsRequest body)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, body.Api, StringComparison.OrdinalIgnoreCase));
        if (api is null) return NotFound(new { message = $"API '{body.Api}' não encontrada." });
        if (api.RunTargets is null || api.RunTargets.Count == 0)
            return BadRequest(new { message = "Nenhum runTarget configurado para esta API." });

        KillDotnetInTargets(api.RunTargets.Select(t => t.Dir).ToList());
        await Task.Delay(2000);

        foreach (var target in api.RunTargets)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsCommandAvailable("wt"))
            {
                var args = $"new-tab --title \"{target.Name}\" --startingDirectory \"{target.Dir}\" pwsh -NoExit -Command \"{target.Command}\"";
                Process.Start(new ProcessStartInfo { FileName = "wt", Arguments = args, UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var script = $"tell application \\\"Terminal\\\" to do script \\\"cd '{target.Dir}' && {target.Command}\\\"";
                Process.Start(new ProcessStartInfo { FileName = "osascript", Arguments = $"-e \"{script}\"", UseShellExecute = false });
            }
            else
            {
                // Linux / container: inicia em background sem terminal gráfico
                Process.Start(new ProcessStartInfo
                {
                    FileName         = "bash",
                    Arguments        = $"-c \"{target.Command} &\"",
                    UseShellExecute  = false,
                    WorkingDirectory = target.Dir
                });
            }
        }

        if (!string.IsNullOrEmpty(api.BrowserUrl))
        {
            var opener = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? null
                       : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "open"
                       : "xdg-open";
            if (opener is null)
                Process.Start(new ProcessStartInfo { FileName = api.BrowserUrl, UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = opener, Arguments = api.BrowserUrl, UseShellExecute = false });
        }

        return Ok(new { message = $"{api.RunTargets.Count} processo(s) iniciado(s).", targets = api.RunTargets.Select(t => t.Name) });
    }

    private static int KillDotnetInTargets(List<string> targetDirs)
    {
        int killed = 0;

        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                var cmdLine = GetProcessCommandLine(proc.Id);
                var workDir = GetProcessWorkingDirectory(proc.Id);
                if (targetDirs.Any(dir =>
                    cmdLine.Contains(dir, StringComparison.OrdinalIgnoreCase) ||
                    workDir.Contains(dir, StringComparison.OrdinalIgnoreCase)))
                {
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        foreach (var proc in Process.GetProcessesByName("node"))
        {
            try
            {
                var cmdLine = GetProcessCommandLine(proc.Id);
                if (targetDirs.Any(dir => cmdLine.Contains(dir, StringComparison.OrdinalIgnoreCase)))
                {
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return killed;
    }

    private static string GetProcessCommandLine(int pid)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var path = $"/proc/{pid}/cmdline";
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Replace('\0', ' ') : "";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RunProcess("ps", $"-p {pid} -o command=", timeoutMs: 3000);

            // Windows
            return RunProcess("wmic", $"process where ProcessId={pid} get CommandLine /format:list", timeoutMs: 3000);
        }
        catch { return ""; }
    }

    private static string GetProcessWorkingDirectory(int pid)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var link = $"/proc/{pid}/cwd";
                return Directory.Exists(link) ? (new DirectoryInfo(link).ResolveLinkTarget(true)?.FullName ?? "") : "";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RunProcess("lsof", $"-p {pid} -Fn", timeoutMs: 3000);

            // Windows
            return RunProcess("wmic", $"process where ProcessId={pid} get WorkingDirectory /format:list", timeoutMs: 3000);
        }
        catch { return ""; }
    }

    private static object LerArquivo(string caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho))
            return new { erro = "Caminho não informado." };

        if (!System.IO.File.Exists(caminho))
            return new { erro = $"Arquivo não encontrado: {caminho}" };

        // Limita leitura a 50KB para não explodir o contexto do LLM
        var info = new FileInfo(caminho);
        if (info.Length > 51_200)
        {
            var preview = System.IO.File.ReadLines(caminho).Take(200);
            return new { conteudo = string.Join('\n', preview), truncado = true, tamanho = info.Length };
        }

        return new { conteudo = System.IO.File.ReadAllText(caminho), truncado = false, tamanho = info.Length };
    }

    private object GitLog(string apiName, int quantidade)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, apiName, StringComparison.OrdinalIgnoreCase));
        if (api is null)
            return new { erro = $"API '{apiName}' não encontrada." };
        if (string.IsNullOrEmpty(api.GitRepo) || !Directory.Exists(api.GitRepo))
            return new { erro = $"Repositório não encontrado: {api.GitRepo}" };

        var log = RunGit($"log --oneline --format=\"%h | %ad | %an | %s\" --date=short -{quantidade}", api.GitRepo);
        var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        return new { api = apiName, repo = api.GitRepo, commits };
    }

    private object ListarArquivos(string apiName, string? subdir, string? glob)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, apiName, StringComparison.OrdinalIgnoreCase));
        if (api is null)
            return new { erro = $"API '{apiName}' não encontrada." };
        if (string.IsNullOrEmpty(api.GitRepo) || !Directory.Exists(api.GitRepo))
            return new { erro = $"Repositório não encontrado: {api.GitRepo}" };

        var baseDir = string.IsNullOrEmpty(subdir)
            ? api.GitRepo
            : Path.Combine(api.GitRepo, subdir);

        if (!Directory.Exists(baseDir))
            return new { erro = $"Diretório não encontrado: {baseDir}" };

        var pattern = string.IsNullOrEmpty(glob) ? "*" : glob;
        var arquivos = Directory.GetFiles(baseDir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar))
            .Select(f => f.Replace(api.GitRepo, "").TrimStart(Path.DirectorySeparatorChar))
            .Take(200)
            .ToList();

        return new { api = apiName, baseDir, arquivos, total = arquivos.Count };
    }

    private static string RunProcess(string fileName, string arguments, string? workDir = null, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            if (workDir != null) psi.WorkingDirectory = workDir;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            if (!p.HasExited) p.Kill();
            return output;
        }
        catch { return ""; }
    }

    [HttpPost("projects/discover")]
    public IActionResult DiscoverProject([FromBody] DiscoverProjectRequest body)
    {
        var path = body.Path?.Trim();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return BadRequest(new { error = "Caminho inválido ou não encontrado." });

        // Git root
        var gitRoot = RunGit("rev-parse --show-toplevel", path).Trim().Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(gitRoot)) gitRoot = path;

        // .sln — procura na raiz git primeiro, depois sobe
        var slnFiles = Directory.GetFiles(gitRoot, "*.sln", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .OrderBy(f => f.Length)
            .ToList();
        var solutionPath = slnFiles.FirstOrDefault();

        // Web .csproj → runTargets + browserUrl
        var runTargets = new List<RunTargetDto>();
        string? browserUrl = null;

        var csprojFiles = Directory.GetFiles(gitRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();

        foreach (var csproj in csprojFiles)
        {
            try
            {
                var content = System.IO.File.ReadAllText(csproj);
                if (!content.Contains("Microsoft.NET.Sdk.Web")) continue;

                var dir  = Path.GetDirectoryName(csproj)!;
                var name = Path.GetFileNameWithoutExtension(csproj);
                runTargets.Add(new RunTargetDto(name, dir, "dotnet run"));

                if (browserUrl is null)
                {
                    var launchSettings = Path.Combine(dir, "Properties", "launchSettings.json");
                    if (System.IO.File.Exists(launchSettings))
                    {
                        var ls = JsonSerializer.Deserialize<JsonElement>(System.IO.File.ReadAllText(launchSettings));
                        if (ls.TryGetProperty("profiles", out var profiles))
                        {
                            foreach (var profile in profiles.EnumerateObject())
                            {
                                if (profile.Value.TryGetProperty("applicationUrl", out var urlProp))
                                {
                                    var urls = urlProp.GetString()?.Split(';');
                                    browserUrl = urls?.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                                 ?? urls?.FirstOrDefault();
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Angular — detecta package.json com @angular/core
        var packageJsonFiles = Directory.GetFiles(gitRoot, "package.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar))
            .ToList();

        foreach (var pkg in packageJsonFiles)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(System.IO.File.ReadAllText(pkg));
                if (doc.TryGetProperty("dependencies", out var deps) && deps.TryGetProperty("@angular/core", out _))
                {
                    var dir = Path.GetDirectoryName(pkg)!;
                    runTargets.Add(new RunTargetDto("Angular", dir, "npm start"));
                }
            }
            catch { }
        }

        var proposedName = solutionPath != null
            ? Path.GetFileNameWithoutExtension(solutionPath)
            : Path.GetFileName(gitRoot.TrimEnd(Path.DirectorySeparatorChar));

        return Ok(new
        {
            name         = proposedName,
            configType   = "json",
            configFile   = "",
            gitRepo      = gitRoot,
            solutionPath = solutionPath ?? "",
            browserUrl   = browserUrl ?? "",
            runTargets
        });
    }

    [HttpPost("apps/register")]
    public IActionResult RegisterApp([FromBody] RegisterAppRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = "Nome obrigatório." });

        var cfg = _config.LoadConfig();
        if (cfg.Apis.Any(a => string.Equals(a.Name, body.Name, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { error = $"API '{body.Name}' já cadastrada." });

        cfg.Apis.Add(new ApiConfig
        {
            Name         = body.Name,
            ConfigType   = body.ConfigType ?? "json",
            ConfigFile   = body.ConfigFile ?? "",
            GitRepo      = body.GitRepo ?? "",
            SolutionPath = body.SolutionPath,
            Desktop      = body.Desktop > 0 ? body.Desktop : 1,
            BrowserUrl   = string.IsNullOrWhiteSpace(body.BrowserUrl) ? null : body.BrowserUrl,
            RunTargets   = body.RunTargets?.Select(r => new RunTarget { Name = r.Name, Dir = r.Dir, Command = r.Command }).ToList()
        });

        _config.SaveConfig(cfg);
        return Ok(new { success = true });
    }

    [HttpPost("apps/unregister")]
    public IActionResult UnregisterApp([FromBody] UnregisterAppRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = "Nome obrigatório." });

        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, body.Name, StringComparison.OrdinalIgnoreCase));
        if (api is null) return NotFound(new { error = $"API '{body.Name}' não encontrada." });

        cfg.Apis.Remove(api);
        _config.SaveConfig(cfg);
        return Ok(new { success = true });
    }

    [HttpPost("apps/update")]
    public IActionResult UpdateApp([FromBody] UpdateAppRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { error = "Nome obrigatório." });

        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, body.Name, StringComparison.OrdinalIgnoreCase));
        if (api is null) return NotFound(new { error = $"API '{body.Name}' não encontrada." });

        if (body.ConfigType   != null) api.ConfigType   = body.ConfigType;
        if (body.ConfigFile   != null) api.ConfigFile   = body.ConfigFile;
        if (body.GitRepo      != null) api.GitRepo      = body.GitRepo;
        if (body.SolutionPath != null) api.SolutionPath = body.SolutionPath;
        if (body.Desktop      > 0)     api.Desktop      = body.Desktop;
        if (body.RunTargets   != null) api.RunTargets   = body.RunTargets.Select(r => new RunTarget { Name = r.Name, Dir = r.Dir, Command = r.Command }).ToList();

        _config.SaveConfig(cfg);
        return Ok(new { success = true });
    }

    // ── BROWSE ────────────────────────────────────────────────────────────────

    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string type = "folder", [FromQuery] string? filter = null)
    {
#if WINDOWS
        string? selectedPath = null;

        var thread = new Thread(() =>
        {
            using var owner = new System.Windows.Forms.Form
            {
                TopMost       = true,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location      = new System.Drawing.Point(-2000, -2000),
                Size          = new System.Drawing.Size(1, 1),
                ShowInTaskbar = false
            };
            owner.Show();

            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 0x0002, UIntPtr.Zero);
            SetForegroundWindow(owner.Handle);
            BringWindowToTop(owner.Handle);

            if (type == "file")
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog();
                if (!string.IsNullOrWhiteSpace(filter)) dlg.Filter = filter;
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    selectedPath = dlg.FileName;
            }
            else
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.UseDescriptionForTitle = true;
                dlg.Description = "Selecione a pasta";
                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    selectedPath = dlg.SelectedPath;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return Ok(new { path = selectedPath });
#else
        return StatusCode(501, new { error = "Seletor de pasta não disponível nesta plataforma." });
#endif
    }

#if WINDOWS
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
#endif
}

// ── REQUEST MODELS ────────────────────────────────────────────────────────────

public record DevRequestActionBody(string? Id, string? Api, string? Action);
public record DevRequestResponderBody(string? Id, string? Resposta);
public record DevRequestEditBody(string? Api, string? Tipo, string? Impacto, string? Descricao, string? Detalhes, string? DiretorioAlvo, string? ComentariosTeste, string? ConsideracoesRefazer, bool? ImplementadoPeloUsuario);
public record StartAppsRequest(string Api);

public record SwitchRequest
{
    public string Environment { get; init; } = "";
    public string? Client { get; init; }
    public string? Api { get; init; }
    public bool GitPull { get; init; }
    public bool OpenVisualStudio { get; init; }
    public bool CloseVisualStudio { get; init; }
}

public record SaveTemplateRequest(string Api, string Env, string? Client, string? Content);
public record GitCommitRequest(string Api, string? Message);
public record RunTargetDto(string Name, string Dir, string Command);
public record RegisterAppRequest(string Name, string? ConfigType, string? ConfigFile, string? GitRepo, string? SolutionPath, int Desktop, List<RunTargetDto>? RunTargets, string? BrowserUrl);
public record UnregisterAppRequest(string Name);
public record UpdateAppRequest(string Name, string? ConfigType, string? ConfigFile, string? GitRepo, string? SolutionPath, int Desktop, List<RunTargetDto>? RunTargets);
public record GitApiRequest(string Api);
public record ServerPullRequest(string Environment, string? Client, string? Api);

public class AgentRequest
{
    public string? Message { get; set; }
    public List<AgentHistoryItem>? History { get; set; }
    public string? ImageBase64 { get; set; }
    public string? ImageMimeType { get; set; }
    public string? Model { get; set; }
    public string? Llm { get; set; }
}

public class AgentHistoryItem
{
    public string Role { get; set; } = "";
    public object[] Parts { get; set; } = [];
}

public record RoadmapPromoteRequest(string ProjectId, string RoadmapItemId);
public record RoadmapStatusRequest(string ProjectId, string RoadmapItemId, string Status);
public record DiscoverProjectRequest(string? Path);
