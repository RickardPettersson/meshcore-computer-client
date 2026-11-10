using System.Reflection;
using System.Runtime.InteropServices;
using MeshCore.Host.Images;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace MeshCore.Host.Hosting;

/// <summary>
/// <c>MeshCore.Host --self-check</c>: loads the native libraries a published build depends on (SQLite, SkiaSharp), does a tiny
/// bit of work with each, prints the result and exits. The release build runs it on every platform it ships.
/// </summary>
internal static class SelfCheck
{
    public const string Argument = "--self-check";

    public static int Run()
    {
        var version = AppVersion.Current;
        Console.WriteLine($"MeshCore.Host {version} on {RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.OSDescription})");
        var ok = Check("SQLite", () =>
        {
            using var db = new SqliteConnection("Data Source=:memory:");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "select sqlite_version()";
            return $"version {command.ExecuteScalar()}";
        });
        ok &= Check("SkiaSharp", () =>
        {
            using var bitmap = new SKBitmap(64, 48);
            bitmap.Erase(SKColors.SteelBlue);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var jpeg = ImageProcessor.Process(png.ToArray(), 32, 80);
            return jpeg.Width == 32 && jpeg.Height == 24 ? "PNG → JPEG ok" : throw new InvalidOperationException($"resized to {jpeg.Width}×{jpeg.Height}");
        });
        Console.WriteLine(ok ? "Self-check passed." : "Self-check FAILED.");
        return ok ? 0 : 1;
    }

    private static bool Check(string name, Func<string> check)
    {
        try
        {
            Console.WriteLine($"  {name}: {check()}");
            return true;
        }
#pragma warning disable CA1031 // report any failure, whatever it is
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine($"  {name}: FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
