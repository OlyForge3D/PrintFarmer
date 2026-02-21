using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Infrastructure.Services.Monitoring;

public class MonitoringSessionService(IConfiguration configuration) : IMonitoringSessionService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private const string MonitoringPurpose = "monitoring-session";

    public string CreateMonitoringToken(string username)
    {
        var key = GetSigningKey();
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = configuration["Jwt:Issuer"] ?? "PrintFarmer",
            Audience = "PrintFarmer-Monitoring",
            Expires = DateTime.UtcNow.Add(TokenLifetime),
            SigningCredentials = creds,
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim("purpose", MonitoringPurpose),
            ]),
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public async Task<MonitoringTokenValidationResult> ValidateMonitoringTokenAsync(string token)
    {
        try
        {
            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = GetSigningKey(),
                ValidateIssuer = true,
                ValidIssuer = configuration["Jwt:Issuer"] ?? "PrintFarmer",
                ValidateAudience = true,
                ValidAudience = "PrintFarmer-Monitoring",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            });

            if (!result.IsValid)
            {
                return new MonitoringTokenValidationResult(false);
            }

            var purpose = result.ClaimsIdentity.FindFirst("purpose")?.Value;
            if (purpose != MonitoringPurpose)
            {
                return new MonitoringTokenValidationResult(false);
            }

            var username = result.ClaimsIdentity.Name;
            return new MonitoringTokenValidationResult(true, username);
        }
        catch
        {
            return new MonitoringTokenValidationResult(false);
        }
    }

    private SymmetricSecurityKey GetSigningKey()
    {
        var rawKey = configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is not configured");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(rawKey));
    }
}
