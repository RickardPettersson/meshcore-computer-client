using System.Text.RegularExpressions;

namespace MeshCore.Host.Auth;

/// <summary>Username and password rules, shared by the API and (in Phase 5) the website forms.</summary>
public static partial class CredentialRules
{
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 32;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    /// <summary>Null if valid, otherwise why not.</summary>
    public static string? CheckUsername(string? username) => username switch
    {
        null or "" => "Username is required.",
        { Length: < MinUsernameLength or > MaxUsernameLength } => $"Username must be {MinUsernameLength}–{MaxUsernameLength} characters.",
        _ when !UsernameRegex().IsMatch(username) => "Username may only contain letters, digits, '.', '_' and '-'.",
        _ => null,
    };

    /// <summary>Null if valid, otherwise why not. Length matters most (NIST SP 800-63B); no composition rules.</summary>
    public static string? CheckPassword(string? password, string? username = null) => password switch
    {
        null or "" => "Password is required.",
        { Length: < MinPasswordLength } => $"Password must be at least {MinPasswordLength} characters.",
        { Length: > MaxPasswordLength } => $"Password must be at most {MaxPasswordLength} characters.",
        _ when username is not null && password.Contains(username, StringComparison.OrdinalIgnoreCase) => "Password must not contain the username.",
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex UsernameRegex();
}
