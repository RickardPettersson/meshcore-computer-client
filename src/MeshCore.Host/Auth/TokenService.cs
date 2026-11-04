using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MeshCore.Host.Auth;

/// <summary>Claim names in our access tokens (short, JWT-style; inbound claim mapping is off).</summary>
public static class MeshClaims
{
    public const string UserId = "sub";
    public const string Username = "name";
    public const string Role = "role";
    public const string SecurityStamp = "sstamp";
    public const string MustChangePassword = "mcp";
}

/// <summary>
/// The HMAC key that signs access tokens. Generated on first run, stored in the user file encrypted
/// with Data Protection (DPAPI-protected keys on Windows), and kept in memory after that.
/// </summary>
public sealed class JwtKeyProvider(IUserStore store, IDataProtectionProvider dataProtection)
{
    private const int KeySizeBytes = 64; // HMAC-SHA512 block size
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MeshCore.Auth.JwtSigningKey.v1");
    private SymmetricSecurityKey? _key;

    public SymmetricSecurityKey SigningKey => _key ?? throw new InvalidOperationException("Call InitializeAsync at startup first.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var protectedKey = await store.UpdateAsync(doc =>
        {
            if (doc.JwtKeyProtected is { } existing)
            {
                return (doc, existing);
            }
            var created = _protector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes)));
            return (doc with { JwtKeyProtected = created }, created);
        }, cancellationToken);
        _key =new SymmetricSecurityKey(Convert.FromBase64String(_protector.Unprotect(protectedKey))) { KeyId = "meshcore-1" };
    }
}

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class TokenService(JwtKeyProvider keys, IOptions<AuthOptions> options, TimeProvider time)
{
    public const string Issuer = "MeshCoreComputerClient";
    public const string Audience = "MeshCore.Api";

    public IssuedToken Issue(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = time.GetUtcNow();
        var expires = now + options.Value.TokenLifetime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.HmacSha512),
            Subject = new ClaimsIdentity(
            [
                new Claim(MeshClaims.UserId, user.Id),
                new Claim(MeshClaims.Username, user.Username),
                new Claim(MeshClaims.Role, user.Role.ToString()),
                new Claim(MeshClaims.SecurityStamp, user.SecurityStamp),
                new Claim(MeshClaims.MustChangePassword, user.MustChangePassword ? "true" : "false"),
            ]),
        };
        return new IssuedToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKeyResolver = (_, _, _, _) => [keys.SigningKey],
        ValidAlgorithms = [SecurityAlgorithms.HmacSha512],
        NameClaimType = MeshClaims.Username,
        RoleClaimType = MeshClaims.Role,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
