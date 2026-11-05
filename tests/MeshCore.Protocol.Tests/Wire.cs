using System.Buffers.Binary;
using System.Text;

namespace MeshCore.Protocol.Tests;

/// <summary>
/// Builds expected wire bytes independently of the library's own writer,
/// following the layouts in the firmware's <c>MyMesh.cpp</c>.
/// </summary>
internal static class Wire
{
    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] B(params byte[] bytes) => bytes;

    public static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    public static byte[] I16(short v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        return b;
    }

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] I32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>NUL-padded fixed-width field.</summary>
    public static byte[] Fixed(string s, int size)
    {
        var b = new byte[size];
        Encoding.UTF8.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    public static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>A recognisable 32-byte key: 0x01, 0x02, … 0x20.</summary>
    public static byte[] Key => [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    public const string KeyHex = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20";

    public const uint Time = 1_762_000_000; // 2025-11-01T12:26:40Z
}
