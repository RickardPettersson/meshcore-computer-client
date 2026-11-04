using MeshCore.Data;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using MeshCore.Host.Realtime;
using MeshCore.Host.Web;
using Microsoft.EntityFrameworkCore;

// One number format everywhere (pages, logs, JSON): "4.05 V", not "4,05 V" depending on the server's Windows locale.
// Times are rendered in UTC and shown in each viewer's own time zone by site.js.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

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

// ---- Website (MVC Backend-for-Frontend over the API) -------------------------
builder.Services.AddMeshCoreWeb(builder.Configuration);
builder.Services.AddMeshCoreRealtime();

var app = builder.Build();

await app.Services.GetRequiredService<MeshStore>().MigrateAsync();
await app.Services.GetRequiredService<JwtKeyProvider>().InitializeAsync();

static bool IsApi(HttpContext http) => http.Request.Path.StartsWithSegments("/api");

// API: problem+json errors. Website: friendly error pages.
app.UseWhen(IsApi, api => api.UseExceptionHandler().UseStatusCodePages());
app.UseWhen(http => !IsApi(http), web =>
{
    if (!app.Environment.IsDevelopment())
    {
        web.UseExceptionHandler("/Account/Error");
    }
    web.UseStatusCodePagesWithReExecute("/Account/Status/{0}");
});
app.UseMeshCoreSecurityHeaders();
app.UseStaticFiles();
app.UseRouting(); // Explicit, after the status-code pages, so re-executed error pages are routed.
app.UseMeshCoreSetupGate();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The API listens on loopback (5081) and the website on the LAN (5080). With SeparatePorts each is
// only served on its own port, so the API can't be reached through the website's port.
var webOptions = app.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
var apiPort = WebSetup.PortOf(app.Configuration["Kestrel:Endpoints:Api:Url"]);
var webPort = WebSetup.PortOf(app.Configuration["Kestrel:Endpoints:Web:Url"]);
var separatePorts = webOptions.SeparatePorts && apiPort is not null && webPort is not null && apiPort != webPort;

var apiEndpoints = app.MapControllers();
var webEndpoints = app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
var webHub = app.MapHub<MeshHub>(MeshHub.WebPath);
var apiHub = app.MapHub<MeshHub>(MeshHub.ApiPath);
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:EnableDocs"))
{
    app.MapMeshCoreApiDocs();
}

// Minimal, unauthenticated liveness probe; details are behind GET /api/v1/device.
var health = app.MapGet("/healthz", (MeshRadio radio) => Results.Ok(new { radio.Status.State }))
    .AllowAnonymous()
    .ExcludeFromDescription();

if (separatePorts)
{
    apiEndpoints.RequireHost($"*:{apiPort}");
    health.RequireHost($"*:{apiPort}");
    webEndpoints.RequireHost($"*:{webPort}");
    webHub.RequireHost($"*:{webPort}");
    apiHub.RequireHost($"*:{apiPort}");
}

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
