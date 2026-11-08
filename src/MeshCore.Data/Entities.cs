using MeshCore.Protocol;

namespace MeshCore.Data;

public enum MessageKind
{
    Direct = 0,
    Channel = 1,
}

public enum MessageDirection
{
    Incoming = 0,
    Outgoing = 1,
}

/// <summary>Delivery state of an outgoing message. Incoming messages are always <see cref="None"/>.</summary>
public enum DeliveryStatus
{
    None = 0,

    /// <summary>Stored, being handed to the radio.</summary>
    Sending = 1,

    /// <summary>The radio transmitted it. Final state for channel messages, which have no ACKs.</summary>
    Sent = 2,

    /// <summary>The recipient acknowledged it (direct messages only).</summary>
    Delivered = 3,

    /// <summary>No ACK before the radio's estimated timeout. It may still have arrived.</summary>
    NotConfirmed = 4,

    /// <summary>The radio rejected it or wasn't connected.</summary>
    Failed = 5,
}

/// <summary>
/// A contact the radio knows (or knew). Kept after removal from the radio so message history
/// still shows a name.
/// </summary>
public sealed class ContactEntity
{
    /// <summary>64 hex chars, lowercase.</summary>
    public required string PublicKey { get; set; }

    /// <summary>First 6 bytes (12 hex chars); how direct messages identify the sender.</summary>
    public required string KeyPrefix { get; set; }

    public required string Name { get; set; }

    public AdvertType Type { get; set; }

    public ContactFlags Flags { get; set; }

    /// <summary>Raw packed <c>path_len</c>; 0xFF = no known path (messages flood).</summary>
    public byte OutPathLength { get; set; }

    public byte[] OutPath { get; set; } = [];

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>When the contact last advertised, by its own clock.</summary>
    public DateTimeOffset LastAdvert { get; set; }

    /// <summary>When the radio last changed this contact, by the radio's clock.</summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>When we last heard an advert from it (our clock).</summary>
    public DateTimeOffset? LastHeard { get; set; }

    /// <summary>False once the contact was removed from the radio.</summary>
    public bool IsOnRadio { get; set; } = true;

    /// <summary>"My device": set in this app only (the radio doesn't know it). Its battery is logged every hour.</summary>
    public bool IsMine { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One battery check of a node: our own radio (<see cref="Self"/>) or a contact marked "my device".
/// <see cref="Millivolts"/> is null when the node didn't answer, so outages show in the history too.
/// </summary>
public sealed class BatteryReadingEntity
{
    /// <summary><see cref="BatteryReadingEntity.Node"/> value for the radio this app is connected to.</summary>
    public const string Self = "self";

    public long Id { get; set; }

    /// <summary><see cref="Self"/>, or the contact's public key (64 hex chars, lowercase).</summary>
    public required string Node { get; set; }

    public DateTimeOffset At { get; set; }

    public int? Millivolts { get; set; }

    /// <summary>How it was read (<c>radio</c>, <c>telemetry</c>, <c>status</c>) or why it failed (<c>no answer</c>, …).</summary>
    public string? Note { get; set; }
}

/// <summary>A channel slot name. The shared secret stays on the radio and is never stored here.</summary>
public sealed class ChannelEntity
{
    public byte Index { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How far a user has read in one conversation. Unread = incoming messages with a higher Id.</summary>
public sealed class ReadMarkerEntity
{
    public required string UserId { get; set; }

    /// <summary>See <see cref="ConversationKey"/>.</summary>
    public required string ConversationKey { get; set; }

    public long LastReadMessageId { get; set; }
}

/// <summary>Stable string ids for conversations: <c>c:3</c> for channel 3, <c>d:&lt;12 hex&gt;</c> for a contact.</summary>
public static class ConversationKey
{
    public static string Channel(byte index) => $"c:{index}";

    public static string Direct(string keyPrefix) => $"d:{keyPrefix}";

    public static string Of(MessageEntity m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m.Kind == MessageKind.Channel ? Channel(m.ChannelIndex ?? 0) : Direct(m.ContactKeyPrefix ?? "");
    }

    /// <summary>Parses a key back into its parts; false if it isn't one of ours.</summary>
    public static bool TryParse(string? key, out MessageKind kind, out string? contactKeyPrefix, out byte? channelIndex)
    {
        kind = default;
        contactKeyPrefix = null;
        channelIndex = null;
        if (key is null || key.Length < 3 || key[1] != ':')
        {
            return false;
        }
        var value = key[2..];
        switch (key[0])
        {
            case 'c' when byte.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var idx):
                kind = MessageKind.Channel;
                channelIndex = idx;
                return true;
            case 'd' when value.Length == 12 && value.All(Uri.IsHexDigit):
                kind = MessageKind.Direct;
                contactKeyPrefix = value.ToLowerInvariant();
                return true;
            default:
                return false;
        }
    }
}

public sealed class MessageEntity
{
    public long Id { get; set; }

    public MessageKind Kind { get; set; }

    public MessageDirection Direction { get; set; }

    /// <summary>Direct messages: the conversation's key prefix (12 hex chars).</summary>
    public string? ContactKeyPrefix { get; set; }

    /// <summary>Channel messages: the channel slot.</summary>
    public byte? ChannelIndex { get; set; }

    /// <summary>Display name at the time: the contact's name, the channel's <c>"name: "</c> prefix, or ours for outgoing.</summary>
    public string? SenderName { get; set; }

    /// <summary>Message body (for channel messages, without the <c>"name: "</c> prefix).</summary>
    public required string Text { get; set; }

    public TextType TextType { get; set; }

    /// <summary>Room servers: key prefix of the original author.</summary>
    public string? SignerPrefix { get; set; }

    /// <summary>When the sender says it was sent (sender's clock).</summary>
    public DateTimeOffset SenderTimestamp { get; set; }

    /// <summary>When we received it, or created it for outgoing.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public double? Snr { get; set; }

    /// <summary>Hop count for flood-routed messages; null when it came direct.</summary>
    public int? Hops { get; set; }

    public DeliveryStatus Status { get; set; }

    /// <summary>Outgoing direct: the ACK code to match against <c>PUSH_CODE_SEND_CONFIRMED</c>.</summary>
    public uint? ExpectedAck { get; set; }

    /// <summary>Outgoing direct: give up waiting for the ACK after this.</summary>
    public DateTimeOffset? AckDeadline { get; set; }

    public int? RoundTripMs { get; set; }

    public bool? Flooded { get; set; }

    /// <summary>Which signed-in user sent it (filled in from Phase 4).</summary>
    public string? SentByUserId { get; set; }

    public string? Error { get; set; }
}
