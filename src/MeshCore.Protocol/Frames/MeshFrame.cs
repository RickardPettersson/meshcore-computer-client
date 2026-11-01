namespace MeshCore.Protocol.Frames;

/// <summary>A decoded frame received from the radio.</summary>
/// <param name="Code">The frame's first byte.</param>
public abstract record MeshFrame(byte Code)
{
    /// <summary>True for unsolicited notifications (codes 0x80 and above).</summary>
    public bool IsPush => Code >= 0x80;
}

/// <summary>A frame with a code this library doesn't decode (yet). The full payload is kept.</summary>
public sealed record UnknownFrame(byte Code, ReadOnlyMemory<byte> Payload) : MeshFrame(Code);

/// <summary>A frame with a known code whose body could not be decoded.</summary>
public sealed record MalformedFrame(byte Code, ReadOnlyMemory<byte> Payload, string Reason) : MeshFrame(Code);
