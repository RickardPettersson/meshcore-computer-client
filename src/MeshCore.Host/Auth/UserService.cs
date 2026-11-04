using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Auth;

public enum AuthError
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
}

/// <summary>An account operation was refused. Maps to 400/404/409/403.</summary>
public sealed class AuthException(AuthError error, string message) : Exception(message)
{
    public AuthError Error { get; } = error;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);

    public int LockoutAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
}

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    LockedOut,
}

public sealed record LoginResult(LoginOutcome Outcome, UserAccount? User = null, DateTimeOffset? LockedUntil = null);

/// <summary>
/// Account rules: first-run setup, login with lockout, password changes, and admin user management.
/// The first user is always an Admin, and the last enabled Admin can't be removed or demoted.
/// </summary>
public sealed partial class UserService(IUserStore store, IOptions<AuthOptions> options, TimeProvider time, ILogger<UserService> logger)
{
    private static readonly PasswordHasher<UserAccount> Hasher = new();

    // A real hash of a random password, verified against when the username doesn't exist,
    // so response time doesn't reveal which usernames are valid.
    private static readonly string DummyHash = Hasher.HashPassword(null!, Guid.NewGuid().ToString());

    private readonly AuthOptions _options = options.Value;

    public async Task<bool> IsSetupRequiredAsync(CancellationToken cancellationToken = default) =>
        (await store.ReadAsync(cancellationToken)).Users.Count == 0;

    public async Task<UserAccount?> FindAsync(string id, CancellationToken cancellationToken = default) =>
        (await store.ReadAsync(cancellationToken)).Users.FirstOrDefault(u => u.Id == id);

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        [.. (await store.ReadAsync(cancellationToken)).Users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Creates the first account (an Admin). Only works while there are no users.</summary>
    public async Task<UserAccount> SetupAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        Validate(username, password);
        var user = await store.UpdateAsync(doc =>
        {
            if (doc.Users.Count > 0)
            {
                throw new AuthException(AuthError.Conflict, "Setup has already been completed.");
            }
            var admin = NewUser(username, password, UserRole.Admin, mustChange: false) with { LastLoginUtc = time.GetUtcNow() };
            return (doc with { Users = [admin] }, admin);
        }, cancellationToken);

        LogSetup(user.Username);
        return user;
    }

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var now = time.GetUtcNow();
        var result = await store.UpdateAsync(doc =>
        {
            var user = doc.Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                Hasher.VerifyHashedPassword(null!, DummyHash, password);
                return (doc, new LoginResult(LoginOutcome.InvalidCredentials));
            }

            if (user.LockoutEndUtc > now)
            {
                return (doc, new LoginResult(LoginOutcome.LockedOut, LockedUntil: user.LockoutEndUtc));
            }

            var verdict = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (verdict == PasswordVerificationResult.Failed || !user.Enabled)
            {
                var failures = user.FailedLogins + 1;
                var locked = failures >= _options.LockoutAttempts;
                var updated = user with
                {
                    FailedLogins = locked ? 0 : failures,
                    LockoutEndUtc = locked ? now + _options.LockoutDuration : user.LockoutEndUtc,
                };
                return (Replace(doc, updated), locked
                    ? new LoginResult(LoginOutcome.LockedOut, LockedUntil: updated.LockoutEndUtc)
                    : new LoginResult(LoginOutcome.InvalidCredentials));
            }

            var signedIn = user with
            {
                FailedLogins = 0,
                LockoutEndUtc = null,
                LastLoginUtc = now,
                PasswordHash = verdict == PasswordVerificationResult.SuccessRehashNeeded ? Hasher.HashPassword(user, password) : user.PasswordHash,
            };
            return (Replace(doc, signedIn), new LoginResult(LoginOutcome.Success, signedIn));
        }, cancellationToken);

        if (result.Outcome == LoginOutcome.Success)
        {
            LogLogin(result.User!.Username);
        }
        else
        {
            LogLoginFailed(username, result.Outcome);
        }
        return result;
    }

    public async Task<UserAccount> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await store.UpdateAsync(doc =>
        {
            var user = Require(doc, userId);
            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            {
                throw new AuthException(AuthError.Validation, "The current password is wrong.");
            }
            if (CredentialRules.CheckPassword(newPassword, user.Username) is { } problem)
            {
                throw new AuthException(AuthError.Validation, problem);
            }
            if (newPassword == currentPassword)
            {
                throw new AuthException(AuthError.Validation, "The new password must be different.");
            }
            var updated = user with
            {
                PasswordHash = Hasher.HashPassword(user, newPassword),
                MustChangePassword = false,
                SecurityStamp = UserAccount.NewStamp(),
            };
            return (Replace(doc, updated), updated);
        }, cancellationToken);

        LogPasswordChanged(user.Username);
        return user;
    }

    // ---- Admin operations ---------------------------------------------------

    /// <summary>Adds a user with a temporary password they must change at first login.</summary>
    public async Task<UserAccount> CreateAsync(string username, string temporaryPassword, UserRole role, string actingAdmin, CancellationToken cancellationToken = default)
    {
        Validate(username, temporaryPassword);
        var user = await store.UpdateAsync(doc =>
        {
            if (doc.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AuthException(AuthError.Conflict, $"A user named '{username}' already exists.");
            }
            var created = NewUser(username, temporaryPassword, role, mustChange: true);
            return (doc with { Users = [.. doc.Users, created] }, created);
        }, cancellationToken);

        LogUserChange(actingAdmin, "created", user.Username, $"role {user.Role}");
        return user;
    }

    /// <summary>Changes role and/or enabled state. Either change signs the user out everywhere.</summary>
    public async Task<UserAccount> UpdateAsync(string userId, UserRole? role, bool? enabled, string actingAdmin, CancellationToken cancellationToken = default)
    {
        var user = await store.UpdateAsync(doc =>
        {
            var user = Require(doc, userId);
            var updated = user with { Role = role ?? user.Role, Enabled = enabled ?? user.Enabled };
            if (updated == user)
            {
                return (doc, user);
            }
            GuardLastAdmin(doc, user, updated);
            updated = updated with { SecurityStamp = UserAccount.NewStamp() };
            return (Replace(doc, updated), updated);
        }, cancellationToken);

        LogUserChange(actingAdmin, "updated", user.Username, $"role {user.Role}, enabled {user.Enabled}");
        return user;
    }

    public async Task<UserAccount> ResetPasswordAsync(string userId, string temporaryPassword, string actingAdmin, CancellationToken cancellationToken = default)
    {
        var user = await store.UpdateAsync(doc =>
        {
            var user = Require(doc, userId);
            if (CredentialRules.CheckPassword(temporaryPassword, user.Username) is { } problem)
            {
                throw new AuthException(AuthError.Validation, problem);
            }
            var updated = user with
            {
                PasswordHash = Hasher.HashPassword(user, temporaryPassword),
                MustChangePassword = true,
                SecurityStamp = UserAccount.NewStamp(),
                FailedLogins = 0,
                LockoutEndUtc = null,
            };
            return (Replace(doc, updated), updated);
        }, cancellationToken);

        LogUserChange(actingAdmin, "reset the password of", user.Username, "");
        return user;
    }

    public async Task DeleteAsync(string userId, string actingAdminId, CancellationToken cancellationToken = default)
    {
        var (deleted, by) = await store.UpdateAsync(doc =>
        {
            var user = Require(doc, userId);
            if (user.Id == actingAdminId)
            {
                throw new AuthException(AuthError.Forbidden, "You can't delete your own account.");
            }
            GuardLastAdmin(doc, user, null);
            var actor = doc.Users.FirstOrDefault(u => u.Id == actingAdminId)?.Username ?? actingAdminId;
            return (doc with { Users = [.. doc.Users.Where(u => u.Id != userId)] }, (user.Username, actor));
        }, cancellationToken);

        LogUserChange(by, "deleted", deleted, "");
    }

    // ---- Helpers ------------------------------------------------------------

    private UserAccount NewUser(string username, string password, UserRole role, bool mustChange)
    {
        var user = new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            PasswordHash = "",
            Role = role,
            MustChangePassword = mustChange,
            SecurityStamp = UserAccount.NewStamp(),
            CreatedUtc = time.GetUtcNow(),
        };
        return user with { PasswordHash = Hasher.HashPassword(user, password) };
    }

    private static void Validate(string username, string password)
    {
        if ((CredentialRules.CheckUsername(username) ?? CredentialRules.CheckPassword(password, username)) is { } problem)
        {
            throw new AuthException(AuthError.Validation, problem);
        }
    }

    private static UserAccount Require(AuthDocument doc, string userId) =>
        doc.Users.FirstOrDefault(u => u.Id == userId) ?? throw new AuthException(AuthError.NotFound, "No such user.");

    private static AuthDocument Replace(AuthDocument doc, UserAccount user) =>
        doc with { Users = [.. doc.Users.Select(u => u.Id == user.Id ? user : u)] };

    /// <summary>Refuses a change that would leave no enabled Admin (<paramref name="after"/> null = delete).</summary>
    private static void GuardLastAdmin(AuthDocument doc, UserAccount before, UserAccount? after)
    {
        static bool IsActiveAdmin(UserAccount u) => u is { Role: UserRole.Admin, Enabled: true };
        var losesAdmin = IsActiveAdmin(before) && (after is null || !IsActiveAdmin(after));
        if (losesAdmin && doc.Users.Count(IsActiveAdmin) == 1)
        {
            throw new AuthException(AuthError.Conflict, "This is the last enabled Admin. Make another user Admin first.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "First-run setup completed: Admin '{Username}' created")]
    private partial void LogSetup(string username);

    [LoggerMessage(Level = LogLevel.Information, Message = "User '{Username}' signed in")]
    private partial void LogLogin(string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in for '{Username}': {Outcome}")]
    private partial void LogLoginFailed(string username, LoginOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "User '{Username}' changed their password")]
    private partial void LogPasswordChanged(string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin '{Admin}' {Action} user '{Username}' {Details}")]
    private partial void LogUserChange(string admin, string action, string username, string details);
}
