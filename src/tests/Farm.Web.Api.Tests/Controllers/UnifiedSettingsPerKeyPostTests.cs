using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the per-key settings save endpoint
/// (<c>POST /api/settings/{keyName}</c>) — the primary save path used by the
/// Settings page under the group-save UX introduced in #935.
/// </summary>
/// <remarks>
/// Regression coverage for the two defects that shipped from Epic #931 and were
/// caught in the multi-reviewer gate #941:
/// <list type="number">
///   <item>The per-key endpoint was missing the <c>farm_admin</c> role gate that
///     the bulk endpoint has, so any authenticated user could write app-wide
///     settings.</item>
///   <item>The per-key endpoint did not call <c>IValidatableSetting.Validate()</c>,
///     so invalid values that the bulk endpoint rejects with a structured 400
///     would silently persist.</item>
/// </list>
/// These are HTTP-level tests because both defects are attribute/pipeline behaviour
/// that unit tests against the controller instance can't observe (auth filters and
/// model-binding run at the pipeline level, not on direct method calls).
/// </remarks>
[Trait("Category", "Integration")]
public class UnifiedSettingsPerKeyPostTests : IAsyncLifetime
{
    private CustomWebApplicationFactory _factory = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task InitializeAsync()
    {
        // Disable DevModeBypassAuth so auth actually runs — Development environment
        // defaults would otherwise bypass authorization on GET and mask regressions.
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        });
        return _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    // ─── Defect 1: farm_admin role required ─────────────────────────────────

    /// <summary>
    /// A non-admin authenticated user must not be able to write application
    /// settings via the per-key endpoint. Before #941 this returned 200 OK
    /// because <c>UpdateSettingsByKeyNameAsync</c> was decorated with only the
    /// class-level <c>[Authorize]</c>. It must now match the bulk endpoint and
    /// require the <c>farm_admin</c> role — i.e. return 403 Forbidden.
    /// </summary>
    [Fact]
    public async Task Post_AsNonAdminAuthenticated_Returns403Forbidden()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "regular-user",
            email: "regular@example.com",
            password: "RegularPassword123!");

        NetworkDiscoverySettings payload = new()
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "10.0.0.0/24" },
        };

        HttpResponseMessage resp = await client.PostAsJsonAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the per-key settings save endpoint must require farm_admin, matching the bulk endpoint");
    }

    /// <summary>
    /// Unauthenticated calls must fail closed at the pipeline before the role
    /// check runs. Guards against a regression that removes both the
    /// class-level <c>[Authorize]</c> and the method-level role check at once.
    /// </summary>
    [Fact]
    public async Task Post_Unauthenticated_Returns401Unauthorized()
    {
        using HttpClient anon = _factory.CreateClient();

        NetworkDiscoverySettings payload = new()
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "10.0.0.0/24" },
        };

        HttpResponseMessage resp = await anon.PostAsJsonAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A farm_admin posting a valid payload succeeds. This is the happy path
    /// the Settings page exercises on every group save.
    /// </summary>
    [Fact]
    public async Task Post_AsAdminWithValidPayload_Returns200AndPersists()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();

        NetworkDiscoverySettings payload = new()
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "192.168.42.0/24" },
            ClientTimeoutMs = 500,
            MaxConcurrentRequests = 10,
        };

        HttpResponseMessage saveResp = await admin.PostAsJsonAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        saveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify persistence by reading the same section back through the GET
        // endpoint (which is [AllowAnonymous], so the anon client suffices).
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage readResp = await anon.GetAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}");

        readResp.StatusCode.Should().Be(HttpStatusCode.OK);
        NetworkDiscoverySettings? read = await readResp.Content.ReadFromJsonAsync<NetworkDiscoverySettings>(JsonOptions);
        read.Should().NotBeNull();
        read!.DiscoverySubnets.Should().ContainSingle().Which.Should().Be("192.168.42.0/24");
        read.ClientTimeoutMs.Should().Be(500);
        read.MaxConcurrentRequests.Should().Be(10);
    }

    // ─── Defect 2: validation must run and return the structured error shape ─

    /// <summary>
    /// A farm_admin posting an INVALID payload to a section that implements
    /// <c>IValidatableSetting</c> must be rejected with a structured 400 that
    /// carries <c>message</c> (string) and <c>errors</c> (dictionary). Before
    /// #941 the per-key path never called <c>Validate()</c>, so this
    /// <c>"not-a-cidr"</c> subnet would silently persist. The response shape
    /// must match the bulk endpoint's — the React SettingsPage error parser
    /// relies on the <c>errors</c> map to attach per-field messages to inputs.
    /// </summary>
    [Fact]
    public async Task Post_AsAdminWithInvalidValidatablePayload_Returns400WithStructuredErrors()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();

        // NetworkDiscoverySettings.Validate() rejects non-CIDR subnet strings.
        NetworkDiscoverySettings payload = new()
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "not-a-cidr" },
        };

        HttpResponseMessage resp = await admin.PostAsJsonAsync(
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The body must have both `message` (string) and a populated `errors`
        // dictionary. The SettingsPage error parser (see SettingsPage.tsx:120-141)
        // requires exactly this shape: `errors` as a map, `message` as a string.
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("message", out JsonElement messageProp).Should().BeTrue();
        messageProp.ValueKind.Should().Be(JsonValueKind.String);
        messageProp.GetString().Should().Contain(NetworkDiscoverySettings.SectionName);

        body.TryGetProperty("errors", out JsonElement errorsProp).Should().BeTrue();
        errorsProp.ValueKind.Should().Be(JsonValueKind.Object);
        errorsProp.EnumerateObject().Should().NotBeEmpty(
            "the errors dictionary must contain at least one entry so the UI can render a per-field message");
    }

    // ─── Regression guard: existing blocklist behaviour must be preserved ────

    /// <summary>
    /// Settings sections that manage their own secret fields (e.g. Telegram's
    /// encrypted bot token) are blocklisted from the generic per-key endpoint —
    /// they have dedicated admin controllers that handle masking/encryption.
    /// The blocklist must still return 404, even for a farm_admin, so a
    /// well-intentioned refactor of the auth attributes doesn't accidentally
    /// open a secret-mutation hole.
    /// </summary>
    [Fact]
    public async Task Post_AsAdminToBlocklistedKey_Returns404NotFound()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();

        HttpResponseMessage resp = await admin.PostAsJsonAsync(
            $"/api/settings/{TelegramSettings.SectionName}",
            new { EnableTelegram = true });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
