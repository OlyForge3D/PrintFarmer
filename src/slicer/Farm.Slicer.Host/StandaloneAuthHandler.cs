using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Host;

/// <summary>
/// A pass-through authentication handler that auto-authenticates every request
/// as an admin user. Used during the transitional standalone-host phase so
/// that <c>[Authorize]</c> attributes on module controllers do not block
/// requests. Will be replaced with real JWT / API-key authentication once
/// the slicer host is deployed behind a gateway.
/// </summary>
public sealed class StandaloneAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.Name, "standalone-admin"),
            new Claim(ClaimTypes.Role, "farm_admin"),
        ];

        ClaimsIdentity identity = new(claims, Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
