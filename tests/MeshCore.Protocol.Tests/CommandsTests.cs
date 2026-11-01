using MeshCore.Protocol.Models;
using static MeshCore.Protocol.Tests.Wire;

namespace MeshCore.Protocol.Tests;

public class CommandsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(Time);

    [Fact]
    public void AppStart_has_version_reserved_bytes_and_name()
    {
        // Firmware requires len >= 8 and reads the name from offset 8.
        Assert.Equal(Concat(Hex("01 03 000000000000"), Text("MCC")), Commands.AppStart("MCC"));
        Assert.Equal(8, Commands.AppStart("").Length);
    }

    [Fact]
    public void DeviceQuery_announces_protocol_version_3()
    {
        Assert.Equal(Hex("16 03"), Commands.DeviceQuery());
    }

    [Fact]
    public void GetContacts_with_and_without_since()
    {
        Assert.Equal(Hex("04"), Commands.GetContacts());
        Assert.Equal(Concat(B(0x04), U32(Time)), Commands.GetContacts(At));
    }

    [Fact]
    public void Time_commands()
    {
        Assert.Equal(Hex("05"), Commands.GetDeviceTime());
        Assert.Equal(Concat(B(0x06), U32(Time)), Commands.SetDeviceTime(At));
    }

    [Fact]
    public void SendSelfAdvert_zero_hop_or_flood()
    {
        Assert.Equal(Hex("07"), Commands.SendSelfAdvert(flood: false));
        Assert.Equal(Hex("07 01"), Commands.SendSelfAdvert(flood: true));
    }

    [Fact]
    public void SetAdvertName_is_capped_at_31_bytes()
    {
        Assert.Equal(Concat(B(0x08), Text("T-Echo")), Commands.SetAdvertName("T-Echo"));
        Assert.Equal(1 + 31, Commands.SetAdvertName(new string('x', 40)).Length);
    }

    [Fact]
    public void SendTextMessage_layout()
    {
        var recipient = PublicKey.Parse(KeyHex).Prefix;

        var cmd = Commands.SendTextMessage(recipient, "hi", At, attempt: 1);

        // [0]=cmd [1]=txt_type [2]=attempt [3..6]=timestamp [7..12]=prefix [13..]=text
        Assert.Equal(Concat(Hex("02 00 01"), U32(Time), Hex("010203040506"), Text("hi")), cmd);
    }

    [Fact]
    public void SendTextMessage_accepts_a_full_key_prefix_and_uses_its_first_6_bytes()
    {
        var full = PublicKeyPrefix.Parse(KeyHex);

        var cmd = Commands.SendTextMessage(full, "", At);

        Assert.Equal(Hex("010203040506"), cmd[7..13]);
        Assert.Equal(13, cmd.Length);
    }

    [Fact]
    public void SendTextMessage_rejects_short_prefixes_and_oversized_text()
    {
        Assert.Throws<ArgumentException>(() => Commands.SendTextMessage(PublicKeyPrefix.Parse("0102"), "x", At));
        Assert.Throws<ArgumentException>(() =>
            Commands.SendTextMessage(PublicKey.Parse(KeyHex).Prefix, new string('x', ProtocolLimits.MaxFrameSize), At));
    }

    [Fact]
    public void SendChannelTextMessage_layout()
    {
        // [0]=cmd [1]=txt_type [2]=channel [3..6]=timestamp [7..]=text
        Assert.Equal(Concat(Hex("03 00 02"), U32(Time), Text("hello")), Commands.SendChannelTextMessage(2, "hello", At));
    }

    [Fact]
    public void Single_byte_commands()
    {
        Assert.Equal(Hex("0A"), Commands.SyncNextMessage());
        Assert.Equal(Hex("14"), Commands.GetBatteryAndStorage());
        Assert.Equal(Hex("1F 05"), Commands.GetChannel(5));
    }

    [Fact]
    public void SetChannel_is_50_bytes()
    {
        var secret = Repeat(0xAB, 16);

        var cmd = Commands.SetChannel(1, "Public", secret);

        Assert.Equal(Concat(Hex("20 01"), Fixed("Public", 32), secret), cmd);
        Assert.Equal(50, cmd.Length);
    }

    [Fact]
    public void SetChannel_validates_secret_and_keeps_a_terminator_in_the_name()
    {
        Assert.Throws<ArgumentException>(() => Commands.SetChannel(0, "x", new byte[8]));

        var cmd = Commands.SetChannel(0, new string('n', 40), new byte[16]);
        Assert.Equal(0, cmd[2 + 31]);
    }

    [Fact]
    public void Reboot_carries_the_magic_word()
    {
        Assert.Equal(Concat(B(0x13), Text("reboot")), Commands.Reboot());
    }

    [Fact]
    public void Contact_key_commands()
    {
        var key = PublicKey.Parse(KeyHex);

        Assert.Equal(Concat(B(0x0F), Key), Commands.RemoveContact(key));
        Assert.Equal(Concat(B(0x0D), Key), Commands.ResetPath(key));
    }
}
