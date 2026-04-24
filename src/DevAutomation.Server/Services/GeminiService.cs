using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevAutomation.Services;

public class GeminiService
{
    private readonly HttpClient _http;
    private readonly ILogger<GeminiService> _logger;

    private static readonly object[] AgentTools =
    [
        new {
            name = "switch_environment",
            description = "Troca o ambiente de desenvolvimento (developer, homolog, master), aplica configs e opcionalmente faz git pull e abre o Visual Studio.",
            parameters = new {
                type = "object",
                properties = new {
                    environment = new { type = "string", @enum = new[]{"developer","homolog","master"}, description = "Ambiente alvo" },
                    client      = new { type = "string", description = "Cliente ex: default. Se nao informado usa default" },
                    apis        = new { type = "string", description = "APIs separadas por virgula. Se nao informado usa all" },
                    gitPull     = new { type = "boolean", description = "Se deve fazer git pull" },
                    openVS      = new { type = "boolean", description = "Se deve abrir o Visual Studio" },
                    closeVS     = new { type = "boolean", description = "Se deve fechar o Visual Studio antes" },
                    force       = new { type = "boolean", description = "Ignorar alteracoes nao commitadas" }
                },
                required = new[]{"environment"}
            }
        },
        new {
            name = "get_git_status",
            description = "Retorna os arquivos modificados, adicionados ou deletados em uma API especifica.",
            parameters = new {
                type = "object",
                properties = new {
                    api = new { type = "string", description = "Nome da API ex: cmsx" }
                },
                required = new[]{"api"}
            }
        },
        new {
            name = "get_git_ahead_behind",
            description = "Verifica quantos commits a branch local esta a frente ou atras do remoto.",
            parameters = new {
                type = "object",
                properties = new { api = new { type = "string", description = "Nome da API. Se nao informado verifica todas." } },
                required = Array.Empty<string>()
            }
        },
        new {
            name = "list_branches",
            description = "Lista as branches locais de uma API.",
            parameters = new {
                type = "object",
                properties = new {
                    api = new { type = "string", description = "Nome da API ex: cmsx" }
                },
                required = new[]{"api"}
            }
        },
        new {
            name = "get_current_status",
            description = "Retorna o status atual de todas as APIs: ambiente, cliente, branch e desktop.",
            parameters = new {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }
        },
        new {
            name = "solicitar_desenvolvimento",
            description = "Registra uma solicitação de melhoria, nova feature ou correção de bug para ser implementada pelo orquestrador. Use quando identificar uma limitação, funcionalidade ausente ou problema no devautomation.",
            parameters = new {
                type = "object",
                properties = new {
                    descricao  = new { type = "string", description = "Descrição clara do que precisa ser implementado" },
                    tipo       = new { type = "string", @enum = new[]{"nova_ferramenta","bugfix","feature","config"}, description = "Tipo da solicitação" },
                    impacto    = new { type = "string", @enum = new[]{"baixo","medio","alto"}, description = "Impacto da mudança" },
                    detalhes   = new { type = "string", description = "Detalhes técnicos adicionais (opcional)" },
                    api        = new { type = "string", description = "Projeto alvo: devautomation, salematic, cmsx. Padrão: devautomation" }
                },
                required = new[]{"descricao","tipo","impacto"}
            }
        }
    ];

    public GeminiService(HttpClient http, ILogger<GeminiService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    public async Task<GeminiResponse> SendAsync(
        string apiKey,
        string model,
        string urlVersion,
        string message,
        string systemContext,
        List<GeminiMessage>? history = null,
        bool includeTools = true,
        string? imageBase64 = null,
        string? imageMimeType = null)
    {
        var url = $"https://generativelanguage.googleapis.com/{urlVersion}/models/{model}:generateContent?key={apiKey}";

        var contents = new List<object>(history?.Select(h => (object)new { role = h.Role, parts = h.Parts }) ?? []);

        object userTurn;
        if (!string.IsNullOrEmpty(imageBase64))
        {
            userTurn = new
            {
                role = "user",
                parts = new object[]
                {
                    new { inlineData = new { mimeType = imageMimeType ?? "image/png", data = imageBase64 } },
                    new { text = string.IsNullOrWhiteSpace(message) ? "Analise este fluxo e descreva o que precisa ser implementado." : message }
                }
            };
        }
        else
        {
            userTurn = new { role = "user", parts = new[] { new { text = message } } };
        }
        contents.Add(userTurn);

        var body = new Dictionary<string, object>
        {
            ["system_instruction"] = new { parts = new[] { new { text = systemContext } } },
            ["contents"] = contents
        };

        if (includeTools)
            body["tools"] = new[] { new { function_declarations = AgentTools } };

        var json     = JsonSerializer.Serialize(body);
        _logger.LogDebug("Gemini request body: {Json}", json.Length > 500 ? json[..500] + $"...[{json.Length} chars]" : json);
        var content  = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var raw      = await response.Content.ReadAsStringAsync();

        return ParseResponse(raw);
    }

    public async Task<GeminiResponse> SendToolResultAsync(
        string apiKey,
        string model,
        string urlVersion,
        string systemContext,
        List<GeminiMessage> history,
        string toolName,
        object toolResult)
    {
        var url = $"https://generativelanguage.googleapis.com/{urlVersion}/models/{model}:generateContent?key={apiKey}";

        var contents = new List<object>(history.Select(h => (object)new { role = h.Role, parts = h.Parts }));
        contents.Add(new
        {
            role  = "function",
            parts = new[]
            {
                new {
                    functionResponse = new {
                        name     = toolName,
                        response = new { result = JsonSerializer.Serialize(toolResult) }
                    }
                }
            }
        });

        var body = new
        {
            system_instruction = new { parts = new[] { new { text = systemContext } } },
            contents           = contents,
            tools              = new[] { new { function_declarations = AgentTools } }
        };

        var json     = JsonSerializer.Serialize(body);
        var content  = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var raw      = await response.Content.ReadAsStringAsync();

        return ParseResponse(raw);
    }

    public async Task<float[]> GetEmbeddingAsync(string apiKey, string text)
    {
        var url  = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={apiKey}";
        var body = JsonSerializer.Serialize(new { content = new { parts = new[] { new { text } } } });
        var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var raw  = await resp.Content.ReadAsStringAsync();
        var doc  = JsonNode.Parse(raw);
        var vals = doc?["embedding"]?["values"]?.AsArray();
        if (vals == null)
            throw new InvalidOperationException($"Resposta de embedding inválida: {raw}");
        return [.. vals.Select(v => v!.GetValue<float>())];
    }

    private GeminiResponse ParseResponse(string raw)
    {
        try
        {
            var doc = JsonNode.Parse(raw);

            // Detecta erro retornado pela API do Gemini
            var apiError = doc?["error"];
            if (apiError != null)
            {
                var msg = apiError["message"]?.GetValue<string>() ?? "Erro desconhecido da API Gemini";
                _logger.LogError("Erro da API Gemini: {Msg} | Raw: {Raw}", msg, raw);
                return new GeminiResponse { Type = "error", Text = $"Gemini API: {msg}" };
            }

            var part = doc?["candidates"]?[0]?["content"]?["parts"]?[0];

            if (part?["functionCall"] != null)
            {
                var fc = part["functionCall"]!;
                return new GeminiResponse
                {
                    Type = "toolCall",
                    ToolName = fc["name"]?.GetValue<string>(),
                    ToolArgs = fc["args"]?.AsObject()
                };
            }

            var text = part?["text"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(text))
                _logger.LogWarning("Resposta Gemini sem texto. Raw: {Raw}", raw);

            return new GeminiResponse { Type = "text", Text = text };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parsear resposta Gemini: {Raw}", raw);
            return new GeminiResponse { Type = "error", Text = ex.Message };
        }
    }
}

public record GeminiResponse
{
    public string Type { get; init; } = "text";
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public JsonObject? ToolArgs { get; init; }
}

public record GeminiMessage(string Role, object[] Parts);
