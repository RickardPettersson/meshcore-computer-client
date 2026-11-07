using System.Threading.RateLimiting;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Images;

public static class ImageSetup
{
    /// <summary>Uploads per user: enough to share photos, not enough to flood the image host.</summary>
    public const string RateLimit = "images";

    public static IServiceCollection AddMeshCoreImages(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ImageUploadOptions>()
            .Bind(configuration.GetSection(ImageUploadOptions.SectionName))
            .Validate(o => ImageExpiries.TryParse(o.DefaultExpiry, out _), "Images:DefaultExpiry must be 1h, 12h, 24h, 72h or keep.")
            .Validate(o => o.MaxDimension is >= 160 and <= 8000, "Images:MaxDimension must be 160–8000.")
            .ValidateOnStart();

        services.AddHttpClient<ImageHostClient>((sp, c) =>
        {
            c.Timeout = sp.GetRequiredService<IOptions<ImageUploadOptions>>().Value.Timeout;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MeshCoreComputerClient/1.0 (+https://github.com/RickardPettersson/meshcore-computer-client)");
        });

        services.Configure<RateLimiterOptions>(o => o.AddPolicy(RateLimit, http =>
            RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst(MeshClaims.UserId)?.Value ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 })));
        return services;
    }
}
