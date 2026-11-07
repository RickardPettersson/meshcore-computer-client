using SkiaSharp;

namespace MeshCore.Host.Images;

/// <summary>A photo ready to upload: re-encoded JPEG with no metadata.</summary>
public sealed record ProcessedImage(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Makes a photo safe and small to share: checks it really is an image, turns it upright (phones store rotation in
/// EXIF), shrinks it, and draws it into a fresh JPEG. Only pixels are copied, so <b>no metadata survives</b>
/// (EXIF incl. GPS location, camera, IPTC, XMP). Transparent areas become white; animations keep their first frame.
/// </summary>
public static class ImageProcessor
{
    /// <summary>Refuse decompression bombs: a small file that expands to a huge bitmap.</summary>
    public const long MaxPixels = 50_000_000;

    public static async Task<ProcessedImage> ProcessAsync(Stream input, int maxDimension, int jpegQuality, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        return Process(buffer.ToArray(), maxDimension, jpegQuality);
    }

    public static ProcessedImage Process(byte[] data, int maxDimension, int jpegQuality)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData)
            ?? throw new ArgumentException("That file isn't an image this app can read (JPEG, PNG, GIF, WebP or BMP).");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
        {
            throw new ArgumentException($"That image is too large ({info.Width} × {info.Height} pixels).");
        }

        // Let the decoder skip detail we'd throw away anyway (JPEG decodes at k/8 of full size): less memory.
        // Take the smallest step that's still at least the target size; the resize below does the rest.
        var target = Math.Min(maxDimension, Math.Max(info.Width, info.Height));
        var decodeSize = info.Size;
        for (var k = 1; k < 8; k++)
        {
            var candidate = codec.GetScaledDimensions(k / 8f);
            if (candidate.Width >= 1 && candidate.Height >= 1 && Math.Max(candidate.Width, candidate.Height) >= target)
            {
                decodeSize = candidate;
                break;
            }
        }
        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ArgumentException($"That image couldn't be decoded ({result}).");
        }

        // Upright size after the EXIF rotation, then shrunk to fit maxDimension.
        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (uprightW, uprightH) = swap ? (decoded.Height, decoded.Width) : (decoded.Width, decoded.Height);
        var fit = Math.Min(1f, (float)maxDimension / Math.Max(uprightW, uprightH));
        var width = Math.Max(1, (int)Math.Round(uprightW * fit));
        var height = Math.Max(1, (int)Math.Round(uprightH * fit));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Couldn't allocate the output image.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale((float)width / uprightW, (float)height / uprightH);
        canvas.Concat(OrientationMatrix(origin, decoded.Width, decoded.Height));
        using (var source = SKImage.FromBitmap(decoded))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 1, 100))
            ?? throw new InvalidOperationException("Couldn't encode the JPEG.");
        return new ProcessedImage(jpeg.ToArray(), width, height);
    }

    /// <summary>Maps the stored (sideways/mirrored) pixels onto an upright canvas, per the EXIF orientation tag.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 180°
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // mirrored vertically
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transposed
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 90° clockwise
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 90° counter-clockwise
        _ => SKMatrix.Identity,
    };
}
