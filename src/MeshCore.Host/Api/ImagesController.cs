using MeshCore.Host.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Api;

/// <param name="Url">Short, unlisted link to send in a chat.</param>
/// <param name="ExpiresAt">When the image disappears; null when kept on Catbox.</param>
public sealed record ImageUploadResponse(string Url, string Expiry, DateTimeOffset? ExpiresAt, int Bytes, int Width, int Height);

/// <param name="Enabled">False when an admin turned image upload off.</param>
/// <param name="DefaultExpiry">What the picker starts on.</param>
public sealed record ImageSettingsResponse(bool Enabled, string DefaultExpiry, IReadOnlyList<string> Expiries, long MaxUploadBytes);

/// <summary>
/// Share a photo in a chat: it's shrunk, stripped of EXIF (including GPS location) and uploaded to an image host,
/// and you get a short link back to put in a message. Nothing is stored here.
/// </summary>
[ApiController]
[Route("api/v1/images")]
[Tags("Images")]
public sealed class ImagesController(ImageHostClient host, IOptions<ImageUploadOptions> options, TimeProvider time) : ControllerBase
{
    [HttpGet("settings")]
    public ImageSettingsResponse Settings() =>
        new(options.Value.Enabled, options.Value.DefaultExpiry, [.. ImageExpiries.All.Select(e => e.Value)], options.Value.MaxUploadBytes);

    /// <summary>
    /// Uploads an image (multipart field <c>file</c>). <c>expires</c>: <c>1h</c>, <c>12h</c>, <c>24h</c> or <c>72h</c>
    /// (litterbox.catbox.moe, deleted afterwards) or <c>keep</c> (catbox.moe, kept until unviewed for 2 years).
    /// Anyone with the link can open it, but uploads aren't listed or searchable.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageUploadOptions.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageUploadOptions.MaxRequestBytes)]
    [EnableRateLimiting(ImageSetup.RateLimit)]
    [ProducesResponseType<ImageUploadResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? expires, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Image upload is turned off",
                detail: "An admin turned it off (Images:Enabled in appsettings.json).");
        }
        if (file.Length == 0)
        {
            throw new ArgumentException("The file is empty.");
        }
        if (file.Length > o.MaxUploadBytes)
        {
            throw new ArgumentException($"That file is {file.Length / 1024 / 1024.0:0.#} MB; the limit is {o.MaxUploadBytes / 1024 / 1024} MB.");
        }
        var expiry = ImageExpiry.ThreeDays;
        if (!ImageExpiries.TryParse(expires ?? o.DefaultExpiry, out expiry))
        {
            throw new ArgumentException("expires must be 1h, 12h, 24h, 72h or keep.");
        }

        await using var stream = file.OpenReadStream();
        var image = await ImageProcessor.ProcessAsync(stream, o.MaxDimension, o.JpegQuality, ct);
        var url = await host.UploadAsync(image.Jpeg, expiry, ct);
        var expiresAt = expiry.Lifetime() is { } l ? time.GetUtcNow() + l : (DateTimeOffset?)null;
        return StatusCode(StatusCodes.Status201Created,
            new ImageUploadResponse(url.ToString(), expiry.ToValue(), expiresAt, image.Jpeg.Length, image.Width, image.Height));
    }
}
