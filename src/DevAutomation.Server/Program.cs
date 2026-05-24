using DevAutomation.Hubs;
using DevAutomation.Models;
using DevAutomation.Services;
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
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.TryParse(builder.Configuration["Qdrant:Port"], out var p) ? p : 6334;
builder.Services.AddSingleton<QdrantClient>(_ => new QdrantClient(qdrantHost, qdrantPort));
builder.Services.AddSingleton<ForgeAuditorService>();

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

builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<RagIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagIndexerService>());
builder.Services.AddSingleton<RagService>();
builder.Services.AddSwaggerGen();

builder.WebHost.UseUrls("http://localhost:8080");

var langfusePublicKey = builder.Configuration["Langfuse:PublicKey"] ?? "";
var langfuseSecretKey = builder.Configuration["Langfuse:SecretKey"] ?? "";
var langfuseBaseUrl   = builder.Configuration["Langfuse:BaseUrl"] ?? "https://cloud.langfuse.com";
var langfuseEnabled   = !string.IsNullOrEmpty(langfusePublicKey) && !string.IsNullOrEmpty(langfuseSecretKey);
var langfuseOtlpAuth  = Convert.ToBase64String(
    System.Text.Encoding.UTF8.GetBytes($"{langfusePublicKey}:{langfuseSecretKey}"));

if (!langfuseEnabled)
    Log.Warning("Langfuse desabilitado: Langfuse:PublicKey ou Langfuse:SecretKey não configurados em user-secrets.");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetSampler(new AlwaysOnSampler())
            .AddSource("Forge.SoftwareFactory.Core");

        if (langfuseEnabled)
        {
            // Ao configurar Endpoint via código, o OTel SDK .NET não auto-appenda o signal path.
            // O endpoint deve ser o URL completo do sinal, incluindo /v1/traces.
            tracing.AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri($"{langfuseBaseUrl}/api/public/otel/v1/traces");
                otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                otlp.Headers  = $"Authorization=Basic {langfuseOtlpAuth}";
            });
        }
    });

// Registrar o cliente HTTP para chamadas puras das LLMs
builder.Services.AddHttpClient();

var app = builder.Build();

// ---- Health check Qdrant ----
var qdrantLogger = app.Services.GetRequiredService<ILogger<Program>>();
var qdrant = app.Services.GetRequiredService<QdrantClient>();
var qdrantReady = false;
for (int i = 0; i < 30; i++)
{
    try
    {
        await qdrant.ListCollectionsAsync();
        qdrantReady = true;
        break;
    }
    catch
    {
        qdrantLogger.LogWarning("Qdrant indisponível, aguardando... ({Attempt}/30)", i + 1);
        await Task.Delay(1000);
    }
}
if (!qdrantReady)
    throw new InvalidOperationException("Qdrant não respondeu em 30s. Verifique o container Docker.");
// ---- fim health check ----

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

app.Run();
