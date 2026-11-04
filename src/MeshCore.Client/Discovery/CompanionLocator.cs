using MeshCore.Client.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCore.Client.Discovery;

/// <summary>Result of a successful probe: the port and an already-connected client.</summary>
public sealed record LocatedCompanion(SerialPortInfo Port, MeshCoreClient Client);

/// <summary>
/// Finds a companion radio by trying ports in likelihood order and keeping the first one
/// that completes the MeshCore handshake.
/// </summary>
public sealed partial class CompanionLocator
{
    private readonly Func<SerialPortInfo, ITransport> _transportFactory;
    private readonly MeshCoreClientOptions _clientOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <param name="transportFactory">Creates the transport for a port; defaults to <see cref="SerialTransport"/>.</param>
    public CompanionLocator(
        MeshCoreClientOptions? clientOptions = null,
        ILoggerFactory? loggerFactory = null,
        Func<SerialPortInfo, ITransport>? transportFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CompanionLocator>();
        _clientOptions = clientOptions ?? new MeshCoreClientOptions();
        _transportFactory = transportFactory ?? (port => new SerialTransport(
            new SerialTransportOptions { PortName = port.PortName },
            _loggerFactory.CreateLogger<SerialTransport>()));
    }

    /// <summary>
    /// Ports worth probing. Ports with USB IDs that match no known board are skipped
    /// unless <paramref name="includeUnlikely"/> is set, so we don't send bytes to unrelated devices.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> Candidates(IEnumerable<SerialPortInfo> ports, bool includeUnlikely = false) =>
        [.. ports.Where(p => includeUnlikely || p.Likelihood != PortLikelihood.Unlikely).OrderBy(p => p.Likelihood)];

    /// <summary>Returns the first port that answers as a MeshCore companion, or null.</summary>
    public async Task<LocatedCompanion?> FindAsync(IEnumerable<SerialPortInfo> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var port in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogProbing(port);
            var client = new MeshCoreClient(_transportFactory(port), _clientOptions, _loggerFactory.CreateLogger<MeshCoreClient>());
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return new LocatedCompanion(port, client);
            }
            catch (Exception ex) when (ex is MeshCoreException or IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Busy (another app has it open), not a MeshCore radio, or a repeater/room firmware.
                LogNotCompanion(port.PortName, ex.Message);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Probing {Port}")]
    private partial void LogProbing(SerialPortInfo port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Port} is not a usable companion radio: {Reason}")]
    private partial void LogNotCompanion(string port, string reason);
}
