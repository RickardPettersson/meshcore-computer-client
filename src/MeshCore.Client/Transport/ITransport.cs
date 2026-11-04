using System.Threading.Channels;

namespace MeshCore.Client.Transport;

/// <summary>
/// A link to a companion radio that carries whole protocol frames (payloads without any
/// transport framing). USB serial today; BLE or TCP can implement the same contract.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Human-readable endpoint, e.g. <c>COM11</c>.</summary>
    string Description { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends one frame payload (first byte is the command code).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Frame payloads received from the radio. Completes when the link closes,
    /// with the error as the completion exception if it failed (e.g. cable unplugged).</summary>
    ChannelReader<byte[]> Frames { get; }
}
