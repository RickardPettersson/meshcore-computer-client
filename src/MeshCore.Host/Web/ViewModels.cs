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
    IReadOnlyDictionary<string, string> UserNames, int MaxBytes, string? Draft, bool CliAvailable = false);
