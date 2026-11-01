using MeshCore.Protocol.Framing;
using static MeshCore.Protocol.Tests.Wire;

namespace MeshCore.Protocol.Tests;

public class FrameEncoderTests
{
    [Fact]
    public void Encode_writes_to_radio_marker_and_little_endian_length()
    {
        var frame = FrameEncoder.Encode([0x16, 0x03]);

        Assert.Equal(Hex("3C 0200 16 03"), frame);
    }

    [Fact]
    public void Encode_can_emulate_the_radio_marker()
    {
        Assert.Equal(Hex("3E 0100 0A"), FrameEncoder.Encode([0x0A], FrameMarker.FromRadio));
    }

    [Fact]
    public void Encode_rejects_empty_and_oversized_payloads()
    {
        Assert.Throws<ArgumentException>(() => FrameEncoder.Encode([]));
        Assert.Throws<ArgumentException>(() => FrameEncoder.Encode(new byte[ProtocolLimits.MaxFrameSize + 1]));
    }

    [Fact]
    public void Encode_accepts_the_maximum_size()
    {
        var frame = FrameEncoder.Encode(new byte[ProtocolLimits.MaxFrameSize]);

        Assert.Equal(FrameMarker.HeaderSize + ProtocolLimits.MaxFrameSize, frame.Length);
        Assert.Equal(U16(ProtocolLimits.MaxFrameSize), frame[1..3]);
    }
}

public class FrameDecoderTests
{
    [Fact]
    public void Decodes_a_single_frame()
    {
        var frames = new FrameDecoder().Decode(Hex("3E 0300 05 01 02"));

        var frame = Assert.Single(frames);
        Assert.Equal(Hex("05 01 02"), frame);
    }

    [Fact]
    public void Decodes_a_frame_delivered_one_byte_at_a_time()
    {
        var decoder = new FrameDecoder();
        var all = new List<byte[]>();

        foreach (var b in Hex("3E 0400 0C 10 0E 00"))
        {
            all.AddRange(decoder.Decode([b]));
        }

        Assert.Equal(Hex("0C 10 0E 00"), Assert.Single(all));
    }

    [Fact]
    public void Decodes_several_frames_and_a_partial_one_across_reads()
    {
        var decoder = new FrameDecoder();

        var first = decoder.Decode(Hex("3E 0100 00   3E 0100 0A   3E 0200 01"));
        var second = decoder.Decode(Hex("02"));

        Assert.Equal([Hex("00"), Hex("0A")], first);
        Assert.Equal(Hex("01 02"), Assert.Single(second));
    }

    [Fact]
    public void Skips_stray_text_before_a_frame()
    {
        var decoder = new FrameDecoder();
        var input = Concat(Text("boot ok\r\n"), Hex("3E 0100 83"));

        var frames = decoder.Decode(input);

        Assert.Equal(Hex("83"), Assert.Single(frames));
        Assert.Equal(9, decoder.DiscardedBytes);
    }

    [Fact]
    public void Ignores_app_bound_markers()
    {
        // An echo of our own '<' frame must not be mistaken for a radio frame.
        var frames = new FrameDecoder().Decode(Hex("3C 0100 0A"));

        Assert.Empty(frames);
    }

    [Fact]
    public void Ignores_zero_length_frames()
    {
        var frames = new FrameDecoder().Decode(Hex("3E 0000   3E 0100 00"));

        Assert.Equal(Hex("00"), Assert.Single(frames));
    }

    [Fact]
    public void Resyncs_after_an_oversized_length()
    {
        var decoder = new FrameDecoder();

        var frames = decoder.Decode(Hex("3E FFFF   3E 0100 83"));

        Assert.Equal(Hex("83"), Assert.Single(frames));
        Assert.Equal(1, decoder.OversizedFrames);
    }

    [Fact]
    public void Reset_drops_a_partial_frame()
    {
        var decoder = new FrameDecoder();
        decoder.Decode(Hex("3E 0500 05 01"));

        decoder.Reset();
        var frames = decoder.Decode(Hex("3E 0100 0A"));

        Assert.Equal(Hex("0A"), Assert.Single(frames));
    }

    [Fact]
    public void Round_trips_with_the_encoder()
    {
        var payload = Enumerable.Range(0, ProtocolLimits.MaxFrameSize).Select(i => (byte)i).ToArray();

        var frames = new FrameDecoder().Decode(FrameEncoder.Encode(payload, FrameMarker.FromRadio));

        Assert.Equal(payload, Assert.Single(frames));
    }
}
