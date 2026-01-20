using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Tests.TestInfrastructure
{
#pragma warning disable CS0618 // AuthenticationHandler constructor with ISystemClock is obsolete but required until ASP.NET packages support TimeProvider directly
    public class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder, new TimeProviderSystemClock(options.CurrentValue.TimeProvider ?? TimeProvider.System))
#pragma warning restore CS0618
    {
        public const string SchemeName = "TestScheme";
        // Adapter to provide the (obsolete) ISystemClock interface from a TimeProvider instance.
        // This adapter is required because the AuthenticationHandler base in the project's
        // referenced ASP.NET assemblies currently expects an ISystemClock. To fully remove
        // this adapter we'd need to upgrade the ASP.NET packages to a version that supports
        // TimeProvider in the base constructor.
#pragma warning disable CS0618
        private sealed class TimeProviderSystemClock(TimeProvider tp) : ISystemClock
        {
            private readonly TimeProvider _tp = tp ?? TimeProvider.System;

            public DateTimeOffset UtcNow => _tp.GetUtcNow();
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Deterministic test identity. Allow tests to override roles via
            // the X-Test-Roles request header (comma-separated). This lets tests
            // simulate non-admin users while still using the Test auth scheme.
            string? roleHeader = Request.Headers.ContainsKey("X-Test-Roles") ? Request.Headers["X-Test-Roles"].ToString() : null;
            List<string> roles = new List<string>();
            if (!string.IsNullOrWhiteSpace(roleHeader))
            {
                roles.AddRange(roleHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (roles.Count == 0)
            {
                // default to farm_admin to preserve existing test expectations
                roles.Add("farm_admin");
            }

            List<Claim> claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                new Claim(ClaimTypes.Name, "testuser")
            };
            foreach (string r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }

            ClaimsIdentity identity = new ClaimsIdentity(claims, SchemeName);
            ClaimsPrincipal principal = new ClaimsPrincipal(identity);
            AuthenticationTicket ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
