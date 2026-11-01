using System.Buffers.Binary;
using System.Text;

namespace MeshCore.Protocol.Binary;

/// <summary>Builds little-endian frame payloads.</summary>
internal sealed class FrameWriter
{
    private readonly List<byte> _bytes = new(32);

    public int Length => _bytes.Count;

    public FrameWriter Byte(byte value)
    {
        _bytes.Add(value);
        return this;
    }

    public FrameWriter Code(CommandCode code) => Byte((byte)code);

    public FrameWriter UInt16(ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        return Bytes(buf);
    }

    public FrameWriter UInt32(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        return Bytes(buf);
    }

    public FrameWriter Int32(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        return Bytes(buf);
    }

    public FrameWriter UnixTime(DateTimeOffset value) => UInt32(checked((uint)value.ToUnixTimeSeconds()));

    public FrameWriter Bytes(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            _bytes.Add(b);
        }
        return this;
    }

    public FrameWriter Zeros(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _bytes.Add(0);
        }
        return this;
    }

    /// <summary>
    /// Writes <paramref name="value"/> into a NUL-padded field of <paramref name="size"/> bytes.
    /// Text is cut at a character boundary so that at least one terminating NUL remains,
    /// matching the firmware's <c>strzcpy</c>.
    /// </summary>
    public FrameWriter FixedString(string value, int size)
    {
        var encoded = Utf8.Truncate(value, size - 1);
        Bytes(encoded);
        return Zeros(size - encoded.Length);
    }

    /// <summary>Writes UTF-8 text with no terminator (runs to the end of the frame).</summary>
    public FrameWriter Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray()
    {
        if (_bytes.Count > ProtocolLimits.MaxFrameSize)
        {
            throw new ArgumentException(
                $"Command is {_bytes.Count} bytes; the companion firmware accepts at most {ProtocolLimits.MaxFrameSize}. Shorten the text.");
        }
        return [.. _bytes];
    }
}
