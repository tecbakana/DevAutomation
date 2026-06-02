using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevAutomation.Services;

public class ClaudeService
{
    private readonly ILogger<ClaudeService> _logger;
    private readonly IConfiguration _cfg;

    private const string ToolCallPrefix = "TOOL_CALL:";
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private static readonly string ToolsManual = """

        ## FERRAMENTAS DISPONÍVEIS

        IMPORTANTE: você NÃO tem acesso a ferramentas nativas (Bash, Write, Edit, Read, etc.).
        Não tente criar arquivos, executar comandos ou acessar o sistema de arquivos diretamente.
        A ÚNICA forma de executar ações é pelo protocolo abaixo — o servidor processa e executa.

        Quando precisar chamar uma ferramenta, responda SOMENTE com esta linha (sem nenhum texto antes ou depois):
        TOOL_CALL:{"name":"nome_da_ferramenta","args":{...}}

        Ferramentas disponíveis:
        - switch_environment(environment: "developer"|"homolog"|"master", client?, apis?, gitPull?, openVS?, closeVS?)
        - get_git_status(api)
        - get_git_ahead_behind(api?)
        - get_current_status()
        - solicitar_desenvolvimento(descricao, tipo: "feature"|"bugfix"|"config", impacto: "baixo"|"medio"|"alto", detalhes?, api?)
        - ler_arquivo(caminho)
        - git_log(api, quantidade?)
        - listar_arquivos(api, subdir?, glob?)

        Após receber o resultado de uma ferramenta, dê a resposta final em texto normal.
        """;

    public ClaudeService(ILogger<ClaudeService> logger, IConfiguration cfg)
    {
        _logger = logger;
        _cfg    = cfg;
    }

    public async Task<GeminiResponse> SendAsync(
        string claudePath,
        string model,
        string message,
        string systemContext,
        List<GeminiMessage>? history = null,
        string? workDir = null)
    {
        var system  = systemContext + ToolsManual;
        var prompt  = BuildPrompt(history, message);
        var output  = await RunCliAsync(claudePath, model, system, prompt, workDir);
        return ParseOutput(output);
    }

    public async Task<GeminiResponse> SendToolResultAsync(
        string claudePath,
        string model,
        string systemContext,
        List<GeminiMessage> history,
        string toolName,
        object toolResult,
        string? workDir = null)
    {
        var system = systemContext + ToolsManual;
        var prior  = BuildPrompt(history, null);
        var prompt = prior
            + $"\n\n[Resultado da ferramenta {toolName}]:\n"
            + JsonSerializer.Serialize(toolResult, _jsonOpts)
            + "\n\nDê sua resposta final ao usuário.";

        var output = await RunCliAsync(claudePath, model, system, prompt, workDir);
        return ParseOutput(output);
    }

    private async Task<string> RunCliAsync(string claudePath, string model, string system, string prompt, string? workDir = null)
    {
        // No Windows, claude é um .cmd — precisa de cmd.exe /c para ser resolvido pelo PATH
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var claudeArgs = new List<string>
        {
            "--dangerously-skip-permissions",
            "--print",
            "--output-format", "text",
            "--model", model,
            "--system-prompt", system,
            "--allowedTools", "none"
        };

        ProcessStartInfo psi;
        if (isWindows && !claudePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claudePath);
            foreach (var a in claudeArgs) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName               = claudePath,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            foreach (var a in claudeArgs) psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            psi.WorkingDirectory = workDir;

        try
        {
            using var proc    = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();

            var output = await proc.StandardOutput.ReadToEndAsync();
            var err    = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(err))
                _logger.LogDebug("[ClaudeCLI] stderr: {Err}", err);

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCLI] Falha ao executar CLI");
            return $"ERRO: {ex.Message}";
        }
    }

    private static string BuildPrompt(List<GeminiMessage>? history, string? currentMessage)
    {
        var sb = new StringBuilder();

        foreach (var h in history ?? [])
        {
            if (h.Role == "function") continue;
            var role = h.Role == "model" ? "Assistente" : "Usuário";
            var text = ExtractText(h.Parts);
            if (!string.IsNullOrEmpty(text))
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{role}: {text}").AppendLine();
        }

        if (!string.IsNullOrEmpty(currentMessage))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Usuário: {currentMessage}").AppendLine();
            sb.Append("Assistente:");
        }

        return sb.ToString();
    }

    private static string ExtractText(object[] parts)
    {
        foreach (var part in parts)
        {
            var json = JsonSerializer.Serialize(part);
            var node = JsonNode.Parse(json);
            var text = node?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return "";
    }

    private GeminiResponse ParseOutput(string output)
    {
        if (output.StartsWith("ERRO:", StringComparison.Ordinal))
            return new GeminiResponse { Type = "error", Text = output };

        // Detecta TOOL_CALL na resposta
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(ToolCallPrefix, StringComparison.Ordinal)) continue;

            try
            {
                var json  = trimmed[ToolCallPrefix.Length..].Trim();
                var node  = JsonNode.Parse(json);
                var name  = node?["name"]?.GetValue<string>();
                var args  = node?["args"]?.AsObject();
                if (!string.IsNullOrEmpty(name))
                    return new GeminiResponse { Type = "toolCall", ToolName = name, ToolArgs = args };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ClaudeCLI] Falha ao parsear TOOL_CALL: {Line}", trimmed);
            }
        }

        return new GeminiResponse { Type = "text", Text = output };
    }
}
