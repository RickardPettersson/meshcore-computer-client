using MeshCore.Data;
using MeshCore.Host.Radio;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----------------------------------------------------------
builder.Services.AddOptions<RadioOptions>()
    .Bind(builder.Configuration.GetSection(RadioOptions.SectionName))
    .Validate(o => o.ReconnectDelays.Length > 0, "MeshCore:ReconnectDelays needs at least one value.")
    .ValidateOnStart();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

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

var app = builder.Build();

await app.Services.GetRequiredService<MeshStore>().MigrateAsync();

// Placeholder until the REST API arrives in Phase 4.
app.MapGet("/healthz", (MeshRadio radio) => Results.Ok(new
{
    radio.Status.State,
    radio.Status.Endpoint,
    Node = radio.Status.Self?.Name,
    Model = radio.Status.Device?.Model,
    Firmware = radio.Status.Device?.FirmwareVersion,
    radio.Status.Since,
    radio.Status.LastError,
    radio.Status.NextAttempt,
}));

await app.RunAsync();
