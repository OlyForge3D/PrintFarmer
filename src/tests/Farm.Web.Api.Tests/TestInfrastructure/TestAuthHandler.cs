using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Tests.TestInfrastructure
{
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestScheme";
    // Adapter to provide the (obsolete) ISystemClock interface from a TimeProvider instance.
    // This adapter is required because the AuthenticationHandler base in the project's
    // referenced ASP.NET assemblies currently expects an ISystemClock. To fully remove
    // this adapter we'd need to upgrade the ASP.NET packages to a version that supports
    // TimeProvider in the base constructor.
#pragma warning disable CS0618
    private sealed class TimeProviderSystemClock : Microsoft.AspNetCore.Authentication.ISystemClock
    {
        private readonly TimeProvider _tp;
        public TimeProviderSystemClock(TimeProvider tp) => _tp = tp ?? TimeProvider.System;
        public DateTimeOffset UtcNow => _tp.GetUtcNow();
    }
#pragma warning restore CS0618
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder, new TimeProviderSystemClock(options.CurrentValue.TimeProvider ?? TimeProvider.System))
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Deterministic test identity. Allow tests to override roles via
            // the X-Test-Roles request header (comma-separated). This lets tests
            // simulate non-admin users while still using the Test auth scheme.
            var roleHeader = Request.Headers.ContainsKey("X-Test-Roles") ? Request.Headers["X-Test-Roles"].ToString() : null;
            var roles = new List<string>();
            if (!string.IsNullOrWhiteSpace(roleHeader))
            {
                roles.AddRange(roleHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            if (roles.Count == 0)
            {
                // default to farm_admin to preserve existing test expectations
                roles.Add("farm_admin");
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000001"),
                new Claim(ClaimTypes.Name, "testuser")
            };
            foreach (var r in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, r));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
