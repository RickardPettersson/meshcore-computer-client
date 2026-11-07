using System.ComponentModel.DataAnnotations;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;

namespace MeshCore.Host.Web;

public sealed class SetupViewModel
{
    [Required, StringLength(CredentialRules.MaxUsernameLength, MinimumLength = CredentialRules.MinUsernameLength)]
    [RegularExpression("^[A-Za-z0-9._-]+$", ErrorMessage = "Use letters, digits, '.', '_' or '-'.")]
    [Display(Name = "Username")]
    public string Username { get; set; } = "";

    [Required, StringLength(CredentialRules.MaxPasswordLength, MinimumLength = CredentialRules.MinPasswordLength)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Required, Compare(nameof(Password), ErrorMessage = "The passwords don't match.")]
    [DataType(DataType.Password), Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = "";
}

public sealed class LoginViewModel
{
    [Required, Display(Name = "Username")]
    public string Username { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Display(Name = "Keep me signed in on this device")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public sealed class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required, StringLength(CredentialRules.MaxPasswordLength, MinimumLength = CredentialRules.MinPasswordLength)]
    [DataType(DataType.Password), Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required, Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
    [DataType(DataType.Password), Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";

    public bool Forced { get; set; }
}

public sealed class CreateUserViewModel
{
    [Required, StringLength(CredentialRules.MaxUsernameLength, MinimumLength = CredentialRules.MinUsernameLength)]
    [RegularExpression("^[A-Za-z0-9._-]+$", ErrorMessage = "Use letters, digits, '.', '_' or '-'.")]
    public string Username { get; set; } = "";

    [Required, StringLength(CredentialRules.MaxPasswordLength, MinimumLength = CredentialRules.MinPasswordLength)]
    [Display(Name = "Temporary password")]
    public string TemporaryPassword { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.User;
}

public sealed class ChannelEditViewModel
{
    public byte Index { get; set; }

    [Required, StringLength(31, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [RegularExpression("^[0-9a-fA-F]{32}$", ErrorMessage = "The key must be 32 hex characters (16 bytes).")]
    [Display(Name = "Channel key (hex)")]
    public string? SecretHex { get; set; }

    public bool IsNew { get; set; }
}

public sealed record DashboardViewModel(
    DeviceResponse Device, BatteryResponse? Battery, int ContactCount, int ChannelCount, IReadOnlyList<ConversationResponse> Recent);

public sealed record DevicePageViewModel(DeviceResponse Device, BatteryResponse? Battery, DeviceTimeResponse? Time);

public sealed record ChannelsPageViewModel(IReadOnlyList<ChannelResponse> Channels, int? MaxChannels, ChannelSecretResponse? Revealed);

public sealed record NodeSettingsViewModel(DeviceResponse Device, TuningResponse? Tuning, string? RevealedPrivateKey);

public sealed record ContactCardViewModel(string Name, string AdvertHex);

/// <summary>A contact plus whatever remote request the user just ran on it.</summary>
public sealed record ContactDetailsViewModel(
    ContactResponse Contact,
    string? CardHex = null,
    LoginResponse? Login = null,
    StatusResponse? Status = null,
    TelemetryResponse? Telemetry = null,
    TraceResponse? Trace = null,
    string? Error = null)
{
    public bool IsServer => Contact.Type is MeshCore.Protocol.AdvertType.Repeater or MeshCore.Protocol.AdvertType.Room;
}

public sealed record ChatViewModel(
    string Key,
    string Title, string? Subtitle, string? ContactKey, byte? ChannelIndex, IReadOnlyList<MessageResponse> Messages,
    IReadOnlyDictionary<string, string> UserNames, int MaxBytes, string? Draft, bool CliAvailable = false,
    IReadOnlyDictionary<string, string>? SenderContacts = null)
{
    /// <summary>
    /// Channel messages only carry the sender's name, so a private chat is possible when exactly one contact has that
    /// name. Built from the radio's contacts: name → public key, leaving out names shared by more than one contact.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SendersFrom(IEnumerable<ContactResponse> contacts) =>
        contacts.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().PublicKey, StringComparer.Ordinal);

    /// <summary>The contact's public key for a channel sender, or null when they can't be messaged privately (yet).</summary>
    public string? SenderKey(string? senderName) =>
        senderName is not null && SenderContacts is not null && SenderContacts.TryGetValue(senderName.Trim(), out var key) ? key : null;
}

/// <summary>
/// "Who is this?" for a channel sender who can't be messaged privately yet. Channel messages carry only the name;
/// a private message needs the node's key, which the radio learns from its advert.
/// </summary>
/// <param name="SameName">Contacts with this exact name, when there are several (pick one).</param>
/// <param name="Heard">Nodes with this name the radio heard advertising, not added yet ("add contacts manually" is on).</param>
/// <param name="ManualAddContacts">The radio waits for contacts to be added by hand; null when unknown (offline).</param>
public sealed record SenderHelpViewModel(
    string Name, byte? Channel, IReadOnlyList<ContactResponse> SameName, IReadOnlyList<DiscoveredNodeResponse> Heard,
    bool? ManualAddContacts, bool Connected, bool IsAdmin);
