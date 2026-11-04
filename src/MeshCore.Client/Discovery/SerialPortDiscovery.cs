using System.Globalization;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MeshCore.Client.Discovery;

/// <summary>Lists serial ports with their USB vendor/product IDs where the OS exposes them.</summary>
public static partial class SerialPortDiscovery
{
    /// <summary>Present serial ports, most likely MeshCore radios first.</summary>
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        IEnumerable<SerialPortInfo> ports;
        if (OperatingSystem.IsWindows())
        {
            ports = GetWindowsPorts();
        }
        else if (OperatingSystem.IsLinux())
        {
            ports = GetLinuxPorts();
        }
        else
        {
            // macOS: no IDs without IOKit; offer the USB-looking call-out devices.
            ports = SerialPort.GetPortNames()
                .Where(p => p.StartsWith("/dev/cu.usb", StringComparison.Ordinal))
                .Select(p => new SerialPortInfo(p, null, null));
        }

        return [.. ports
            .OrderBy(p => p.Likelihood)
            .ThenBy(p => p.PortName.Length)
            .ThenBy(p => p.PortName, StringComparer.Ordinal)];
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SerialPortInfo> GetWindowsPorts()
    {
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, (ushort Vid, ushort Pid)>(StringComparer.OrdinalIgnoreCase);

        // HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_239A&PID_8029&MI_00\<instance>\Device Parameters\PortName
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is not null)
        {
            foreach (var deviceKeyName in usb.GetSubKeyNames())
            {
                var match = WindowsUsbIdRegex().Match(deviceKeyName);
                if (!match.Success)
                {
                    continue;
                }

                var vid = ushort.Parse(match.Groups["vid"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var pid = ushort.Parse(match.Groups["pid"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                using var deviceKey = usb.OpenSubKey(deviceKeyName);
                foreach (var instance in deviceKey?.GetSubKeyNames() ?? [])
                {
                    using var parameters = deviceKey!.OpenSubKey($@"{instance}\Device Parameters");
                    // The registry also remembers unplugged devices; only keep ports that exist now.
                    if (parameters?.GetValue("PortName") is string portName && present.Contains(portName))
                    {
                        ids[portName] = (vid, pid);
                    }
                }
            }
        }

        foreach (var name in present)
        {
            yield return ids.TryGetValue(name, out var id)
                ? new SerialPortInfo(name, id.Vid, id.Pid)
                : new SerialPortInfo(name, null, null);
        }
    }

    [SupportedOSPlatform("linux")]
    private static IEnumerable<SerialPortInfo> GetLinuxPorts()
    {
        const string sysTty = "/sys/class/tty";
        if (!Directory.Exists(sysTty))
        {
            yield break;
        }

        foreach (var ttyDir in Directory.EnumerateDirectories(sysTty))
        {
            var name = Path.GetFileName(ttyDir);
            if (!name.StartsWith("ttyACM", StringComparison.Ordinal) && !name.StartsWith("ttyUSB", StringComparison.Ordinal))
            {
                continue;
            }

            var (vid, pid) = FindLinuxUsbIds(Path.Combine(ttyDir, "device"));
            yield return new SerialPortInfo($"/dev/{name}", vid, pid);
        }
    }

    [SupportedOSPlatform("linux")]
    private static (ushort? Vid, ushort? Pid) FindLinuxUsbIds(string devicePath)
    {
        // device -> .../usbX/1-1/1-1:1.0 ; idVendor lives on the USB device a level or two up.
        var dir = new DirectoryInfo(devicePath).ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo
                  ?? new DirectoryInfo(devicePath);
        for (var level = 0; dir is not null && level < 4; level++, dir = dir.Parent)
        {
            var vidFile = Path.Combine(dir.FullName, "idVendor");
            var pidFile = Path.Combine(dir.FullName, "idProduct");
            if (File.Exists(vidFile) && File.Exists(pidFile))
            {
                return (ParseHex(File.ReadAllText(vidFile)), ParseHex(File.ReadAllText(pidFile)));
            }
        }
        return (null, null);
    }

    private static ushort? ParseHex(string text) =>
        ushort.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"^VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUsbIdRegex();
}
