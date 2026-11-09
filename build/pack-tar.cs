#:property PublishAot=false
// Makes a release .tar.gz with the right file modes, from any OS (Windows has no executable bit to copy).
//
//   dotnet run build/pack-tar.cs -- <folder> <out.tar.gz> <top-folder-name> <executable> [<executable> ...]
//
// Everything in <folder> goes under <top-folder-name>/ in the archive. The named files (paths relative to <folder>)
// get mode 755, everything else 644. Used by build/package.ps1 for the Linux and macOS packages.
using System.Formats.Tar;
using System.IO.Compression;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: pack-tar.cs <folder> <out.tar.gz> <top-folder-name> [executables...]");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var top = args[2];
var executables = args.Skip(3).Select(e => e.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);

const UnixFileMode File644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
const UnixFileMode Mode755 = File644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
var now = DateTimeOffset.UtcNow;

await using (var file = File.Create(output))
await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
{
    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = Mode755, ModificationTime = now });
    foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var name = $"{top}/{Path.GetRelativePath(source, dir).Replace('\\', '/')}/";
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name) { Mode = Mode755, ModificationTime = now });
    }

    var missing = new HashSet<string>(executables, StringComparer.Ordinal);
    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        missing.Remove(relative);
        await using var content = File.OpenRead(path);
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"{top}/{relative}")
        {
            Mode = executables.Contains(relative) ? Mode755 : File644,
            ModificationTime = File.GetLastWriteTimeUtc(path),
            DataStream = content,
        });
    }

    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"Not found in {source}: {string.Join(", ", missing)}");
        return 1;
    }
}

Console.WriteLine($"Wrote {output}");
return 0;
