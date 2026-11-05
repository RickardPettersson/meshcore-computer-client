using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using Serilog.Events;

namespace MeshCore.Host.Hosting;

/// <summary>What the website knows about its HTTPS setup (null certificates when HTTPS is off).</summary>
public sealed record HttpsState(HttpsOptions Options, HttpsCertificates? Certificates)
{
    public bool Enabled => Certificates is not null;
}

public static class HostingSetup
{
    public const string ServiceName = "MeshCoreComputerClient";

    /// <summary>
    /// Where appsettings.json and the data folder live. Normally the current folder (e.g. <c>dotnet run</c> in the
    /// project). When started from elsewhere (a Windows service starts in System32, a shortcut may start anywhere),
    /// use the folder the app is installed in instead.
    /// </summary>
    public static WebApplicationOptions Options(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        var appFolder = AppContext.BaseDirectory;
        var useAppFolder = WindowsServiceHelpers.IsWindowsService()
                           || (!File.Exists(Path.Combine(cwd, "appsettings.json")) && File.Exists(Path.Combine(appFolder, "appsettings.json")));
        return new WebApplicationOptions { Args = args, ContentRootPath = useAppFolder ? appFolder : null };
    }

    /// <summary>Service support, log files, and HTTPS for the website.</summary>
    public static HttpsState AddMeshCoreHosting(this WebApplicationBuilder builder, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Runs as a Windows service or systemd unit when started by one; does nothing otherwise.
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        builder.Host.UseSystemd();

        // Console as before, plus daily log files kept for two weeks. Levels come from the "Serilog" section.
        var logFolder = Path.Combine(dataDirectory, "logs");
        builder.Host.UseSerilog((context, services, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u4} {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .WriteTo.File(Path.Combine(logFolder, "meshcore-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        var https = builder.Configuration.GetSection(HttpsOptions.SectionName).Get<HttpsOptions>() ?? new HttpsOptions();
        HttpsCertificates? certificates = null;
        if (https.Enabled)
        {
            // Same key folder and application name as the app's own Data Protection setup, so both share one key ring.
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dataDirectory, "keys")), o =>
            {
                o.SetApplicationName("MeshCoreComputerClient");
                if (OperatingSystem.IsWindows())
                {
                    o.ProtectKeysWithDpapi();
                }
            });
            certificates = HttpsCertificates.Load(https, Path.Combine(dataDirectory, "https"), protection, TimeProvider.System);

            builder.Configuration.AddInMemoryCollection([new("Kestrel:Endpoints:WebHttps:Url", $"https://0.0.0.0:{https.Port}")]);
            var site = certificates.SiteCertificate;
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = site));
        }

        var state = new HttpsState(https, certificates);
        builder.Services.AddSingleton(state);
        return state;
    }

    /// <summary>
    /// Sends website visitors on plain HTTP to HTTPS. The API (loopback) and the certificate help pages are left alone,
    /// so a phone can fetch the certificate before it trusts the site.
    /// </summary>
    public static IApplicationBuilder UseMeshCoreHttpsRedirect(this IApplicationBuilder app, HttpsState https, int? webHttpPort) => app.Use(async (http, next) =>
    {
        var path = http.Request.Path;
        var redirect = https.Enabled && https.Options.RedirectHttp && !http.Request.IsHttps
                       && (webHttpPort is null || http.Connection.LocalPort == webHttpPort)
                       && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/healthz")
                       && !path.StartsWithSegments("/Account/Certificate") && !path.StartsWithSegments("/Account/CertificateFile");
        if (redirect)
        {
            var target = $"https://{http.Request.Host.Host}:{https.Options.Port}{http.Request.PathBase}{path}{http.Request.QueryString}";
            http.Response.Redirect(target, permanent: false, preserveMethod: true);
            return;
        }
        await next();
    });

    public static void LogHttps(this WebApplication app, HttpsState https)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (https.Certificates is { } c)
        {
            var which = c.Authority is null ? "your own certificate" : c.Created ? "a new site certificate" : "the saved site certificate";
            var names = string.Join(", ", HttpsCertificates.HostNames());
            HostingLog.Https(app.Logger, https.Options.Port, which, names);
        }
    }
}

internal static partial class HostingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Website HTTPS on port {Port} with {Certificate}, valid for: {Names}")]
    public static partial void Https(Microsoft.Extensions.Logging.ILogger logger, int port, string certificate, string names);
}
