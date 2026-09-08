using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Infrastructure.Identity;

public class JwtOptions
{
    public string Secret { get; set; } = "dev-only-insecure-secret-change-me-please-32b";
    public string Issuer { get; set; } = "taskflow";
    public string Audience { get; set; } = "taskflow-web";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string hash)
    {
        var parts = hash.Split('.', 3);
        if (parts.Length != 3) return false;
        var iterations = int.Parse(parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        var key = Convert.FromBase64String(parts[2]);
        var attempt = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, key.Length);
        return CryptographicOperations.FixedTimeEquals(attempt, key);
    }
}

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options) => _options = options.Value;

    public TokenPair Issue(UserEntity user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new("role", user.Role.ToString()),
            new("stamp", user.SecurityStamp),
        };
        if (user.MustChangePassword)
        {
            // Blocks every protected endpoint except the forced change-password flow.
            claims.Add(new Claim("mcp", "1"));
        }

        var jwt = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: creds);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(jwt);
        var refreshToken = BuildRefreshToken(user.Id, user.SecurityStamp);
        return new TokenPair(accessToken, refreshToken);
    }

    public RefreshTokenPrincipal? ValidateRefreshToken(string refreshToken)
    {
        try
        {
            // Format: {userId}.{unixExpiry}.{securityStamp}.{signature}
            var parts = refreshToken.Split('.', 4);
            if (parts.Length != 4) return null;

            var userId = Guid.Parse(parts[0]);
            var expires = long.Parse(parts[1]);
            if (DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow) return null;

            var expected = Sign($"{parts[0]}.{parts[1]}.{parts[2]}");
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(parts[3]), Convert.FromBase64String(expected))
                ? new RefreshTokenPrincipal(userId, parts[2])
                : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildRefreshToken(Guid userId, string securityStamp)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(_options.RefreshTokenDays).ToUnixTimeSeconds();
        var payload = $"{userId}.{expires}.{securityStamp}";
        return $"{payload}.{Sign(payload)}";
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}

public class DatabaseIdentityProvider : IIdentityProvider
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;

    public DatabaseIdentityProvider(IUserRepository users, IPasswordHasher hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    public async Task<UserEntity?> AuthenticateAsync(string email, string password, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || !_hasher.Verify(password, user.PasswordHash))
        {
            return null;
        }
        return user;
    }
}
