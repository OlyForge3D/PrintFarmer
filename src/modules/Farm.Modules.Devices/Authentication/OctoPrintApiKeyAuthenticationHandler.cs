using System.Security.Claims;
using System.Text.Encodings.Web;
using Farm.Modules.Devices.Services.OctoPrint;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Modules.Devices.Authentication;

/// <summary>
/// Constants identifying the OctoPrint API key authentication scheme, so it can be
/// registered once in <c>AuthenticationStartup</c> and referenced from
/// <c>[Authorize(AuthenticationSchemes = ...)]</c> without repeating the raw string.
/// </summary>
public static class OctoPrintApiKeyDefaults
{
    public const string AuthenticationScheme = "OctoPrintApiKey";
}

/// <summary>
/// Real ASP.NET Core authentication scheme for the OctoPrint-compatible <c>X-Api-Key</c>
/// header. Unlike the legacy <see cref="Farm.Modules.Devices.Filters.OctoPrintApiKeyAttribute"/>
/// (an MVC authorization filter), this handler runs as part of the standard authentication
/// pipeline, so a validated key produces a genuine <see cref="ClaimsPrincipal"/> for the
/// key's owning user *before* ASP.NET Core's authorization middleware evaluates
/// <c>[RequirePermission]</c>/<c>[Authorize]</c> — closing the gap where
/// <c>[AllowAnonymous]</c> previously skipped authorization entirely (see issue #1666).
/// </summary>
#pragma warning disable CS0618 // AuthenticationHandler constructor with ISystemClock is obsolete but required until ASP.NET packages support TimeProvider directly
public class OctoPrintApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOctoPrintAuthService authService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder, new TimeProviderSystemClock(options.CurrentValue.TimeProvider ?? TimeProvider.System))
#pragma warning restore CS0618
{
#pragma warning disable CS0618
    private sealed class TimeProviderSystemClock(TimeProvider tp) : ISystemClock
    {
        private readonly TimeProvider _tp = tp ?? TimeProvider.System;

        public DateTimeOffset UtcNow => _tp.GetUtcNow();
    }
#pragma warning restore CS0618

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKey = Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // No key presented — let another scheme (e.g. Bearer) attempt authentication
            // instead of outright failing the combined authenticate result.
            return AuthenticateResult.NoResult();
        }

        ClaimsPrincipal? principal = await authService.ResolveApiKeyPrincipalAsync(apiKey, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Invalid, expired, or unauthorized OctoPrint API key.");
        }

        var ticket = new AuthenticationTicket(principal, OctoPrintApiKeyDefaults.AuthenticationScheme);
        return AuthenticateResult.Success(ticket);
    }
}
