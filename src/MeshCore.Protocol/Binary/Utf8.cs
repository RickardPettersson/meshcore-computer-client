using System.Text;

namespace MeshCore.Protocol.Binary;

/// <summary>UTF-8 helpers for the fixed byte budgets in the protocol.</summary>
public static class Utf8
{
    /// <summary>Number of bytes <paramref name="value"/> takes on the wire.</summary>
    public static int ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>Encodes <paramref name="value"/>, keeping at most <paramref name="maxBytes"/>
    /// bytes without splitting a multi-byte character.</summary>
    public static byte[] Truncate(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        var cut = maxBytes;
        // Step back over continuation bytes (10xxxxxx) so we cut before a lead byte.
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }
        return bytes[..cut];
    }
}
