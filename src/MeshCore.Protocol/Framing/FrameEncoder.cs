using System.Buffers.Binary;

namespace MeshCore.Protocol.Framing;

/// <summary>Wraps protocol payloads in serial frames.</summary>
public static class FrameEncoder
{
    /// <summary>
    /// Builds a serial frame for <paramref name="payload"/>.
    /// </summary>
    /// <param name="payload">Command payload; the first byte is the command code.</param>
    /// <param name="marker">Start byte; defaults to <see cref="FrameMarker.ToRadio"/>.
    /// Use <see cref="FrameMarker.FromRadio"/> to emulate a radio (e.g. in tests).</param>
    /// <exception cref="ArgumentException">The payload is empty or larger than <see cref="ProtocolLimits.MaxFrameSize"/>.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> payload, byte marker = FrameMarker.ToRadio)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A frame payload cannot be empty.", nameof(payload));
        }

        if (payload.Length > ProtocolLimits.MaxFrameSize)
        {
            throw new ArgumentException(
                $"Frame payload is {payload.Length} bytes; the companion firmware accepts at most {ProtocolLimits.MaxFrameSize}.",
                nameof(payload));
        }

        var frame = new byte[FrameMarker.HeaderSize + payload.Length];
        frame[0] = marker;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(FrameMarker.HeaderSize));
        return frame;
    }
}
