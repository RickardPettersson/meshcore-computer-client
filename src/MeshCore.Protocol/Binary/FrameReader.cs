using System.Buffers.Binary;
using System.Text;

namespace MeshCore.Protocol.Binary;

/// <summary>Sequential little-endian reader over a frame payload.</summary>
internal ref struct FrameReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public byte ReadByte() => Take(1)[0];

    public sbyte ReadSByte() => unchecked((sbyte)Take(1)[0]);

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    public void Skip(int count) => Take(count);

    public DateTimeOffset ReadUnixTime() => DateTimeOffset.FromUnixTimeSeconds(ReadUInt32());

    /// <summary>Reads a fixed-width, NUL-padded UTF-8 field.</summary>
    public string ReadFixedString(int size) => DecodeUntilNul(Take(size));

    /// <summary>Reads everything that is left as UTF-8 text.</summary>
    public string ReadRemainingString() => DecodeUntilNul(Take(Remaining));

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ProtocolException($"Frame too short: needed {count} more byte(s) at offset {_position}, {Remaining} left.");
        }

        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    private static string DecodeUntilNul(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }
}
