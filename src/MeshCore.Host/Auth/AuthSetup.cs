using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace MeshCore.Host.Auth;

public static class AuthPolicies
{
    /// <summary>Signed in and not waiting on a forced password change. The default for every endpoint.</summary>
    public const string User = "User";

    /// <summary><see cref="User"/> plus the Admin role.</summary>
    public const string Admin = "Admin";

    /// <summary>Signed in, even with a pending forced password change (only for change-password and "me").</summary>
    public const string SignedIn = "SignedIn";

    /// <summary>Rate-limit policy for anonymous auth endpoints.</summary>
    public const string AuthRateLimit = "auth";
}

public static class AuthSetup
{
    /// <summary>Registers users, password hashing, JWT bearer auth, policies and the auth rate limit.</summary>
    public static IServiceCollection AddMeshCoreAuth(this IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("MeshCoreComputerClient")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi();
        }

        services.AddSingleton<IUserStore>(new JsonUserStore(Path.Combine(dataDirectory, "appsettings.auth.json")));
        services.AddSingleton<UserService>();
        services.AddSingleton<JwtKeyProvider>();
        services.AddSingleton<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((o, tokens) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = tokens.ValidationParameters();
                o.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedTokensAsync };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.SignedIn, p => p.RequireAuthenticatedUser())
            .AddPolicy(AuthPolicies.User, p => p.RequireAuthenticatedUser().RequireAssertion(NoPendingPasswordChange))
            .AddPolicy(AuthPolicies.Admin, p => p.RequireAuthenticatedUser().RequireAssertion(NoPendingPasswordChange).RequireRole(nameof(UserRole.Admin)))
            // Secure by default: anything without its own [Authorize]/[AllowAnonymous] needs a normal sign-in.
            .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(NoPendingPasswordChange).Build())
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(NoPendingPasswordChange).Build());

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthPolicies.AuthRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }

    private static bool NoPendingPasswordChange(AuthorizationHandlerContext context) =>
        context.User.FindFirst(MeshClaims.MustChangePassword)?.Value != "true";

    /// <summary>
    /// Tokens stay valid only while the user exists, is enabled, and has the same security stamp,
    /// so a password reset, role change or disable signs that user out immediately.
    /// </summary>
    private static async Task RejectRevokedTokensAsync(TokenValidatedContext context)
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        var id = context.Principal?.FindFirst(MeshClaims.UserId)?.Value;
        var stamp = context.Principal?.FindFirst(MeshClaims.SecurityStamp)?.Value;
        var user = id is null ? null : await users.FindAsync(id, context.HttpContext.RequestAborted);
        if (user is not { Enabled: true } || user.SecurityStamp != stamp)
        {
            context.Fail("The token has been revoked.");
        }
    }
}
