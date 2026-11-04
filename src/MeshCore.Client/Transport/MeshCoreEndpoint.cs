using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCore.Client.Transport;

/// <summary>
/// Where a companion radio lives: a serial port (USB firmware) or a TCP address (WiFi firmware).
/// Parse from config or the command line with <see cref="Parse"/>.
/// </summary>
public abstract record MeshCoreEndpoint
{
    public abstract ITransport CreateTransport(ILoggerFactory? loggerFactory = null);

    /// <summary>
    /// Accepts <c>tcp://host[:port]</c>, <c>host:port</c>, a bare IP address (port 5000),
    /// <c>serial://COM11</c>, <c>COM11</c> or <c>/dev/ttyACM0</c>.
    /// </summary>
    /// <exception cref="FormatException">The text matches none of these forms.</exception>
    public static MeshCoreEndpoint Parse(string text) =>
        TryParse(text, out var endpoint) ? endpoint : throw new FormatException(
            $"'{text}' is not a radio address. Use e.g. tcp://192.168.1.212:5000, 192.168.1.212, COM11 or /dev/ttyACM0.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out MeshCoreEndpoint? endpoint)
    {
        endpoint = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith("serial://", StringComparison.OrdinalIgnoreCase))
        {
            var port = text["serial://".Length..];
            endpoint = port.Length > 0 ? new SerialEndpoint(port) : null;
            return endpoint is not null;
        }

        if (text.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.AsSpan(3), CultureInfo.InvariantCulture, out _)
            || text.StartsWith("/dev/", StringComparison.Ordinal))
        {
            endpoint = new SerialEndpoint(text);
            return true;
        }

        var hostPart = text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? text["tcp://".Length..] : text;
        var explicitTcp = hostPart.Length != text.Length;

        // [v6]:port, host:port, or host / IP alone.
        if (IPEndPoint.TryParse(hostPart, out var ip) && (ip.Port != 0 || IPAddress.TryParse(hostPart, out _)))
        {
            endpoint = new TcpEndpoint(ip.Address.ToString(), ip.Port == 0 ? TcpTransport.DefaultPort : ip.Port);
            return true;
        }

        var colon = hostPart.LastIndexOf(':');
        if (colon > 0 && int.TryParse(hostPart.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535)
        {
            endpoint = new TcpEndpoint(hostPart[..colon], p);
            return true;
        }

        if (explicitTcp && Uri.CheckHostName(hostPart) != UriHostNameType.Unknown)
        {
            endpoint = new TcpEndpoint(hostPart, TcpTransport.DefaultPort);
            return true;
        }

        return false;
    }
}

public sealed record SerialEndpoint(string PortName) : MeshCoreEndpoint
{
    public override ITransport CreateTransport(ILoggerFactory? loggerFactory = null) =>
        new SerialTransport(new SerialTransportOptions { PortName = PortName },
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SerialTransport>());

    public override string ToString() => $"serial://{PortName}";
}

public sealed record TcpEndpoint(string Host, int Port = TcpTransport.DefaultPort) : MeshCoreEndpoint
{
    public override ITransport CreateTransport(ILoggerFactory? loggerFactory = null) =>
        new TcpTransport(new TcpTransportOptions { Host = Host, Port = Port },
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TcpTransport>());

    public override string ToString() => Host.Contains(':', StringComparison.Ordinal) ? $"tcp://[{Host}]:{Port}" : $"tcp://{Host}:{Port}";
}
