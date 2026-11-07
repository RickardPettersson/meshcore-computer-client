using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Images;

/// <summary>The image host refused the upload or couldn't be reached.</summary>
public sealed class ImageHostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Uploads to catbox.moe (kept) or litterbox.catbox.moe (expires after 1–72 h). Both are free, need no account,
/// don't list uploads anywhere, and answer with the file's direct link as plain text, e.g.
/// <c>https://litter.catbox.moe/a1b2c3.jpg</c> (about 36 characters, short enough for a MeshCore message).
/// </summary>
public sealed partial class ImageHostClient(HttpClient http, IOptions<ImageUploadOptions> options, ILogger<ImageHostClient> logger)
{
    public async Task<Uri> UploadAsync(byte[] jpeg, ImageExpiry expiry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        var o = options.Value;
        var lifetime = expiry.Lifetime();
        var endpoint = lifetime is null ? o.CatboxUrl : o.LitterboxUrl;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("fileupload"), "reqtype");
        if (lifetime is { } l)
        {
            form.Add(new StringContent($"{(int)l.TotalHours}h"), "time");
        }
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "fileToUpload", "image.jpg");

        string body;
        try
        {
            using var response = await http.PostAsync(endpoint, form, ct);
            body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (!response.IsSuccessStatusCode)
            {
                var reason = Truncate(body);
                LogRefused(endpoint.Host, (int)response.StatusCode, reason);
                throw new ImageHostException($"{endpoint.Host} refused the upload (HTTP {(int)response.StatusCode}). Try again later.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreachable(endpoint.Host, ex.Message);
            throw new ImageHostException($"Couldn't reach {endpoint.Host}. Is this computer online?", ex);
        }

        // Success is the link itself; anything else is an error message from the host.
        if (!Uri.TryCreate(body, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            var reason = Truncate(body);
            LogRefused(endpoint.Host, 200, reason);
            throw new ImageHostException($"{endpoint.Host} didn't return a link: {reason}");
        }
        var expiryValue = expiry.ToValue();
        LogUploaded(endpoint.Host, jpeg.Length, expiryValue);
        return url;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "Image uploaded to {Host} ({Bytes} bytes, expiry {Expiry})")]
    private partial void LogUploaded(string host, int bytes, string expiry);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image upload to {Host} refused: HTTP {Status} {Body}")]
    private partial void LogRefused(string host, int status, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image host {Host} unreachable: {Reason}")]
    private partial void LogUnreachable(string host, string reason);
}
