using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using static MeshCore.Protocol.Tests.Wire;

namespace MeshCore.Protocol.Tests;

public class FrameParserTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(Time);

    private static T Parse<T>(byte[] payload) where T : MeshFrame => Assert.IsType<T>(FrameParser.Parse(payload));

    /// <summary>A contact body as written by <c>writeContactRespFrame</c>, after the code byte.</summary>
    private static byte[] ContactBody(byte pathLen = 0x02) => Concat(
        Key,
        B(0x02, 0x01, pathLen),                     // type=repeater, flags=favourite, out_path_len
        Concat(Hex("AABB"), Repeat(0, 62)),         // out_path, always MAX_PATH_SIZE bytes
        Fixed("Hilltop RPT", 32),
        U32(Time),                                  // last_advert_timestamp
        I32(59_329_323), I32(18_068_581),           // lat, lon (micro-degrees)
        U32(Time + 60));                            // lastmod

    [Fact]
    public void SelfInfo()
    {
        var payload = Concat(
            B(0x05, 0x01, 22, 22),
            Key,
            I32(59_329_323), I32(-18_068_581),
            B(0, 1, 0x15, 1),
            U32(869_525), U32(250_000),
            B(11, 5),
            Text("T-Echo Rickard"));

        var info = Parse<SelfInfo>(payload);

        Assert.Equal(AdvertType.Chat, info.Type);
        Assert.Equal(22, info.TxPowerDbm);
        Assert.Equal(KeyHex, info.PublicKey.Hex);
        Assert.Equal(59.329323, info.Latitude, 6);
        Assert.Equal(-18.068581, info.Longitude, 6);
        Assert.Equal(1, info.AdvertLocationPolicy);
        Assert.Equal(0x15, info.TelemetryModes);
        Assert.True(info.ManualAddContacts);
        Assert.Equal(869.525, info.FrequencyMhz, 3);
        Assert.Equal(250.0, info.BandwidthKhz, 3);
        Assert.Equal(11, info.SpreadingFactor);
        Assert.Equal(5, info.CodingRate);
        Assert.Equal("T-Echo Rickard", info.Name);
        Assert.False(info.IsPush);
    }

    [Fact]
    public void DeviceInfo_full_v10_frame()
    {
        var payload = Concat(
            B(0x0D, 14, 175, 8),
            U32(123456),
            Fixed("01 Nov 2026", 12),
            Fixed("LilyGo T-Echo", 40),
            Fixed("v1.10.0", 20),
            B(0, 1));

        Assert.Equal(82, payload.Length);
        var info = Parse<DeviceInfo>(payload);

        Assert.Equal(14, info.FirmwareVersionCode);
        Assert.Equal(350, info.MaxContacts);
        Assert.Equal(8, info.MaxChannels);
        Assert.Equal(123456u, info.BlePin);
        Assert.Equal("01 Nov 2026", info.FirmwareBuildDate);
        Assert.Equal("LilyGo T-Echo", info.Model);
        Assert.Equal("v1.10.0", info.FirmwareVersion);
        Assert.False(info.RepeatEnabled);
        Assert.Equal((byte)1, info.PathHashMode);
    }

    [Fact]
    public void DeviceInfo_from_old_firmware_leaves_newer_fields_null()
    {
        var info = Parse<DeviceInfo>(Hex("0D 02"));

        Assert.Equal(2, info.FirmwareVersionCode);
        Assert.Null(info.MaxContacts);
        Assert.Null(info.Model);
        Assert.Null(info.PathHashMode);
    }

    [Fact]
    public void Contact_list_frames()
    {
        Assert.Equal(42u, Parse<ContactsStart>(Concat(B(0x02), U32(42))).TotalCount);
        Assert.Equal(At, Parse<EndOfContacts>(Concat(B(0x04), U32(Time))).MostRecentLastModified);

        var payload = Concat(B(0x03), ContactBody());
        Assert.Equal(84 + ProtocolLimits.MaxPathSize, payload.Length);

        var c = Parse<Contact>(payload);

        Assert.Equal(KeyHex, c.PublicKey.Hex);
        Assert.Equal(AdvertType.Repeater, c.Type);
        Assert.Equal(ContactFlags.Favourite, c.Flags);
        Assert.Equal(2, c.OutPathLength.HopCount);
        Assert.Equal(Hex("AABB"), c.OutPath.ToArray());
        Assert.Equal("Hilltop RPT", c.Name);
        Assert.Equal(At, c.LastAdvert);
        Assert.Equal(59.329323, c.Latitude, 6);
        Assert.Equal(18.068581, c.Longitude, 6);
        Assert.Equal(At.AddSeconds(60), c.LastModified);
    }

    [Fact]
    public void Contact_with_unknown_path_has_empty_out_path()
    {
        var c = Parse<Contact>(Concat(B(0x03), ContactBody(pathLen: 0xFF)));

        Assert.True(c.OutPathLength.IsNone);
        Assert.True(c.OutPath.IsEmpty);
    }

    [Fact]
    public void Contact_with_two_byte_path_hashes()
    {
        // path_len 0x42: hash size (0x42 >> 6) + 1 = 2, hop count 2 → 4 bytes.
        var c = Parse<Contact>(Concat(B(0x03), ContactBody(pathLen: 0x42)));

        Assert.Equal(2, c.OutPathLength.HashSize);
        Assert.Equal(4, c.OutPath.Length);
    }

    [Fact]
    public void ContactMessage_v3()
    {
        var payload = Concat(
            B(0x10, unchecked((byte)-26), 0, 0),        // SNR -6.5 dB
            Hex("010203040506"),
            B(0xFF, 0x00),                              // direct, plain
            U32(Time),
            Text("Hej från T-Echo"));

        var m = Parse<ContactMessage>(payload);

        Assert.Equal(-6.5, m.Snr);
        Assert.Equal("010203040506", m.SenderPrefix.Hex);
        Assert.True(m.PathLength.IsNone);
        Assert.Equal(TextType.Plain, m.TextType);
        Assert.Equal(At, m.SenderTimestamp);
        Assert.Null(m.SignerPrefix);
        Assert.Equal("Hej från T-Echo", m.Text);
        Assert.True(PublicKey.Parse(KeyHex).Matches(m.SenderPrefix));
    }

    [Fact]
    public void ContactMessage_signed_from_room_server()
    {
        var payload = Concat(B(0x10, 40, 0, 0), Hex("010203040506"), B(0x01, 0x02), U32(Time), Hex("CAFEBABE"), Text("posted"));

        var m = Parse<ContactMessage>(payload);

        Assert.Equal(10.0, m.Snr);
        Assert.Equal(TextType.SignedPlain, m.TextType);
        Assert.Equal("cafebabe", m.SignerPrefix?.Hex);
        Assert.Equal("posted", m.Text);
    }

    [Fact]
    public void ContactMessage_legacy_has_no_snr()
    {
        var m = Parse<ContactMessage>(Concat(B(0x07), Hex("010203040506"), B(0x01, 0x00), U32(Time), Text("old")));

        Assert.Null(m.Snr);
        Assert.Equal(1, m.PathLength.HopCount);
        Assert.Equal("old", m.Text);
    }

    [Fact]
    public void ChannelMessage_v3_splits_sender_name()
    {
        var payload = Concat(B(0x11, 0x08, 0, 0, 0x00, 0x03, 0x00), U32(Time), Text("Alice: hello mesh"));

        var m = Parse<ChannelMessage>(payload);

        Assert.Equal(2.0, m.Snr);
        Assert.Equal(0, m.ChannelIndex);
        Assert.Equal(3, m.PathLength.HopCount);
        Assert.Equal("Alice", m.SenderName);
        Assert.Equal("hello mesh", m.Body);
    }

    [Fact]
    public void ChannelMessage_legacy_without_sender_convention()
    {
        var m = Parse<ChannelMessage>(Concat(B(0x08, 0x02, 0xFF, 0x00), U32(Time), Text("no sender")));

        Assert.Null(m.Snr);
        Assert.Equal(2, m.ChannelIndex);
        Assert.Null(m.SenderName);
        Assert.Equal("no sender", m.Body);
    }

    [Fact]
    public void ChannelInfo_and_empty_slot()
    {
        var secret = Repeat(0x8B, 16);
        var used = Parse<ChannelInfo>(Concat(B(0x12, 0x00), Fixed("Public", 32), secret));
        var empty = Parse<ChannelInfo>(Concat(B(0x12, 0x07), Repeat(0, 48)));

        Assert.Equal("Public", used.Name);
        Assert.Equal(secret, used.Secret.ToArray());
        Assert.False(used.IsEmpty);
        Assert.Equal(7, empty.Index);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void BatteryAndStorage_new_and_old_firmware()
    {
        var full = Parse<BatteryAndStorage>(Concat(B(0x0C), U16(4012), U32(120), U32(2048)));
        var old = Parse<BatteryAndStorage>(Concat(B(0x0C), U16(3700)));

        Assert.Equal(4.012, full.BatteryVolts, 3);
        Assert.Equal(120u, full.StorageUsedKb);
        Assert.Equal(2048u, full.StorageTotalKb);
        Assert.Equal(3700, old.BatteryMillivolts);
        Assert.Null(old.StorageTotalKb);
    }

    [Fact]
    public void Simple_responses()
    {
        Parse<OkResponse>(Hex("00"));
        Parse<NoMoreMessages>(Hex("0A"));
        Assert.Equal(ErrorCode.NotFound, Parse<ErrorResponse>(Hex("01 02")).Error);
        Assert.Equal(ErrorCode.Unknown, Parse<ErrorResponse>(Hex("01")).Error);
        Assert.Equal(At, Parse<CurrentTime>(Concat(B(0x09), U32(Time))).Time);
    }

    [Fact]
    public void MessageSent()
    {
        var sent = Parse<MessageSent>(Concat(B(0x06, 0x01), U32(0xDEADBEEF), U32(12_000)));

        Assert.True(sent.Flooded);
        Assert.Equal(0xDEADBEEF, sent.ExpectedAck);
        Assert.Equal(TimeSpan.FromSeconds(12), sent.EstimatedTimeout);
    }

    [Fact]
    public void Pushes()
    {
        Assert.Equal(KeyHex, Parse<AdvertPush>(Concat(B(0x80), Key)).PublicKey.Hex);
        Assert.Equal(KeyHex, Parse<PathUpdatedPush>(Concat(B(0x81), Key)).PublicKey.Hex);

        var ack = Parse<SendConfirmedPush>(Concat(B(0x82), U32(0xDEADBEEF), U32(1500)));
        Assert.Equal(0xDEADBEEF, ack.AckCode);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ack.RoundTrip);

        Assert.True(Parse<MessagesWaitingPush>(Hex("83")).IsPush);

        var advert = Parse<NewAdvertPush>(Concat(B(0x8A), ContactBody()));
        Assert.Equal("Hilltop RPT", advert.Contact.Name);
    }

    [Fact]
    public void Unknown_codes_keep_the_payload()
    {
        var frame = Parse<UnknownFrame>(Hex("88 0102"));

        Assert.True(frame.IsPush);
        Assert.Equal(Hex("88 0102"), frame.Payload.ToArray());
    }

    [Fact]
    public void Truncated_frames_are_reported_not_thrown()
    {
        var frame = Parse<MalformedFrame>(Concat(B(0x05, 0x01), Repeat(0, 10)));

        Assert.Equal(0x05, frame.Code);
        Assert.Contains("too short", frame.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_payload_throws()
    {
        Assert.Throws<ArgumentException>(() => FrameParser.Parse([]));
    }
}
