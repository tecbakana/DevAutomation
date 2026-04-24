using System.Diagnostics;
using System.Text.Json;
using DevAutomation.Models;
using DevAutomation.Services;
using Microsoft.AspNetCore.Mvc;

namespace DevAutomation.Controllers;

[ApiController]
[Route("api")]
public class DevPanelController : ControllerBase
{
    private readonly ConfigService _config;
    private readonly GeminiService _gemini;
    private readonly OrchestratorService _orchestrator;
    private readonly RagIndexerService _ragIndexer;
    private readonly RagService _ragService;
    private readonly string _templatesDir;
    private readonly string _switchScript;
    private readonly ILogger<DevPanelController> _logger;
    private readonly IConfiguration _cfg;

    public DevPanelController(
        ConfigService config,
        GeminiService gemini,
        OrchestratorService orchestrator,
        RagIndexerService ragIndexer,
        RagService ragService,
        IConfiguration cfg,
        ILogger<DevPanelController> logger)
    {
        _config       = config;
        _gemini       = gemini;
        _orchestrator = orchestrator;
        _ragIndexer   = ragIndexer;
        _ragService   = ragService;
        _cfg          = cfg;
        _templatesDir = cfg["DevAutomation:TemplatesDir"]!;
        _switchScript = cfg["DevAutomation:SwitchScript"]!;
        _logger       = logger;
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
        var args = new List<string>
        {
            "-ExecutionPolicy", "Bypass",
            "-File", $"\"{_switchScript}\"",
            "-Environment", body.Environment,
            "-Client", body.Client ?? "default"
        };

        if (!string.IsNullOrEmpty(body.Api) && body.Api != "all")
            args.AddRange(["-Api", body.Api]);
        if (body.GitPull)           args.Add("-GitPull");
        if (body.OpenVisualStudio)  args.Add("-OpenVisualStudio");
        if (body.CloseVisualStudio) args.Add("-CloseVisualStudio");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            var error  = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            // Atualiza state para todas as APIs
            var cfg = _config.LoadConfig();
            foreach (var api in cfg.Apis)
                _config.SetState(api.Name, body.Client ?? "default");

            var messages = (output + error).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return Ok(new { success = true, messages });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, error = ex.Message });
        }
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
        var scriptDir = Path.GetDirectoryName(_switchScript)!;
        var script    = Path.Combine(scriptDir, "Server-Operations.ps1");

        var psi = new ProcessStartInfo
        {
            FileName  = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -Command \". '{script}'; Invoke-ServerPullConfig " +
                        $"-Environment '{body.Environment}' -Client '{body.Client ?? "default"}' " +
                        $"-ApiName '{body.Api ?? "all"}' " +
                        $"-ConfigDir '{Path.GetDirectoryName(Path.GetDirectoryName(_switchScript))}\\config' " +
                        $"-TemplatesDir '{_templatesDir}'\"",
            RedirectStandardOutput = true,
            UseShellExecute        = false
        };

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        return Ok(new { success = true, output });
    }

    // ── RESTART ───────────────────────────────────────────────────────────────

    [HttpPost("restart")]
    public IActionResult Restart()
    {
        Task.Run(async () =>
        {
            await Task.Delay(500);
            Process.Start(new ProcessStartInfo
            {
                FileName        = @"T:\Developer\RepositorioTrabalho\tecbakana\ForgeV2\batches\start-server.bat",
                UseShellExecute = true
            });
            Environment.Exit(0);
        });
        return Ok(new { success = true, message = "Reiniciando..." });
    }

    // ── AGENT ─────────────────────────────────────────────────────────────────

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
            Você é um assistente de ambiente de desenvolvimento chamado DevAgent.
            Responda sempre em português brasileiro, de forma concisa e direta.

            Quando identificar uma limitação, funcionalidade ausente ou problema no devautomation,
            use a ferramenta solicitar_desenvolvimento para registrar a melhoria. Faça isso proativamente.

            ESTADO ATUAL:
            - APIs disponíveis: {apiNames}
            - Estado: {JsonSerializer.Serialize(state)}

            REGRAS:
            - Ao executar ações, confirme o que foi feito de forma resumida
            - Se o usuário pedir algo ambíguo, pergunte antes de executar
            - Para switch de ambiente sem especificar APIs, use all
            - Nunca invente dados — use sempre as ferramentas para buscar informações reais
            """;

        var history = body.History?.Select(h => new GeminiMessage(h.Role, h.Parts)).ToList();

        var resp = await _gemini.SendAsync(
            agent.ApiKey, agent.Model, agent.Url,
            body.Message ?? "",
            systemCtx,
            history,
            imageBase64: body.ImageBase64,
            imageMimeType: body.ImageMimeType);

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
                    var gsApi = args?["api"]?.GetValue<string>();
                    result = (GitStatus(gsApi) as OkObjectResult)?.Value;
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
                        Id            = Guid.NewGuid().ToString(),
                        Api           = args?["api"]?.GetValue<string>() ?? "devautomation",
                        Tipo          = args?["tipo"]?.GetValue<string>() ?? "feature",
                        Impacto       = args?["impacto"]?.GetValue<string>() ?? "medio",
                        Descricao     = args?["descricao"]?.GetValue<string>() ?? "",
                        Detalhes      = args?["detalhes"]?.GetValue<string>(),
                        Status        = "pending",
                        DiretorioAlvo = "T:\\devautomation\\DevAutomation.Server",
                        Timestamp     = DateTime.UtcNow
                    };
                    var devReqDir  = _cfg["DevAutomation:DevRequestsDir"]!;
                    var devReqPath = Path.Combine(devReqDir, $"{devReq.Id}.json");
                    Directory.CreateDirectory(devReqDir);
                    System.IO.File.WriteAllText(devReqPath,
                        System.Text.Json.JsonSerializer.Serialize(devReq,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    result = new { mensagem = "Solicitação registrada com sucesso.", id = devReq.Id };
                    break;

                default:
                    result = new { error = $"Tool '{toolName}' não implementada." };
                    break;
            }

            var updatedHistory = new List<GeminiMessage>(history ?? [])
            {
                new("user",  [new { text = body.Message ?? "" }]),
                new("model", [new { functionCall = new { name = toolName, args = args } }])
            };

            var finalResp = await _gemini.SendToolResultAsync(
                agent.ApiKey, agent.Model, agent.Url,
                systemCtx, updatedHistory, toolName, result ?? new { });

            return Ok(new { type = "text", text = finalResp.Text, action = toolName });
        }

        return Ok(new { type = "text", text = resp.Text });
    }

    // ── ROADMAP ───────────────────────────────────────────────────────────────

    [HttpPost("roadmap/promote")]
    public IActionResult RoadmapPromote([FromBody] RoadmapPromoteRequest body)
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

        var devReqDir  = _orchestrator.DevRequestsDir;
        Directory.CreateDirectory(devReqDir);
        var path = Path.Combine(devReqDir, $"{devReq.Id}.json");
        System.IO.File.WriteAllText(path,
            JsonSerializer.Serialize(devReq, new JsonSerializerOptions { WriteIndented = true }));

        // Atualiza status do item para in_progress
        item["status"] = "in_progress";
        System.IO.File.WriteAllText(projectsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

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
        System.IO.File.WriteAllText(projectsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return Ok(new { success = true });
    }

    // ── DEV-REQUESTS ─────────────────────────────────────────────────────────

    [HttpGet("devrequests")]
    public IActionResult GetDevRequests()
    {
        return Ok(_orchestrator.ListAll());
    }

    [HttpPost("devrequests")]
    public IActionResult CreateDevRequest([FromBody] DevRequest request)
    {
        request.Id        = Guid.NewGuid().ToString();
        request.Status    = "pendente";
        request.Timestamp = DateTime.UtcNow;

        var dir  = _orchestrator.DevRequestsDir;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{request.Id}.json");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));

        return Ok(new { success = true, id = request.Id });
    }

    [HttpPut("devrequests/{id}")]
    public IActionResult EditDevRequest(string id, [FromBody] DevRequestEditBody body)
    {
        var dir  = _orchestrator.DevRequestsDir;
        var path = Path.Combine(dir, $"{id}.json");

        if (!System.IO.File.Exists(path))
            return NotFound(new { success = false, error = "Dev request não encontrada." });

        var json = System.IO.File.ReadAllText(path);
        var req  = JsonSerializer.Deserialize<DevRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (req is null)
            return BadRequest(new { success = false, error = "JSON inválido." });

        req.Api                  = body.Api ?? req.Api;
        req.Tipo                 = body.Tipo ?? req.Tipo;
        req.Impacto              = body.Impacto ?? req.Impacto;
        req.Descricao            = body.Descricao ?? req.Descricao;
        req.Detalhes             = body.Detalhes ?? req.Detalhes;
        req.DiretorioAlvo        = body.DiretorioAlvo ?? req.DiretorioAlvo;
        req.ComentariosTeste     = body.ComentariosTeste ?? req.ComentariosTeste;
        req.TimestampAtualizacao = DateTime.UtcNow;

        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true }));

        return Ok(new { success = true });
    }

    [HttpPost("devrequests/action")]
    public async Task<IActionResult> DevRequestAction([FromBody] DevRequestActionBody body)
    {
        var result = await _orchestrator.ProcessActionAsync(body.Id!, body.Action!);
        return Ok(new { success = result });
    }

    [HttpPost("devrequests/responder")]
    public IActionResult DevRequestResponder([FromBody] DevRequestResponderBody body)
    {
        var dir  = _orchestrator.DevRequestsDir;
        var path = Path.Combine(dir, $"{body.Id}.json");

        if (!System.IO.File.Exists(path))
            return NotFound(new { success = false, error = "Dev request não encontrada." });

        var json = System.IO.File.ReadAllText(path);
        var req  = JsonSerializer.Deserialize<DevRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (req is null)
            return BadRequest(new { success = false, error = "JSON inválido." });

        req.RespostaUsuario      = body.Resposta;
        req.Status               = "pendente";
        req.TimestampAtualizacao = DateTime.UtcNow;

        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true }));

        return Ok(new { success = true });
    }

    // ── RAG ───────────────────────────────────────────────────────────────────

    [HttpGet("rag/stats")]
    public IActionResult RagStats()
    {
        var (total, porProjeto) = _ragIndexer.GetStats();
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
        var (total, porProjeto) = _ragIndexer.GetStats();
        return Ok(new { ok = true, totalChunks = total, porProjeto = porProjeto });
    }

    [HttpGet("rag/search")]
    public async Task<IActionResult> RagSearch([FromQuery] string q, [FromQuery] int limit = 5, [FromQuery] string? project = null)
    {
        var filtros = project != null ? new[] { project } : null;
        var chunks  = await _ragService.QueryAsync(q, limit, filtros);
        return Ok(chunks.Select(c => new
        {
            c.Fonte,
            c.Projeto,
            c.Tipo,
            conteudo = c.Conteudo[..Math.Min(300, c.Conteudo.Length)]
        }));
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

    [HttpPost("start-apps")]
    public IActionResult StartApps([FromBody] StartAppsRequest body)
    {
        var cfg = _config.LoadConfig();
        var api = cfg.Apis.FirstOrDefault(a => string.Equals(a.Name, body.Api, StringComparison.OrdinalIgnoreCase));
        if (api is null) return NotFound(new { message = $"API '{body.Api}' não encontrada." });
        if (api.RunTargets is null || api.RunTargets.Count == 0)
            return BadRequest(new { message = "Nenhum runTarget configurado para esta API." });

        foreach (var target in api.RunTargets)
        {
            var args = $"new-tab --title \"{target.Name}\" --startingDirectory \"{target.Dir}\" pwsh -NoExit -Command \"{target.Command}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wt",
                Arguments = args,
                UseShellExecute = true
            });
        }

        if (!string.IsNullOrEmpty(api.BrowserUrl))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = api.BrowserUrl,
                UseShellExecute = true
            });

        return Ok(new { message = $"{api.RunTargets.Count} terminal(is) aberto(s).", targets = api.RunTargets.Select(t => t.Name) });
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
}

// ── REQUEST MODELS ────────────────────────────────────────────────────────────

public record DevRequestActionBody(string? Id, string? Api, string? Action);
public record DevRequestResponderBody(string? Id, string? Resposta);
public record DevRequestEditBody(string? Api, string? Tipo, string? Impacto, string? Descricao, string? Detalhes, string? DiretorioAlvo, string? ComentariosTeste);
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
public record RegisterAppRequest(string Name, string? ConfigType, string? ConfigFile, string? GitRepo, string? SolutionPath, int Desktop, List<RunTargetDto>? RunTargets);
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
}

public class AgentHistoryItem
{
    public string Role { get; set; } = "";
    public object[] Parts { get; set; } = [];
}

public record RoadmapPromoteRequest(string ProjectId, string RoadmapItemId);
public record RoadmapStatusRequest(string ProjectId, string RoadmapItemId, string Status);
