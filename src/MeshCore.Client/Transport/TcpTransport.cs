using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MeshCore.Client.Transport;

public sealed class TcpTransportOptions
{
    /// <summary>The companion's IP address or host name.</summary>
    public required string Host { get; init; }

    /// <summary>The companion WiFi firmware listens on 5000 by default.</summary>
    public int Port { get; init; } = TcpTransport.DefaultPort;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// TCP keep-alive probes, so a radio that loses power or WiFi is noticed in about
    /// <see cref="KeepAliveTime"/> + 3 × <see cref="KeepAliveInterval"/> instead of never.
    /// </summary>
    public TimeSpan KeepAliveTime { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// MeshCore companion link over WiFi (companion WiFi firmware, <c>SerialWifiInterface</c>).
/// The framing is identical to serial. The radio serves one client at a time; a new
/// connection silently replaces the previous one.
/// </summary>
public sealed class TcpTransport(TcpTransportOptions options, ILogger<TcpTransport>? logger = null)
    : StreamTransport(logger)
{
    public const int DefaultPort = 5000;
    private const int KeepAliveRetryCount = 3;

    private readonly TcpTransportOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private Socket? _socket;

    public override string Description => $"{_options.Host}:{_options.Port}";

    protected override async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)_options.KeepAliveTime.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)_options.KeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveRetryCount);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MeshCoreTimeoutException(
                    $"Could not reach {Description} within {_options.ConnectTimeout.TotalSeconds:0} s. Is the radio on the same network?");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        return new NetworkStream(socket, ownsSocket: true);
    }

    protected override void CloseLink()
    {
        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // Already reset by the radio.
        }
        _socket?.Close();
    }
}
