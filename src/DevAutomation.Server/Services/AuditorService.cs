using DevAutomation.Models;
using System.Text.Json;

namespace DevAutomation.Services;

public class AuditorService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AuditorService> _logger;
    private readonly string _url;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public AuditorService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<AuditorService> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
        _url         = config.GetValue<string>("DevAutomation:AuditorUrl") ?? "http://localhost:8000";
    }

    public async Task<RevisorResponseModel?> VerificarQualidadeAsync(string task, string codigo, string regras)
    {
        var body = new { task, proposed_code = codigo, guidelines = regras };

        using var http     = _httpFactory.CreateClient();
        var response       = await http.PostAsJsonAsync($"{_url}/audit", body);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<RevisorResponseModel>(_jsonOpts);
    }
}
