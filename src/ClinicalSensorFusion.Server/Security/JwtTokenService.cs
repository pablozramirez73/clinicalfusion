using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClinicalSensorFusion.Server.Security;

/// <summary>Token di accesso per chi usa le API senza browser (sezione <c>Jwt</c>).</summary>
public sealed class JwtOptions
{
    public string Issuer { get; init; } = "clinical-sensor-fusion";

    public string Audience { get; init; } = "clinical-sensor-fusion-api";

    /// <summary>
    /// Segreto HMAC-SHA256, almeno 32 byte (da variabile d'ambiente o secret store, mai nel repository).
    /// Se manca, a ogni avvio se ne genera uno casuale: i token funzionano ma scadono al riavvio e non
    /// valgono su più istanze.
    /// </summary>
    public string? SigningKey { get; init; }

    /// <summary>Durata breve: un token non si può revocare prima della scadenza.</summary>
    public int LifetimeMinutes { get; init; } = 30;
}

public sealed record AccessToken(string Token, int ExpiresInSeconds);

/// <summary>
/// Emette e descrive come validare i JWT dei clinici. Stessi utenti e stessi permessi del cookie della
/// dashboard: cambia solo il trasporto (<c>Authorization: Bearer</c>), adatto a Swagger e agli script.
/// </summary>
public sealed class JwtTokenService
{
    public const string DisplayNameClaim = "display_name";

    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;
    private readonly SymmetricSecurityKey _key;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _clock = clock;
        byte[] secret;
        if (string.IsNullOrEmpty(_options.SigningKey))
        {
            secret = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning("Jwt:SigningKey non configurata: chiave temporanea, i token emessi scadono al riavvio.");
        }
        else
        {
            secret = Encoding.UTF8.GetBytes(_options.SigningKey);
            if (secret.Length < 32)
            {
                throw new InvalidOperationException("Jwt:SigningKey deve essere di almeno 32 byte.");
            }
        }

        _key = new SymmetricSecurityKey(secret);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = _options.Issuer,
        ValidAudience = _options.Audience,
        IssuerSigningKey = _key,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Name,
        RoleClaimType = "role",
    };

    public AccessToken Create(DashboardUser user)
    {
        var now = _clock.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(_options.LifetimeMinutes, 1, 24 * 60));
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime).UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.UserName),
                new Claim(JwtRegisteredClaimNames.Name, user.UserName),
                new Claim(DisplayNameClaim, user.DisplayName ?? user.UserName),
                new Claim("role", Roles.Clinician),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, (int)lifetime.TotalSeconds);
    }
}
