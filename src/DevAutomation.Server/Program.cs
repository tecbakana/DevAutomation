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

// Detecta o diretório raiz do Forge independente do nome da pasta
var searchDir = new DirectoryInfo(AppContext.BaseDirectory);
while (searchDir != null && !Directory.Exists(Path.Combine(searchDir.FullName, "config")))
    searchDir = searchDir.Parent;
var rootPath = searchDir?.FullName
    ?? throw new InvalidOperationException("Diretório raiz do Forge não encontrado (pasta 'config' ausente).");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.Seq("http://localhost:5341")
    .CreateLogger();

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
builder.Services.AddSingleton<QdrantClient>(_ => new QdrantClient("localhost", 6334));

builder.Services.Configure<OllamaSettings>(
    builder.Configuration.GetSection(OllamaSettings.SectionName));

builder.Services.AddOptions<OllamaSettings>()
    .Bind(builder.Configuration.GetSection(OllamaSettings.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Registro do Embedding Generator usando as Options
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    // Recupera as configurações injetadas
    var settings = sp.GetRequiredService<IOptions<OllamaSettings>>().Value;

    var client = new OllamaEmbeddingGenerator(
        new Uri(settings.Endpoint),
        settings.ModelName);

    // Aqui você pode configurar filtros ou wrappers que usem o NumCtx se necessário
    return client;
});

// No LM Studio, a key pode ser qualquer string, mas o client exige que não seja nula
var lmStudioClient = new OpenAIClient(
    new System.ClientModel.ApiKeyCredential("lm-studio"),
    new OpenAIClientOptions { Endpoint = new Uri("http://localhost:1234/v1") }
);

// Agora registra usando o client configurado
builder.Services.AddChatClient(lmStudioClient.AsChatClient("model-id"));
builder.Services.AddEmbeddingGenerator(lmStudioClient.AsEmbeddingGenerator("nomic-embed-text"));

builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
builder.Services.AddSingleton<RagIndexerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagIndexerService>());
builder.Services.AddSingleton<RagService>();
builder.Services.AddSwaggerGen();

builder.WebHost.UseUrls("http://localhost:8080");

var app = builder.Build();

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
