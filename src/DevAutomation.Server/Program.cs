using System.Diagnostics;
using System.Runtime.InteropServices;
using DevAutomation.Hubs;
using DevAutomation.Models;
using DevAutomation.Services;
using DevAutomation.Services.Orchestration;
using DevAutomation.Services.Store;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using OpenAI;
using Qdrant.Client;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.ClientModel;
using OpenAI.Chat;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Detecta o diretório raiz do Forge independente do nome da pasta
var searchDir = new DirectoryInfo(AppContext.BaseDirectory);
while (searchDir != null && !Directory.Exists(Path.Combine(searchDir.FullName, "config")))
    searchDir = searchDir.Parent;
var rootPath = searchDir?.FullName
    ?? throw new InvalidOperationException("Diretório raiz do Forge não encontrado (pasta 'config' ausente).");

#pragma warning disable CA1305
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.Seq("http://localhost:5341")
    .CreateLogger();
#pragma warning restore CA1305

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Configuration["DevAutomation:RootPath"]      = rootPath;
builder.Configuration["DevAutomation:ConfigFile"]    = Path.Combine(rootPath, "config", "environments.json");
builder.Configuration["DevAutomation:StateFile"]     = Path.Combine(rootPath, "config", "state.json");
builder.Configuration["DevAutomation:TemplatesDir"]  = Path.Combine(rootPath, "templates");
builder.Configuration["DevAutomation:PanelDir"]      = Path.Combine(rootPath, "panel");
builder.Configuration["DevAutomation:ScriptsDir"]    = Path.Combine(rootPath, "scripts");
builder.Configuration["DevAutomation:DevRequestsDir"]= Path.Combine(rootPath, "dev-requests");
builder.Configuration["DevAutomation:SwitchScript"]  = Path.Combine(rootPath, "scripts", "Switch-Environment.ps1");

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddSingleton<ClaudeService>();
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.TryParse(builder.Configuration["Qdrant:Port"], out var p) ? p : 6334;
builder.Services.AddSingleton<QdrantClient>(_ => new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<ForgeAuditorService>();
builder.Services.AddSingleton<DevRequestService>();

builder.Services.Configure<OllamaSettings>(
    builder.Configuration.GetSection(OllamaSettings.SectionName));

builder.Services.AddOptions<OllamaSettings>()
    .Bind(builder.Configuration.GetSection(OllamaSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<OllamaSettings>>().Value;
    return new OllamaEmbeddingGenerator(new Uri(settings.Endpoint), settings.ModelName);
});

builder.Services.AddSingleton<IDevRequestStore>(sp =>
{
    var cfg       = sp.GetRequiredService<IConfiguration>();
    var storeType = cfg["DevAutomation:StoreType"]?.ToLowerInvariant() ?? "json";
    var devReqDir = cfg["DevAutomation:DevRequestsDir"]!;

    return storeType switch
    {
        "mongo"  => (IDevRequestStore)new MongoDevRequestStore(
                        cfg["DevAutomation:MongoConnectionString"] ?? "mongodb://localhost:27017"),
        "sqlite" => new SqliteDevRequestStore(
                        cfg["DevAutomation:SqliteConnectionString"]
                        ?? $"Data Source={Path.Combine(devReqDir, "devrequests.db")}"),
        _        => new JsonFileDevRequestStore(devReqDir)
    };
});
builder.Services.AddSingleton<IOrchestrationStrategy>(sp =>
{
    var cfg           = sp.GetRequiredService<IConfiguration>();
    var flags         = sp.GetRequiredService<FeatureFlags>();
    var claudePath    = cfg["DevAutomation:ClaudePath"] ?? "claude";
    var forceStrategy = cfg["DevAutomation:OrchestratorStrategy"]?.ToLowerInvariant() ?? "auto";

    if (forceStrategy == "noop")
        return new NoopStrategy();

    if (flags.OrchestratorAvailable)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClaudeCliStrategy>();
        return new ClaudeCliStrategy(claudePath, logger);
    }

    return new NoopStrategy();
});
builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<RagIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagIndexerService>());
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<FeatureFlags>();
builder.Services.AddSwaggerGen();

builder.WebHost.UseUrls("http://+:8080");

var langfusePublicKey = builder.Configuration["Langfuse:PublicKey"] ?? "";
var langfuseSecretKey = builder.Configuration["Langfuse:SecretKey"] ?? "";
var langfuseBaseUrl   = builder.Configuration["Langfuse:BaseUrl"] ?? "https://cloud.langfuse.com";
var langfuseEnabled   = !string.IsNullOrEmpty(langfusePublicKey) && !string.IsNullOrEmpty(langfuseSecretKey);
var langfuseOtlpAuth  = Convert.ToBase64String(
    System.Text.Encoding.UTF8.GetBytes($"{langfusePublicKey}:{langfuseSecretKey}"));

if (!langfuseEnabled)
    Log.Warning("Langfuse desabilitado: Langfuse:PublicKey ou Langfuse:SecretKey não configurados em user-secrets.");

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("Forge"))
    .WithTracing(tracing =>
    {
        tracing
            .SetSampler(new AlwaysOnSampler())
            .AddSource("Forge.SoftwareFactory.Core");

        if (langfuseEnabled)
        {
            tracing.AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri($"{langfuseBaseUrl}/api/public/otel/v1/traces");
                otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                otlp.Headers  = $"Authorization=Basic {langfuseOtlpAuth},x-langfuse-ingestion-version=4";
            });
        }
    });

// Registrar o cliente HTTP para chamadas puras das LLMs
builder.Services.AddHttpClient();

var app = builder.Build();

// ── Feature Detection ──────────────────────────────────────────────────────────
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var featureFlags  = app.Services.GetRequiredService<FeatureFlags>();

// Claude CLI
var claudeCfgPath = builder.Configuration["DevAutomation:ClaudePath"] ?? "claude";
featureFlags.OrchestratorAvailable = IsCliAvailable(claudeCfgPath);
featureFlags.DevAgentClaudeEnabled  = featureFlags.OrchestratorAvailable;
if (!featureFlags.OrchestratorAvailable)
    startupLogger.LogWarning("Claude CLI não encontrado em '{Path}'. OrchestratorAvailable=false.", claudeCfgPath);

// Gemini
var geminiKey = builder.Configuration["agent:apiKey"] ?? builder.Configuration["Agent:apiKey"] ?? "";
featureFlags.DevAgentGeminiEnabled = !string.IsNullOrWhiteSpace(geminiKey);

// Qdrant
var qdrant      = app.Services.GetRequiredService<QdrantClient>();
var qdrantReady = false;
for (int i = 0; i < 3; i++)
{
    try { await qdrant.ListCollectionsAsync(); qdrantReady = true; break; }
    catch { startupLogger.LogWarning("Qdrant indisponível ({Attempt}/3)", i + 1); await Task.Delay(1000); }
}
featureFlags.QdrantAvailable = qdrantReady;
if (!qdrantReady)
    startupLogger.LogWarning("Qdrant não respondeu. RAG será desabilitado.");

// Ollama
var ollamaEndpoint = builder.Configuration[$"{OllamaSettings.SectionName}:Endpoint"] ?? "http://localhost:11434";
var ollamaReady    = false;
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var resp = await http.GetAsync(ollamaEndpoint + "/api/tags");
    ollamaReady = resp.IsSuccessStatusCode;
}
catch
{
    startupLogger.LogWarning("Ollama não respondeu em '{Endpoint}'. RAG será desabilitado.", ollamaEndpoint);
}
featureFlags.OllamaAvailable = ollamaReady;

featureFlags.RagEnabled = qdrantReady && ollamaReady;
startupLogger.LogInformation(
    "Feature detection — RAG: {Rag} | Qdrant: {Q} | Ollama: {O} | Orquestrador: {Orch} | Gemini: {Gem} | Claude: {Cla}",
    featureFlags.RagEnabled, qdrantReady, ollamaReady,
    featureFlags.OrchestratorAvailable, featureFlags.DevAgentGeminiEnabled, featureFlags.DevAgentClaudeEnabled);
// ── fim feature detection ───────────────────────────────────────────────────────

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forge API v1");
    c.RoutePrefix = "swagger";
});

// Serve o painel HTML estático da pasta original
var panelDir = builder.Configuration["DevAutomation:PanelDir"]!;
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(panelDir),
    RequestPath  = ""
});

app.MapFallback(async context =>
{
    if (!context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/hub"))
    {
        var indexPath = Path.Combine(panelDir, "index.html");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath);
    }
});

app.MapControllers();
app.MapHub<OrchestratorHub>("/hub");

// Abre o browser após o servidor estar pronto — funciona com dotnet run, bat ou qualquer launcher
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var openBrowser = !string.Equals(
    app.Configuration["DevAutomation:NoBrowser"], "true",
    StringComparison.OrdinalIgnoreCase);

if (openBrowser)
{
    lifetime.ApplicationStarted.Register(() =>
    {
        var url = "http://localhost:8080";
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { /* ambiente sem display (ex: container) — ignora silenciosamente */ }
    });
}

app.Run();

static bool IsCliAvailable(string command)
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
