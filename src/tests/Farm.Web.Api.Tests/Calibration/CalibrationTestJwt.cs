using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Mints the ordinary session JWTs that a split deployment shares between the API and the slicer
/// host (same key, issuer and audience, as the compose templates require).
/// </summary>
internal static class CalibrationTestJwt
{
    public const string Key =
        "PrintFarmerSplitCalibrationResolutionTestSigningKey-0123456789";

    public const string Issuer = "PrintFarmer";

    public const string Audience = "PrintFarmer";

    public static string Create(
        Guid userId,
        IEnumerable<string>? permissions = null,
        IEnumerable<string>? roles = null) =>
        Create(Key, Issuer, Audience, userId, permissions, roles);

    public static string Create(
        string key,
        string issuer,
        string audience,
        Guid userId,
        IEnumerable<string>? permissions = null,
        IEnumerable<string>? roles = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "calibration-test-user"),
        ];
        foreach (string permission in permissions ?? [])
        {
            claims.Add(new Claim(PrintFarmerPermissions.ClaimType, permission));
        }

        foreach (string role in roles ?? [])
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
