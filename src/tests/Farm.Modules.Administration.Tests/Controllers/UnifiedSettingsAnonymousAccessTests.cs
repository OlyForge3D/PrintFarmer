using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

/// <summary>
/// Security regression coverage for issue #950: the three <c>[AllowAnonymous]</c> endpoints on
/// <c>UnifiedSettingsController</c>. The generic settings surface must not leak configuration to
/// unauthenticated callers, while the tokenless printer-discovery microservice must still be able to
/// read the one section it depends on (<c>NetworkDiscovery</c>) and post its heartbeat.
/// </summary>
/// <remarks>
/// These are HTTP-level tests: the behaviour under test is the authorization pipeline plus the
/// in-controller anonymous allowlist, neither of which a direct method call against the controller
/// instance can observe. <c>Security:DevModeBypassAuth = false</c> ensures auth actually runs so the
/// anonymous paths are genuinely unauthenticated.
/// </remarks>
[Trait("Category", "Integration")]
public class UnifiedSettingsAnonymousAccessTests : IClassFixture<UnifiedSettingsAnonymousAccessTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        })
        {
        }
    }

    private readonly Factory _factory;

    public UnifiedSettingsAnonymousAccessTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    // ─── Endpoint 1: GET /api/settings (aggregate read) ─────────────────────

    /// <summary>
    /// The aggregate read returns every non-secret section verbatim — internal URLs, intervals,
    /// paths, feature flags, hostnames. It has no anonymous consumer (the discovery service reads a
    /// single section via the per-key endpoint), so it must require authentication. Before #950 it
    /// carried <c>[AllowAnonymous]</c> and returned 200 to anyone.
    /// </summary>
    [Fact]
    public async Task GetRoot_Unauthenticated_Returns401()
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage resp = await anon.GetAsync("/api/settings");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the aggregate settings read exposes the whole configuration surface and has no anonymous consumer");
    }

    /// <summary>
    /// A signed-in user (any role) must still be able to read the aggregate surface — the settings
    /// UI depends on it. Removing <c>[AllowAnonymous]</c> must not break authenticated reads.
    /// </summary>
    [Fact]
    public async Task GetRoot_AsAuthenticatedUser_Returns200()
    {
        using HttpClient user = await _factory.CreateAuthenticatedClientAsync(
            username: "reader-user",
            email: "reader@example.com",
            password: "ReaderPassword123!");

        HttpResponseMessage resp = await user.GetAsync("/api/settings");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Endpoint 2: GET /api/settings/{keyName} (per-key read) ─────────────

    /// <summary>
    /// The allowlisted section stays anonymously readable so the discovery microservice, which holds
    /// no user token, can poll its configuration. This is the deliberate exception that the allowlist
    /// permits.
    /// </summary>
    [Fact]
    public async Task GetByKey_AllowlistedNetworkDiscovery_Unauthenticated_Returns200()
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage resp = await anon.GetAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the printer-discovery microservice reads NetworkDiscovery anonymously and must keep working");
    }

    /// <summary>
    /// Any section not on the anonymous allowlist must fail closed for unauthenticated callers. This
    /// is the core of the fix: the previous blocklist failed open, exposing every non-secret section;
    /// the allowlist exposes only what is explicitly listed.
    /// </summary>
    [Fact]
    public async Task GetByKey_NonAllowlistedSection_Unauthenticated_Returns401()
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage resp = await anon.GetAsync(
            $"/api/settings/{SignalRSettings.SectionName}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a non-allowlisted section must not be readable without authentication");
    }

    /// <summary>
    /// The same non-allowlisted section must remain readable for a signed-in user — the allowlist
    /// only gates anonymous access, not authenticated access.
    /// </summary>
    [Fact]
    public async Task GetByKey_NonAllowlistedSection_AsAuthenticatedUser_Returns200()
    {
        using HttpClient user = await _factory.CreateAuthenticatedClientAsync(
            username: "reader-user2",
            email: "reader2@example.com",
            password: "ReaderPassword123!");

        HttpResponseMessage resp = await user.GetAsync(
            $"/api/settings/{SignalRSettings.SectionName}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Endpoint 3: POST /api/settings/{keyName}/heartbeat ─────────────────

    /// <summary>
    /// The heartbeat stays anonymous by design — the discovery microservice posts it on a timer with
    /// no user token. This test documents and pins that deliberate contract (see the endpoint's
    /// remarks): closing it here without a coordinated service-credential change would break discovery.
    /// </summary>
    [Fact]
    public async Task Heartbeat_Unauthenticated_Returns204()
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage resp = await anon.PostAsJsonAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}/heartbeat",
            new { timestamp = System.DateTime.UtcNow });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the tokenless discovery microservice must still be able to post its heartbeat");
    }

    /// <summary>
    /// The heartbeat's narrow scope must hold: it only accepts <c>NetworkDiscovery</c>. Any other key
    /// is rejected, so the anonymous write cannot be repurposed to touch a different section.
    /// </summary>
    [Fact]
    public async Task Heartbeat_NonDiscoveryKey_Unauthenticated_Returns400()
    {
        using HttpClient anon = _factory.CreateClient();

        HttpResponseMessage resp = await anon.PostAsJsonAsync(
            $"/api/settings/{SignalRSettings.SectionName}/heartbeat",
            new { timestamp = System.DateTime.UtcNow });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the heartbeat endpoint only supports the NetworkDiscovery section");
    }
}
