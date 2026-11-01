using MeshCore.Protocol.Binary;
using MeshCore.Protocol.Models;
using static MeshCore.Protocol.Tests.Wire;

namespace MeshCore.Protocol.Tests;

public class ModelTests
{
    [Fact]
    public void PublicKey_round_trips_and_exposes_prefix()
    {
        var key = PublicKey.Parse(KeyHex.ToUpperInvariant());

        Assert.Equal(KeyHex, key.Hex);
        Assert.Equal(Key, key.ToBytes());
        Assert.Equal("010203040506", key.Prefix.Hex);
        Assert.Equal(key, new PublicKey(Key));
    }

    [Fact]
    public void PublicKey_rejects_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => PublicKey.Parse("0102"));
        Assert.Throws<FormatException>(() => PublicKey.Parse("zz"));
    }

    [Theory]
    [InlineData(0x00, 0, 1, 0)]
    [InlineData(0x03, 3, 1, 3)]
    [InlineData(0x42, 2, 2, 4)]
    [InlineData(0xC5, 5, 4, 20)]
    public void PathLength_unpacks_hops_and_hash_size(byte raw, int hops, int hashSize, int bytes)
    {
        var p = new PathLength(raw);

        Assert.False(p.IsNone);
        Assert.Equal(hops, p.HopCount);
        Assert.Equal(hashSize, p.HashSize);
        Assert.Equal(bytes, p.ByteLength);
    }

    [Fact]
    public void PathLength_none()
    {
        Assert.True(PathLength.None.IsNone);
        Assert.Equal(0, PathLength.None.ByteLength);
    }

    [Fact]
    public void Utf8_truncate_never_splits_a_character()
    {
        // "åäö" is 6 bytes (2 each); a 5-byte budget must keep only "åä".
        Assert.Equal(Text("åä"), Utf8.Truncate("åäö", 5));
        Assert.Equal(Text("ok"), Utf8.Truncate("ok", 10));
        Assert.Empty(Utf8.Truncate("😀", 3));
    }
}
