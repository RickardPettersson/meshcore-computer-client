using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MeshCore.Host.Web;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Where the website reaches the API. Defaults to the Kestrel <c>Api</c> endpoint on loopback.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Serve the API only on the API port and the website only on the web port. On by default, so the
    /// API (bound to 127.0.0.1) is never reachable through the LAN-facing website port.
    /// </summary>
    public bool SeparatePorts { get; set; } = true;

    /// <summary>Send auth cookies only over HTTPS. Turn on once HTTPS is set up (Phase 8).</summary>
    public bool RequireHttps { get; set; }
}

public static class WebSetup
{
    public const string AuthCookieName = "MeshCore.Auth";

    public static IServiceCollection AddMeshCoreWeb(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
        var apiBase = options.ApiBaseUrl ?? LoopbackUrl(configuration["Kestrel:Endpoints:Api:Url"]) ?? "http://127.0.0.1:5081";

        services.AddControllersWithViews();
        services.AddHttpContextAccessor();
        services.AddTransient<ApiTokenHandler>();
        services.AddHttpClient<MeshApiClient>(c =>
            {
                c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/api/v1/");
                c.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<ApiTokenHandler>();

        services.AddSingleton<SetupGate>();
        services.AddScoped<ForcePasswordChangeFilter>();
        services.AddScoped<ApiErrorFilter>();

        var secure = options.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            o.Cookie.Name = AuthCookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = secure;
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Account/AccessDenied";
            o.SlidingExpiration = false; // The cookie expires with the API token inside it.
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                // A user with a temporary password is "denied" everywhere; send them to change it instead.
                var pending = ctx.HttpContext.User.FindFirst(MeshClaims.MustChangePassword)?.Value == "true";
                ctx.Response.Redirect(pending ? "/Account/ChangePassword" : ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });

        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "MeshCore.Antiforgery";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = secure;
        });

        return services;
    }

    /// <summary>Security headers for website pages. Everything is self-hosted, so the CSP can be strict.</summary>
    public static IApplicationBuilder UseMeshCoreSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!http.Request.Path.StartsWithSegments("/api") && !http.Request.Path.StartsWithSegments("/scalar"))
        {
            var headers = http.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
                                            "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        }
        await next();
    });

    /// <summary>
    /// Until the first Admin exists, every website page redirects to setup. Runs before authentication
    /// so a fresh install shows setup rather than a sign-in page nobody can use yet.
    /// </summary>
    public static IApplicationBuilder UseMeshCoreSetupGate(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var path = http.Request.Path;
        var isPage = HttpMethods.IsGet(http.Request.Method)
                     && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/Account/Setup")
                     && !path.StartsWithSegments("/Account/Status") && !path.StartsWithSegments("/healthz")
                     && !path.StartsWithSegments("/openapi") && !path.StartsWithSegments("/scalar");
        if (isPage && await http.RequestServices.GetRequiredService<SetupGate>().IsSetupRequiredAsync(http.RequestAborted))
        {
            http.Response.Redirect("/Account/Setup");
            return;
        }
        await next();
    });

    /// <summary>The port of a Kestrel endpoint URL from config, if one is set.</summary>
    public static int? PortOf(string? url) =>
        url is not null && Uri.TryCreate(url.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri.Port
            : null;

    private static string? LoopbackUrl(string? url) =>
        PortOf(url) is { } port ? $"http://127.0.0.1:{port}" : null;
}
