using MeshCore.Protocol.Binary;
using MeshCore.Protocol.Models;

namespace MeshCore.Protocol;

/// <summary>
/// Builds app-to-radio command payloads (without the serial header; see
/// <see cref="Framing.FrameEncoder"/>). The comment on each method names the reply to expect.
/// Layouts follow the firmware source; see <c>docs/PROTOCOL.md</c> for the references.
/// </summary>
public static partial class Commands
{
    private const int AppStartReservedSize = 6;

    /// <summary>Starts a session. Reply: <see cref="Frames.SelfInfo"/>.</summary>
    public static byte[] AppStart(string appName) =>
        new FrameWriter()
            .Code(CommandCode.AppStart)
            .Byte(ProtocolLimits.AppProtocolVersion)
            .Zeros(AppStartReservedSize)
            .Text(appName)
            .ToArray();

    /// <summary>Announces our protocol version and asks for device details. Reply: <see cref="Frames.DeviceInfo"/>.</summary>
    public static byte[] DeviceQuery(byte appProtocolVersion = ProtocolLimits.AppProtocolVersion) =>
        new FrameWriter().Code(CommandCode.DeviceQuery).Byte(appProtocolVersion).ToArray();

    /// <summary>Lists contacts, optionally only those changed after <paramref name="since"/>.
    /// Reply: <see cref="Frames.ContactsStart"/>, N × <see cref="Frames.Contact"/>, <see cref="Frames.EndOfContacts"/>.</summary>
    public static byte[] GetContacts(DateTimeOffset? since = null)
    {
        var w = new FrameWriter().Code(CommandCode.GetContacts);
        if (since is { } s)
        {
            w.UnixTime(s);
        }
        return w.ToArray();
    }

    /// <summary>Reply: <see cref="Frames.CurrentTime"/>.</summary>
    public static byte[] GetDeviceTime() => [(byte)CommandCode.GetDeviceTime];

    /// <summary>Sets the radio clock. The firmware refuses to move it backwards (<see cref="ErrorCode.IllegalArgument"/>).
    /// Reply: <see cref="Frames.OkResponse"/>.</summary>
    public static byte[] SetDeviceTime(DateTimeOffset time) =>
        new FrameWriter().Code(CommandCode.SetDeviceTime).UnixTime(time).ToArray();

    /// <summary>Broadcasts our advert, zero-hop (neighbours only) or flooded across the mesh.
    /// Reply: <see cref="Frames.OkResponse"/>.</summary>
    public static byte[] SendSelfAdvert(bool flood) =>
        flood ? [(byte)CommandCode.SendSelfAdvert, 1] : [(byte)CommandCode.SendSelfAdvert];

    /// <summary>Renames this node. Reply: <see cref="Frames.OkResponse"/>.</summary>
    public static byte[] SetAdvertName(string name) =>
        new FrameWriter().Code(CommandCode.SetAdvertName).Bytes(Utf8.Truncate(name, ProtocolLimits.NameSize - 1)).ToArray();

    /// <summary>Sends a direct message. Reply: <see cref="Frames.MessageSent"/>, later
    /// <see cref="Frames.SendConfirmedPush"/> when acknowledged.</summary>
    /// <param name="attempt">Retry counter (0 for the first try); part of the ACK hash.</param>
    public static byte[] SendTextMessage(
        PublicKeyPrefix recipient,
        string text,
        DateTimeOffset timestamp,
        byte attempt = 0,
        TextType textType = TextType.Plain)
    {
        var prefix = recipient.ToBytes();
        if (prefix.Length < ProtocolLimits.PublicKeyPrefixSize)
        {
            throw new ArgumentException($"Recipient prefix must be at least {ProtocolLimits.PublicKeyPrefixSize} bytes.", nameof(recipient));
        }

        return new FrameWriter()
            .Code(CommandCode.SendTextMessage)
            .Byte((byte)textType)
            .Byte(attempt)
            .UnixTime(timestamp)
            .Bytes(prefix.AsSpan(0, ProtocolLimits.PublicKeyPrefixSize))
            .Text(text)
            .ToArray();
    }

    /// <summary>Sends to a group channel. Reply: <see cref="Frames.OkResponse"/> (channel messages aren't acknowledged).</summary>
    public static byte[] SendChannelTextMessage(byte channelIndex, string text, DateTimeOffset timestamp) =>
        new FrameWriter()
            .Code(CommandCode.SendChannelTextMessage)
            .Byte((byte)TextType.Plain)
            .Byte(channelIndex)
            .UnixTime(timestamp)
            .Text(text)
            .ToArray();

    /// <summary>Pops the next queued message. Reply: <see cref="Frames.ContactMessage"/>,
    /// <see cref="Frames.ChannelMessage"/> or <see cref="Frames.NoMoreMessages"/>.</summary>
    public static byte[] SyncNextMessage() => [(byte)CommandCode.SyncNextMessage];

    /// <summary>Reply: <see cref="Frames.ChannelInfo"/>, or <see cref="ErrorCode.NotFound"/> past the last slot.</summary>
    public static byte[] GetChannel(byte index) => [(byte)CommandCode.GetChannel, index];

    /// <summary>Configures a channel slot. Reply: <see cref="Frames.OkResponse"/>.</summary>
    /// <param name="secret">16-byte (128-bit) shared key.</param>
    public static byte[] SetChannel(byte index, string name, ReadOnlySpan<byte> secret)
    {
        if (secret.Length != ProtocolLimits.ChannelSecretSize)
        {
            throw new ArgumentException($"Channel secret must be {ProtocolLimits.ChannelSecretSize} bytes, got {secret.Length}.", nameof(secret));
        }

        return new FrameWriter()
            .Code(CommandCode.SetChannel)
            .Byte(index)
            .FixedString(name, ProtocolLimits.NameSize)
            .Bytes(secret)
            .ToArray();
    }

    /// <summary>Reply: <see cref="Frames.BatteryAndStorage"/>.</summary>
    public static byte[] GetBatteryAndStorage() => [(byte)CommandCode.GetBatteryAndStorage];

    /// <summary>Reboots the radio; the firmware needs the literal text "reboot". There is no reply.</summary>
    public static byte[] Reboot() => new FrameWriter().Code(CommandCode.Reboot).Text("reboot").ToArray();

    /// <summary>Reply: <see cref="Frames.OkResponse"/>, or <see cref="ErrorCode.NotFound"/>.</summary>
    public static byte[] RemoveContact(PublicKey key) =>
        new FrameWriter().Code(CommandCode.RemoveContact).Bytes(key.ToBytes()).ToArray();

    /// <summary>Forgets the learned route so the next message floods. Reply: <see cref="Frames.OkResponse"/>.</summary>
    public static byte[] ResetPath(PublicKey key) =>
        new FrameWriter().Code(CommandCode.ResetPath).Bytes(key.ToBytes()).ToArray();
}
