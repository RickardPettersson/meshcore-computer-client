using MeshCore.Protocol.Models;

namespace MeshCore.Protocol.Frames;

/// <summary><c>RESP_CODE_OK</c>.</summary>
public sealed record OkResponse() : MeshFrame((byte)ResponseCode.Ok);

/// <summary><c>RESP_CODE_ERR</c>.</summary>
public sealed record ErrorResponse(ErrorCode Error) : MeshFrame((byte)ResponseCode.Error);

/// <summary><c>RESP_CODE_DISABLED</c>: the feature is turned off on this build.</summary>
public sealed record DisabledResponse() : MeshFrame((byte)ResponseCode.Disabled);

/// <summary><c>RESP_CODE_SELF_INFO</c>: reply to <c>CMD_APP_START</c>.</summary>
public sealed record SelfInfo(
    AdvertType Type,
    sbyte TxPowerDbm,
    sbyte MaxTxPowerDbm,
    PublicKey PublicKey,
    double Latitude,
    double Longitude,
    byte MultiAcks,
    byte AdvertLocationPolicy,
    byte TelemetryModes,
    bool ManualAddContacts,
    uint FrequencyKhz,
    uint BandwidthHz,
    byte SpreadingFactor,
    byte CodingRate,
    string Name) : MeshFrame((byte)ResponseCode.SelfInfo)
{
    public double FrequencyMhz => FrequencyKhz / 1000.0;

    public double BandwidthKhz => BandwidthHz / 1000.0;
}

/// <summary><c>RESP_CODE_DEVICE_INFO</c>: reply to <c>CMD_DEVICE_QUERY</c>.
/// Older firmware sends a shorter frame, so later fields are nullable.</summary>
public sealed record DeviceInfo(
    byte FirmwareVersionCode,
    int? MaxContacts,
    int? MaxChannels,
    uint? BlePin,
    string? FirmwareBuildDate,
    string? Model,
    string? FirmwareVersion,
    bool? RepeatEnabled,
    byte? PathHashMode) : MeshFrame((byte)ResponseCode.DeviceInfo);

/// <summary><c>RESP_CODE_CONTACTS_START</c>: total contacts on the radio.</summary>
public sealed record ContactsStart(uint TotalCount) : MeshFrame((byte)ResponseCode.ContactsStart);

/// <summary>A contact entry. Sent as <c>RESP_CODE_CONTACT</c> or inside <see cref="NewAdvertPush"/>.</summary>
public sealed record Contact(
    PublicKey PublicKey,
    AdvertType Type,
    ContactFlags Flags,
    PathLength OutPathLength,
    ReadOnlyMemory<byte> OutPath,
    string Name,
    DateTimeOffset LastAdvert,
    double Latitude,
    double Longitude,
    DateTimeOffset LastModified) : MeshFrame((byte)ResponseCode.Contact);

/// <summary><c>RESP_CODE_END_OF_CONTACTS</c>. Pass <see cref="MostRecentLastModified"/> as
/// <c>since</c> next time to fetch only changed contacts.</summary>
public sealed record EndOfContacts(DateTimeOffset MostRecentLastModified) : MeshFrame((byte)ResponseCode.EndOfContacts);

/// <summary><c>RESP_CODE_CURR_TIME</c>.</summary>
public sealed record CurrentTime(DateTimeOffset Time) : MeshFrame((byte)ResponseCode.CurrentTime);

/// <summary><c>RESP_CODE_SENT</c>: a direct message was queued. Match
/// <see cref="ExpectedAck"/> against <see cref="SendConfirmedPush.AckCode"/>.</summary>
public sealed record MessageSent(bool Flooded, uint ExpectedAck, TimeSpan EstimatedTimeout) : MeshFrame((byte)ResponseCode.Sent);

/// <summary><c>RESP_CODE_NO_MORE_MESSAGES</c>: the radio's message queue is empty.</summary>
public sealed record NoMoreMessages() : MeshFrame((byte)ResponseCode.NoMoreMessages);

/// <summary>A direct message from a contact (<c>RESP_CODE_CONTACT_MSG_RECV</c> or its V3 variant).</summary>
/// <param name="Snr">Signal-to-noise ratio in dB; only V3 frames carry it.</param>
/// <param name="SignerPrefix">For <see cref="TextType.SignedPlain"/> (room servers): the original author's key prefix.</param>
public sealed record ContactMessage(
    byte Code,
    double? Snr,
    PublicKeyPrefix SenderPrefix,
    PathLength PathLength,
    TextType TextType,
    DateTimeOffset SenderTimestamp,
    PublicKeyPrefix? SignerPrefix,
    string Text) : MeshFrame(Code);

/// <summary>A group channel message (<c>RESP_CODE_CHANNEL_MSG_RECV</c> or its V3 variant).
/// The sender isn't authenticated; by convention the text is <c>"name: message"</c>.</summary>
public sealed record ChannelMessage(
    byte Code,
    double? Snr,
    byte ChannelIndex,
    PathLength PathLength,
    TextType TextType,
    DateTimeOffset SenderTimestamp,
    string Text) : MeshFrame(Code)
{
    private const string Separator = ": ";

    /// <summary>The sender name from the <c>"name: message"</c> convention, if present.</summary>
    public string? SenderName => Text.IndexOf(Separator, StringComparison.Ordinal) is > 0 and var i ? Text[..i] : null;

    /// <summary>The text without the sender name.</summary>
    public string Body => SenderName is { } name ? Text[(name.Length + Separator.Length)..] : Text;
}

/// <summary><c>RESP_CODE_CHANNEL_INFO</c>. An unused slot has an empty name and an all-zero secret.</summary>
public sealed record ChannelInfo(byte Index, string Name, ReadOnlyMemory<byte> Secret) : MeshFrame((byte)ResponseCode.ChannelInfo)
{
    public bool IsEmpty => Name.Length == 0 && !Secret.Span.ContainsAnyExcept((byte)0);
}

/// <summary><c>RESP_CODE_BATT_AND_STORAGE</c>.</summary>
public sealed record BatteryAndStorage(ushort BatteryMillivolts, uint? StorageUsedKb, uint? StorageTotalKb)
    : MeshFrame((byte)ResponseCode.BatteryAndStorage)
{
    public double BatteryVolts => BatteryMillivolts / 1000.0;
}
