namespace MeshCore.Host.Images;

/// <summary>
/// Image upload (<c>Images</c> in appsettings.json). Photos are shrunk, stripped of EXIF (including GPS) and uploaded
/// to catbox.moe (kept) or litterbox.catbox.moe (expires), which return a short, unlisted link to send in a chat.
/// </summary>
public sealed class ImageUploadOptions
{
    public const string SectionName = "Images";

    /// <summary>Largest request the upload endpoints accept (the original photo, before shrinking).</summary>
    public const long MaxRequestBytes = 25L * 1024 * 1024;

    /// <summary>Turn image upload off entirely (the button disappears and the API answers 403).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What the chat picker starts on: <c>1h</c>, <c>12h</c>, <c>24h</c>, <c>72h</c> (Litterbox) or <c>keep</c> (Catbox).</summary>
    public string DefaultExpiry { get; set; } = "72h";

    /// <summary>Longest side after shrinking, in pixels.</summary>
    public int MaxDimension { get; set; } = 1600;

    /// <summary>JPEG quality of the uploaded image, 1–100.</summary>
    public int JpegQuality { get; set; } = 82;

    /// <summary>Largest original photo accepted, in bytes (at most <see cref="MaxRequestBytes"/>).</summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Upload endpoint for kept images.</summary>
    public Uri CatboxUrl { get; set; } = new("https://catbox.moe/user/api.php");

    /// <summary>Upload endpoint for expiring images.</summary>
    public Uri LitterboxUrl { get; set; } = new("https://litterbox.catbox.moe/resources/internals/api.php");

    /// <summary>How long one upload may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>How long an uploaded image stays online.</summary>
public enum ImageExpiry
{
    OneHour,
    TwelveHours,
    OneDay,
    ThreeDays,
    /// <summary>Catbox: kept until it hasn't been viewed for 2 years.</summary>
    Keep,
}

public static class ImageExpiries
{
    /// <summary>Wire values, in picker order.</summary>
    public static readonly IReadOnlyList<(string Value, ImageExpiry Expiry, string Label)> All =
    [
        ("1h", ImageExpiry.OneHour, "1 hour"),
        ("12h", ImageExpiry.TwelveHours, "12 hours"),
        ("24h", ImageExpiry.OneDay, "1 day"),
        ("72h", ImageExpiry.ThreeDays, "3 days"),
        ("keep", ImageExpiry.Keep, "Keep (Catbox)"),
    ];

    public static bool TryParse(string? value, out ImageExpiry expiry)
    {
        foreach (var e in All)
        {
            if (string.Equals(e.Value, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                expiry = e.Expiry;
                return true;
            }
        }
        expiry = default;
        return false;
    }

    public static string ToValue(this ImageExpiry expiry) => All.First(e => e.Expiry == expiry).Value;

    /// <summary>Litterbox's <c>time</c> value, or null for a kept Catbox upload.</summary>
    public static TimeSpan? Lifetime(this ImageExpiry expiry) => expiry switch
    {
        ImageExpiry.OneHour => TimeSpan.FromHours(1),
        ImageExpiry.TwelveHours => TimeSpan.FromHours(12),
        ImageExpiry.OneDay => TimeSpan.FromHours(24),
        ImageExpiry.ThreeDays => TimeSpan.FromHours(72),
        _ => null,
    };
}
