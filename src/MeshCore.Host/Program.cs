using MeshCore.Data;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----------------------------------------------------------
builder.Services.AddOptions<RadioOptions>()
    .Bind(builder.Configuration.GetSection(RadioOptions.SectionName))
    .Validate(o => o.ReconnectDelays.Length > 0, "MeshCore:ReconnectDelays needs at least one value.")
    .ValidateOnStart();

var radioOptions = builder.Configuration.GetSection(RadioOptions.SectionName).Get<RadioOptions>() ?? new RadioOptions();
var dataDirectory = Path.GetFullPath(radioOptions.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);

// ---- Storage ------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Mesh")
                       ?? $"Data Source={Path.Combine(dataDirectory, "meshcore.db")}";
builder.Services.AddDbContextFactory<MeshDbContext>(o => o.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MeshStore>();

// ---- Radio --------------------------------------------------------------------
builder.Services.AddSingleton<MeshRadio>();
builder.Services.AddSingleton<RadioConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioConnectionService>());

var consoleChat = builder.Configuration.GetValue<bool?>("MeshCore:ConsoleChat")
                  ?? (Environment.UserInteractive && !Console.IsInputRedirected);
if (consoleChat)
{
    builder.Services.AddHostedService<ConsoleChatService>();
}

// ---- Auth & API ---------------------------------------------------------------
builder.Services.AddMeshCoreAuth(builder.Configuration, dataDirectory);
builder.Services.AddMeshCoreApi();

var app = builder.Build();

await app.Services.GetRequiredService<MeshStore>().MigrateAsync();
await app.Services.GetRequiredService<JwtKeyProvider>().InitializeAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:EnableDocs"))
{
    app.MapMeshCoreApiDocs();
}

// Minimal, unauthenticated liveness probe; details are behind GET /api/v1/device.
app.MapGet("/healthz", (MeshRadio radio) => Results.Ok(new { radio.Status.State }))
    .AllowAnonymous()
    .ExcludeFromDescription();

if (await app.Services.GetRequiredService<UserService>().IsSetupRequiredAsync())
{
    StartupLog.NoUsers(app.Logger);
}

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "No users yet. Create the first Admin with POST /api/v1/auth/setup (or the website from Phase 5).")]
    public static partial void NoUsers(ILogger logger);
}
