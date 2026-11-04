using System.Security.Claims;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeshCore.Host.Api;

internal static class PrincipalExtensions
{
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirst(MeshClaims.UserId)?.Value ?? throw new InvalidOperationException("Signed-in user has no id claim.");
}

/// <summary>First-run setup, sign-in and password changes.</summary>
[ApiController]
[Route("api/v1/auth")]
[Tags("Auth")]
public sealed class AuthController(UserService users, TokenService tokens, TimeProvider time) : ControllerBase
{
    /// <summary>True until the first user (the Admin) has been created.</summary>
    [HttpGet("setup-required")]
    [AllowAnonymous]
    public async Task<SetupStatusResponse> SetupRequired(CancellationToken ct) => new(await users.IsSetupRequiredAsync(ct));

    /// <summary>Creates the first user as Admin and signs them in. Only works once.</summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Setup(CredentialsRequest request, CancellationToken ct)
    {
        var user = await users.SetupAsync(request.Username, request.Password, ct);
        return CreatedAtAction(nameof(Me), Token(user));
    }

    /// <summary>Signs in. Five wrong passwords lock the account for 15 minutes.</summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Token(CredentialsRequest request, CancellationToken ct)
    {
        var result = await users.LoginAsync(request.Username, request.Password, ct);
        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(Token(result.User!)),
            LoginOutcome.LockedOut => Problem(
                statusCode: StatusCodes.Status423Locked,
                title: "Account locked",
                detail: $"Too many failed sign-ins. Try again after {result.LockedUntil:u}."),
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Wrong username or password"),
        };
    }

    /// <summary>The signed-in user. Works even while a password change is pending.</summary>
    [HttpGet("me")]
    [Authorize(AuthPolicies.SignedIn)]
    public async Task<ActionResult<UserResponse>> Me(CancellationToken ct) =>
        await users.FindAsync(User.UserId(), ct) is { } user ? UserResponse.From(user, time.GetUtcNow()) : Unauthorized();

    /// <summary>Changes your own password and returns a fresh token (old tokens stop working).</summary>
    [HttpPost("change-password")]
    [Authorize(AuthPolicies.SignedIn)]
    [EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    public async Task<TokenResponse> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        Token(await users.ChangePasswordAsync(User.UserId(), request.CurrentPassword, request.NewPassword, ct));

    private TokenResponse Token(UserAccount user)
    {
        var token = tokens.Issue(user);
        return new TokenResponse(token.AccessToken, "Bearer", token.ExpiresAt, UserResponse.From(user, time.GetUtcNow()));
    }
}

/// <summary>User management. Admins only.</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(AuthPolicies.Admin)]
[Tags("Users")]
public sealed class UsersController(UserService users, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<UserResponse>> List(CancellationToken ct) =>
        (await users.ListAsync(ct)).Select(u => UserResponse.From(u, time.GetUtcNow()));

    [HttpGet("{id}")]
    public async Task<ActionResult<UserResponse>> Get(string id, CancellationToken ct) =>
        await users.FindAsync(id, ct) is { } u ? UserResponse.From(u, time.GetUtcNow()) : NotFound();

    /// <summary>Adds a user. They must change the temporary password at first sign-in.</summary>
    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct)
    {
        var user = await users.CreateAsync(request.Username, request.TemporaryPassword, request.Role, User.Identity!.Name!, ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, UserResponse.From(user, time.GetUtcNow()));
    }

    /// <summary>Changes role and/or enabled. Signs the user out everywhere. The last Admin can't be demoted or disabled.</summary>
    [HttpPut("{id}")]
    public async Task<UserResponse> Update(string id, UpdateUserRequest request, CancellationToken ct) =>
        UserResponse.From(await users.UpdateAsync(id, request.Role, request.Enabled, User.Identity!.Name!, ct), time.GetUtcNow());

    /// <summary>Sets a temporary password, clears any lockout, and signs the user out everywhere.</summary>
    [HttpPost("{id}/reset-password")]
    public async Task<UserResponse> ResetPassword(string id, ResetPasswordRequest request, CancellationToken ct) =>
        UserResponse.From(await users.ResetPasswordAsync(id, request.TemporaryPassword, User.Identity!.Name!, ct), time.GetUtcNow());

    /// <summary>Deletes a user. You can't delete yourself or the last Admin.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await users.DeleteAsync(id, User.UserId(), ct);
        return NoContent();
    }
}
