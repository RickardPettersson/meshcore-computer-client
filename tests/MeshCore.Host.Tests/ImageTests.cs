using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MeshCore.Host.Images;
using SkiaSharp;

namespace MeshCore.Host.Tests;

/// <summary>Test photos: a red left half and a blue right half, optionally with an EXIF block (orientation + a secret).</summary>
internal static class TestPhotos
{
    public const string Secret = "SECRET-CAMERA GPS 59.3293N";

    public static byte[] Jpeg(int width = 400, int height = 200, ushort? orientation = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Blue);
        using (var red = new SKPaint { Color = SKColors.Red })
        {
            surface.Canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = data.ToArray();
        return orientation is { } o ? WithExif(jpeg, o) : jpeg;
    }

    public static byte[] Png(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Inserts an APP1 EXIF segment (TIFF little-endian: Make = <see cref="Secret"/>, Orientation) after SOI.</summary>
    private static byte[] WithExif(byte[] jpeg, ushort orientation)
    {
        var make = Encoding.ASCII.GetBytes(Secret + "\0");
        using var tiff = new MemoryStream();
        using (var w = new BinaryWriter(tiff, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("II"u8); w.Write((ushort)42); w.Write(8u);           // header, IFD0 at 8
            w.Write((ushort)2);                                         // 2 entries
            w.Write((ushort)0x010F); w.Write((ushort)2); w.Write((uint)make.Length); w.Write(8u + 2 + 12 * 2 + 4); // Make → data
            w.Write((ushort)0x0112); w.Write((ushort)3); w.Write(1u); w.Write(orientation); w.Write((ushort)0);    // Orientation
            w.Write(0u);                                                // no next IFD
            w.Write(make);
        }
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff.ToArray()).ToArray();
        var length = payload.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }
}

public class ImageProcessorTests
{
    [Fact]
    public void Photos_come_out_upright_smaller_and_without_any_metadata()
    {
        var photo = TestPhotos.Jpeg(400, 200, orientation: 6); // stored sideways: "rotate 90° clockwise to view"
        Assert.Contains(TestPhotos.Secret, Encoding.ASCII.GetString(photo), StringComparison.Ordinal);

        var result = ImageProcessor.Process(photo, maxDimension: 160, jpegQuality: 80);
        var text = Encoding.ASCII.GetString(result.Jpeg);
        using var output = SKBitmap.Decode(result.Jpeg);

        Assert.Equal((80, 160), (result.Width, result.Height));            // upright (portrait) and shrunk to 160
        Assert.Equal((80, 160), (output.Width, output.Height));
        Assert.DoesNotContain(TestPhotos.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exif", text, StringComparison.Ordinal);
        // Rotating clockwise puts the stored left (red) half on top.
        Assert.True(output.GetPixel(40, 20).Red > 200 && output.GetPixel(40, 20).Blue < 80, $"top is {output.GetPixel(40, 20)}");
        Assert.True(output.GetPixel(40, 140).Blue > 200 && output.GetPixel(40, 140).Red < 80, $"bottom is {output.GetPixel(40, 140)}");
    }

    [Theory]
    [InlineData((ushort)1, 400, 200)]
    [InlineData((ushort)3, 400, 200)]
    [InlineData((ushort)8, 200, 400)]
    public void Every_orientation_gives_the_right_shape(ushort orientation, int width, int height)
    {
        var result = ImageProcessor.Process(TestPhotos.Jpeg(400, 200, orientation), maxDimension: 1600, jpegQuality: 80);

        Assert.Equal((width, height), (result.Width, result.Height)); // small photos aren't enlarged
    }

    [Fact]
    public void Transparent_pngs_become_jpegs_on_white()
    {
        var result = ImageProcessor.Process(TestPhotos.Png(50, 30), 1600, 80);
        using var output = SKBitmap.Decode(result.Jpeg);

        Assert.Equal(0xFF, result.Jpeg[0]);
        Assert.Equal(0xD8, result.Jpeg[1]);
        Assert.True(output.GetPixel(10, 10).Red > 240 && output.GetPixel(10, 10).Blue > 240);
    }

    [Fact]
    public void Files_that_arent_images_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ImageProcessor.Process("MZ this is an exe"u8.ToArray(), 1600, 80));
        Assert.Contains("isn't an image", ex.Message, StringComparison.Ordinal);
    }
}

public class ImageApiTests
{
    private static MultipartFormDataContent Form(byte[] file, string? expires, string name = "photo.jpg")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "file", name);
        if (expires is not null)
        {
            form.Add(new StringContent(expires), "expires");
        }
        return form;
    }

    [Fact]
    public async Task Upload_strips_the_photo_and_returns_a_short_expiring_link()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        var response = await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(orientation: 6), "24h"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var upload = Assert.Single(app.ImageHost.Uploads);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("https://litter.catbox.moe/abc123.jpg", body.GetProperty("url").GetString());
        Assert.Equal("24h", body.GetProperty("expiry").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(23));
        Assert.Equal("litterbox.catbox.moe", upload.Endpoint.Host);
        Assert.Equal("fileupload", upload.Fields["reqtype"]);
        Assert.Equal("24h", upload.Fields["time"]);
        Assert.DoesNotContain(TestPhotos.Secret, Encoding.ASCII.GetString(upload.File), StringComparison.Ordinal); // what left this computer
        Assert.Equal(200, body.GetProperty("width").GetInt32());                                               // upright: 200 × 400
    }

    [Fact]
    public async Task Keep_goes_to_catbox_without_a_time_limit()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        var body = await (await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "keep"))).Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var upload = Assert.Single(app.ImageHost.Uploads);

        Assert.Equal("https://files.catbox.moe/xyz789.jpg", body.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("expiresAt").ValueKind);
        Assert.Equal("catbox.moe", upload.Endpoint.Host);
        Assert.False(upload.Fields.ContainsKey("time"));
    }

    [Fact]
    public async Task Bad_uploads_and_host_errors_come_back_as_clear_problems()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        var notImage = await admin.PostAsync("/api/v1/images", Form("hello"u8.ToArray(), "1h", "notes.txt"));
        var badExpiry = await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "forever"));
        app.ImageHost.Respond = _ => (HttpStatusCode.InternalServerError, "oops");
        var hostDown = await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "1h"));
        app.ImageHost.Respond = _ => (HttpStatusCode.OK, "Error: file type not allowed");
        var noLink = await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "1h"));
        var anonymous = await app.CreateClient().PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "1h"));

        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
        Assert.Contains("isn't an image", await notImage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badExpiry.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, hostDown.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, noLink.StatusCode);
        Assert.Contains("didn't return a link", await noLink.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Turned_off_means_no_button_and_403()
    {
        using var app = new ApiFactory { Settings = [("Images:Enabled", "false")] };
        var admin = await app.AdminClientAsync();
        var browser = new Browser(app);
        await browser.LoginAsync("admin", "correct horse battery");
        await app.WaitForRadioAsync();

        var chat = await browser.PageAsync("/Messages/Channel/0");
        var upload = await admin.PostAsync("/api/v1/images", Form(TestPhotos.Jpeg(), "1h"));

        Assert.DoesNotContain("data-image-button", chat, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Empty(app.ImageHost.Uploads);
    }
}

public class ImageWebTests
{
    [Fact]
    public async Task Chat_page_uploads_through_the_website_and_gets_the_link()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var chat = await browser.PageAsync("/Messages/Channel/0");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestPhotos.Jpeg(orientation: 6));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "IMG_0001.jpg");
        form.Add(new StringContent("72h"), "expires");
        var response = await browser.UploadAsync("/Messages/Channel/0", "/Messages/UploadImage", form);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.Contains("data-image-button", chat, StringComparison.Ordinal);
        Assert.Contains("data-upload-url=\"/Messages/UploadImage\"", chat, StringComparison.Ordinal);
        Assert.Contains("<option value=\"72h\" selected=\"selected\">3 days</option>", chat, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://litter.catbox.moe/abc123.jpg", body.GetProperty("url").GetString());
        Assert.Equal("72h", Assert.Single(app.ImageHost.Uploads).Fields["time"]);
    }

    [Fact]
    public async Task Upload_errors_reach_the_page_as_json()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();
        app.ImageHost.Respond = _ => (HttpStatusCode.ServiceUnavailable, "down");

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(TestPhotos.Jpeg()), "file", "a.jpg");
        var response = await browser.UploadAsync("/Messages/Channel/0", "/Messages/UploadImage", form);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("refused the upload", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }
}
