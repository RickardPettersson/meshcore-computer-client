namespace MeshCore.Host.Auth;

public enum UserRole
{
    User = 0,
    Admin = 1,
}

/// <summary>
/// A login. Passwords are stored only as an ASP.NET Identity PBKDF2 hash
/// (HMAC-SHA512, 100k iterations, per-user salt); they can't be recovered.
/// Immutable: change with <c>with</c> and save through <see cref="IUserStore"/>.
/// </summary>
public sealed record UserAccount
{
    public required string Id { get; init; }

    public required string Username { get; init; }

    public required string PasswordHash { get; init; }

    public UserRole Role { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Set for new users and after an admin reset; the user must pick their own password next.</summary>
    public bool MustChangePassword { get; init; }

    /// <summary>Changes whenever access should be revoked (password, role, disable). Tokens carry it.</summary>
    public required string SecurityStamp { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? LastLoginUtc { get; init; }

    public int FailedLogins { get; init; }

    public DateTimeOffset? LockoutEndUtc { get; init; }

    public static string NewStamp() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
}

/// <summary>Everything in <c>appsettings.auth.json</c> under <c>"Auth"</c>.</summary>
public sealed record AuthDocument
{
    /// <summary>The JWT signing key, encrypted with ASP.NET Core Data Protection.</summary>
    public string? JwtKeyProtected { get; init; }

    public IReadOnlyList<UserAccount> Users { get; init; } = [];
}
