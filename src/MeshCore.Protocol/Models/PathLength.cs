namespace MeshCore.Protocol.Models;

/// <summary>
/// The firmware's packed <c>path_len</c> byte: the low 6 bits are the hop count and the
/// top 2 bits are the per-hop hash size minus one. <c>0xFF</c> means "no path"
/// (an unknown out-path on a contact, or a direct, non-flooded message).
/// </summary>
public readonly record struct PathLength(byte Raw)
{
    public const byte NoneValue = 0xFF;

    public static PathLength None => new(NoneValue);

    public bool IsNone => Raw == NoneValue;

    /// <summary>Number of hops, or 0 when <see cref="IsNone"/>.</summary>
    public int HopCount => IsNone ? 0 : Raw & 0x3F;

    /// <summary>Bytes per hop hash (1–4).</summary>
    public int HashSize => IsNone ? 0 : (Raw >> 6) + 1;

    /// <summary>Bytes the path occupies.</summary>
    public int ByteLength => HopCount * HashSize;

    public override string ToString() => IsNone ? "none" : $"{HopCount} hop(s)";
}
