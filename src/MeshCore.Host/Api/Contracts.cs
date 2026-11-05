using System.ComponentModel.DataAnnotations;
using MeshCore.Data;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using MeshCore.Protocol;
using MeshCore.Protocol.Models;

namespace MeshCore.Host.Api;

// ---- Auth & users -------------------------------------------------------------

public sealed record SetupStatusResponse(bool SetupRequired);

public sealed record CredentialsRequest(
    [Required, StringLength(CredentialRules.MaxUsernameLength)] string Username,
    [Required, StringLength(CredentialRules.MaxPasswordLength)] string Password);

public sealed record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, StringLength(CredentialRules.MaxPasswordLength)] string NewPassword);

public sealed record CreateUserRequest(
    [Required, StringLength(CredentialRules.MaxUsernameLength)] string Username,
    [Required, StringLength(CredentialRules.MaxPasswordLength)] string TemporaryPassword,
    UserRole Role = UserRole.User);

public sealed record UpdateUserRequest(UserRole? Role, bool? Enabled);

public sealed record ResetPasswordRequest([Required, StringLength(CredentialRules.MaxPasswordLength)] string TemporaryPassword);

public sealed record UserResponse(
    string Id, string Username, UserRole Role, bool Enabled, bool MustChangePassword,
    DateTimeOffset CreatedUtc, DateTimeOffset? LastLoginUtc, bool LockedOut)
{
    public static UserResponse From(UserAccount u, DateTimeOffset now) =>
        new(u.Id, u.Username, u.Role, u.Enabled, u.MustChangePassword, u.CreatedUtc, u.LastLoginUtc, u.LockoutEndUtc > now);
}

public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, UserResponse User);

// ---- Device -------------------------------------------------------------------

public sealed record RadioSettingsResponse(double FrequencyMhz, double BandwidthKhz, int SpreadingFactor, int CodingRate, int TxPowerDbm, int MaxTxPowerDbm);

public sealed record NodeResponse(
    string Name, string PublicKey, AdvertType Type, double Latitude, double Longitude, RadioSettingsResponse Radio, PreferencesResponse Preferences);

/// <param name="AdvertLocationPolicy">0 = don't include location in adverts, 1 = include it.</param>
/// <param name="TelemetryModes">Packed base/location/environment telemetry permissions: (env << 4) | (loc << 2) | base.</param>
public sealed record PreferencesResponse(bool ManualAddContacts, int AdvertLocationPolicy, int TelemetryModes, int MultiAcks);

public sealed record HardwareResponse(
    string? Model, string? FirmwareVersion, string? FirmwareBuildDate, int FirmwareVersionCode, int? MaxContacts, int? MaxChannels);

public sealed record DeviceResponse(
    RadioState State, string Endpoint, DateTimeOffset? ConnectedSince, string? LastError, DateTimeOffset? NextAttempt,
    NodeResponse? Node, HardwareResponse? Hardware)
{
    public static DeviceResponse From(RadioStatus s) => new(
        s.State, s.Endpoint, s.Since, s.LastError, s.NextAttempt,
        s.Self is { } n ? new NodeResponse(n.Name, n.PublicKey.Hex, n.Type, n.Latitude, n.Longitude,
            new RadioSettingsResponse(n.FrequencyMhz, n.BandwidthKhz, n.SpreadingFactor, n.CodingRate, n.TxPowerDbm, n.MaxTxPowerDbm),
            new PreferencesResponse(n.ManualAddContacts, n.AdvertLocationPolicy, n.TelemetryModes, n.MultiAcks)) : null,
        s.Device is { } d ? new HardwareResponse(d.Model, d.FirmwareVersion, d.FirmwareBuildDate, d.FirmwareVersionCode, d.MaxContacts, d.MaxChannels) : null);
}

public sealed record BatteryResponse(int Millivolts, double Volts, uint? StorageUsedKb, uint? StorageTotalKb);

public sealed record DeviceTimeResponse(DateTimeOffset DeviceTime, DateTimeOffset ServerTime, double DriftSeconds);

public sealed record AdvertRequest(bool Flood = false);

// ---- Contacts & channels ------------------------------------------------------

public sealed record ContactResponse(
    string PublicKey, string KeyPrefix, string Name, AdvertType Type, bool Favourite,
    int? Hops, double Latitude, double Longitude, DateTimeOffset LastAdvert, DateTimeOffset? LastHeard, bool IsOnRadio)
{
    public static ContactResponse From(ContactEntity c)
    {
        var path = new PathLength(c.OutPathLength);
        return new(c.PublicKey, c.KeyPrefix, c.Name, c.Type, c.Flags.HasFlag(ContactFlags.Favourite),
            path.IsNone ? null : path.HopCount, c.Latitude, c.Longitude, c.LastAdvert, c.LastHeard, c.IsOnRadio);
    }
}

public sealed record ChannelResponse(byte Index, string Name)
{
    public static ChannelResponse From(ChannelEntity c) => new(c.Index, c.Name);
}

public sealed record ChannelSecretResponse(byte Index, string Name, string SecretHex);

/// <param name="Name">Channel name, up to 31 bytes.</param>
/// <param name="SecretHex">16-byte key as 32 hex characters. Leave out to generate a random key.</param>
public sealed record SetChannelRequest(
    [Required, StringLength(31, MinimumLength = 1)] string Name,
    [RegularExpression("^[0-9a-fA-F]{32}$", ErrorMessage = "The secret must be 32 hex characters (16 bytes).")] string? SecretHex);

// ---- Messages -----------------------------------------------------------------

public sealed record MessageResponse(
    long Id, MessageKind Kind, MessageDirection Direction, string? ContactKeyPrefix, byte? ChannelIndex,
    string? SenderName, string Text, DateTimeOffset SenderTimestamp, DateTimeOffset ReceivedAt,
    double? Snr, int? Hops, DeliveryStatus Status, int? RoundTripMs, bool? Flooded, string? SentByUserId, string? Error, TextType TextType = TextType.Plain)
{
    public static MessageResponse From(MessageEntity m) => new(
        m.Id, m.Kind, m.Direction, m.ContactKeyPrefix, m.ChannelIndex, m.SenderName, m.Text, m.SenderTimestamp, m.ReceivedAt,
        m.Snr, m.Hops, m.Status, m.RoundTripMs, m.Flooded, m.SentByUserId, m.Error, m.TextType);
}

/// <param name="Contact">The contact's public key, or at least its first 12 hex characters.</param>
public sealed record SendDirectRequest(
    [Required, RegularExpression("^[0-9a-fA-F]{12,64}$", ErrorMessage = "Use the contact's public key (at least 12 hex characters).")] string Contact,
    [Required, StringLength(ProtocolLimits.MaxTextBytes, MinimumLength = 1, ErrorMessage = "The message is too long: the limit is 160 bytes.")] string Text);

public sealed record SendChannelRequest(
    [Range(0, 255)] byte Channel,
    [Required, StringLength(ProtocolLimits.MaxTextBytes, MinimumLength = 1, ErrorMessage = "The message is too long: the limit is 160 bytes.")] string Text);

public sealed record SyncResponse(int NewMessages);

/// <summary>A chat: a channel, or a contact you've exchanged direct messages with.</summary>
/// <param name="Key">Stable id: <c>c:&lt;channel&gt;</c> or <c>d:&lt;key prefix&gt;</c>.</param>
/// <param name="Unread">Incoming messages the signed-in user hasn't read yet.</param>
public sealed record ConversationResponse(
    string Key, MessageKind Kind, string? ContactKeyPrefix, byte? ChannelIndex, string Title, MessageResponse? LastMessage, int Unread);

/// <param name="Key">Conversation key from <see cref="ConversationResponse.Key"/>.</param>
/// <param name="MessageId">The newest message the user has seen.</param>
public sealed record MarkReadRequest(
    [Required, RegularExpression("^(c:[0-9]{1,3}|d:[0-9a-fA-F]{12})$")] string Key,
    [Range(0, long.MaxValue)] long MessageId);
