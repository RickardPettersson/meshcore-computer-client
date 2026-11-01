namespace MeshCore.Protocol.Models;

/// <summary>A node's 32-byte Ed25519 public key, held as lowercase hex.</summary>
public readonly record struct PublicKey
{
    public PublicKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ProtocolLimits.PublicKeySize)
        {
            throw new ArgumentException($"A public key is {ProtocolLimits.PublicKeySize} bytes, got {bytes.Length}.", nameof(bytes));
        }
        Hex = Convert.ToHexStringLower(bytes);
    }

    public string Hex { get; }

    /// <summary>The 6-byte prefix used to address this node in messages.</summary>
    public PublicKeyPrefix Prefix => PublicKeyPrefix.Parse(Hex[..(ProtocolLimits.PublicKeyPrefixSize * 2)]);

    public byte[] ToBytes() => Convert.FromHexString(Hex);

    public bool Matches(PublicKeyPrefix prefix) => Hex.StartsWith(prefix.Hex, StringComparison.Ordinal);

    /// <exception cref="FormatException">Not 64 hex characters.</exception>
    public static PublicKey Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return new PublicKey(Convert.FromHexString(hex));
    }

    public override string ToString() => Hex;
}

/// <summary>The leading bytes of a <see cref="PublicKey"/>, as used in message frames.</summary>
public readonly record struct PublicKeyPrefix
{
    public PublicKeyPrefix(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > ProtocolLimits.PublicKeySize)
        {
            throw new ArgumentException($"A key prefix is 1..{ProtocolLimits.PublicKeySize} bytes, got {bytes.Length}.", nameof(bytes));
        }
        Hex = Convert.ToHexStringLower(bytes);
    }

    public string Hex { get; }

    public byte[] ToBytes() => Convert.FromHexString(Hex);

    public static PublicKeyPrefix Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return new PublicKeyPrefix(Convert.FromHexString(hex));
    }

    public override string ToString() => Hex;
}
