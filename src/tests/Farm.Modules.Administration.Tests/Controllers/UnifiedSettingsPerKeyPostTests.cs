using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

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
public class UnifiedSettingsPerKeyPostTests : IClassFixture<UnifiedSettingsPerKeyPostTests.Factory>, IAsyncLifetime
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

    public UnifiedSettingsPerKeyPostTests(Factory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
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

        HttpResponseMessage saveResp = await PostWithCurrentRevisionAsync(admin,
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

    [Fact]
    public async Task Post_AfterHeartbeat_PreservesTokenAndLiveTelemetry()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        using HttpClient anonymous = _factory.CreateClient();
        const string endpoint = "/api/settings/NetworkDiscovery";
        JsonObject draft = (await admin.GetFromJsonAsync<JsonObject>(endpoint))!;
        string token = draft["rowVersion"]!.GetValue<string>();
        draft["clientTimeoutMs"] = 700;

        for (int i = 0; i < 2; i++)
        {
            (await anonymous.PostAsync($"{endpoint}/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            JsonObject heartbeatRead = (await anonymous.GetFromJsonAsync<JsonObject>(endpoint))!;
            heartbeatRead["rowVersion"]!.GetValue<string>().Should().Be(token);
            heartbeatRead["lastHeartbeat"].Should().NotBeNull();
        }

        JsonObject beforeSave = (await anonymous.GetFromJsonAsync<JsonObject>(endpoint))!;
        HttpResponseMessage saved = await admin.PostAsJsonAsync(endpoint, draft);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonObject savedBody = (await saved.Content.ReadFromJsonAsync<JsonObject>())!;
        savedBody["lastHeartbeat"]!.GetValue<string>().Should().Be(beforeSave["lastHeartbeat"]!.GetValue<string>());
        JsonObject afterSave = (await anonymous.GetFromJsonAsync<JsonObject>(endpoint))!;
        afterSave["clientTimeoutMs"]!.GetValue<int>().Should().Be(700);
        afterSave["lastHeartbeat"]!.GetValue<string>().Should().Be(beforeSave["lastHeartbeat"]!.GetValue<string>());
        afterSave["rowVersion"]!.GetValue<string>().Should().NotBe(token);

        // The same protection applies once the editable section already has a persisted revision.
        token = afterSave["rowVersion"]!.GetValue<string>();
        (await anonymous.PostAsync($"{endpoint}/heartbeat", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        JsonObject heartbeatAfterSave = (await anonymous.GetFromJsonAsync<JsonObject>(endpoint))!;
        heartbeatAfterSave["rowVersion"]!.GetValue<string>().Should().Be(token);
        (await admin.PostAsJsonAsync(endpoint, afterSave)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_UpdateChannel_RoundTripsStableInsiderStableAndRejectsUnacknowledgedInsider()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        string endpoint = $"/api/settings/{UpdateChannelSettings.SectionName}";

        (await PostWithCurrentRevisionAsync(admin,
            endpoint,
            new UpdateChannelSettings { Channel = "stable", InsiderAcknowledged = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWithCurrentRevisionAsync(admin,
            endpoint,
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        UpdateChannelSettings? insider = await admin.GetFromJsonAsync<UpdateChannelSettings>(
            endpoint,
            JsonOptions);
        insider.Should().BeEquivalentTo(
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true });

        (await PostWithCurrentRevisionAsync(admin,
            endpoint,
            new UpdateChannelSettings { Channel = "stable", InsiderAcknowledged = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage rejected = await PostWithCurrentRevisionAsync(admin,
            endpoint,
            new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = false });

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        UpdateChannelSettings? retained = await admin.GetFromJsonAsync<UpdateChannelSettings>(
            endpoint,
            JsonOptions);
        retained.Should().BeEquivalentTo(
            new UpdateChannelSettings { Channel = "stable", InsiderAcknowledged = false });
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

        HttpResponseMessage resp = await PostWithCurrentRevisionAsync(admin,
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The body must have both `message` (string) and a populated `errors`
        // dictionary. The SettingsPage error parser (see SettingsPage.tsx:120-141)
        // requires exactly this shape: `errors` as a map, `message` as a string.
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("message", out JsonElement messageProp).Should().BeTrue();
        messageProp.ValueKind.Should().Be(JsonValueKind.String);
        // The top-level `message` must carry the concrete validation reason so the React
        // SettingsPage save-error banner tells the user what actually went wrong. Before the
        // #941 regate fix it was a generic "Validation failed for section 'X'" and the real
        // reason was buried under `errors[sectionKey]` — which no field renders because no
        // rendered property is ever named the same as its section key.
        messageProp.GetString().Should().Contain("Invalid CIDR");
        messageProp.GetString().Should().Contain("not-a-cidr");

        body.TryGetProperty("errors", out JsonElement errorsProp).Should().BeTrue();
        errorsProp.ValueKind.Should().Be(JsonValueKind.Object);
        errorsProp.EnumerateObject().Should().NotBeEmpty(
            "the errors dictionary must contain at least one entry so the UI can render a per-field message");
    }

    /// <summary>
    /// Regression guard for the #941 regate finding: a memberless
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/> — the shape used by 21 of the 23 <c>Validate()</c>
    /// implementations across the settings classes — must surface its <see cref="Exception.Message"/>
    /// in the top-level <c>message</c> field of the response. The React SettingsPage save-error
    /// banner renders that field verbatim (<c>firstMessage ?? summary</c>). If the message is a
    /// generic "Validation failed for section 'X'" and the concrete reason lives only under
    /// <c>errors[sectionKey]</c>, the user is never told what to fix — <c>errors[sectionKey]</c>
    /// is looked up against <c>prop.name</c>, and no rendered property is ever named the same
    /// as its section key.
    /// </summary>
    [Fact]
    public async Task Post_MemberlessValidationException_SurfacesRealReasonInTopLevelMessage()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();

        // NetworkDiscoverySettings.Validate() throws `new ValidationException($"Invalid CIDR subnet: {subnet}")`
        // for a bad CIDR. That throw has no MemberNames — it is exactly the memberless shape
        // the frontend was dropping on the floor.
        NetworkDiscoverySettings payload = new()
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "10.0.0.0/foo" },
        };

        HttpResponseMessage resp = await PostWithCurrentRevisionAsync(admin,
            $"/api/settings/{NetworkDiscoverySettings.SectionName}",
            payload);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("message", out JsonElement messageProp).Should().BeTrue();
        messageProp.ValueKind.Should().Be(JsonValueKind.String);
        string? message = messageProp.GetString();
        message.Should().NotBeNull();
        message.Should().NotStartWith(
            "Validation failed for section",
            "the top-level message must be the concrete reason, not a generic section-scoped placeholder — the placeholder is what the frontend was rendering when the real reason was invisible");
        message.Should().Contain(
            "Invalid CIDR subnet",
            "the concrete reason from the settings class's validator must reach the user via the top-level message");
        message.Should().Contain(
            "10.0.0.0/foo",
            "the offending value should appear in the surfaced message so the user can identify which entry to correct");
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

    [Fact]
    public async Task Post_MissingRowVersion_Returns428()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        using HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/settings/UpdateChannel", new UpdateChannelSettings());
        ((int)response.StatusCode).Should().Be(428);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("not-base64")]
    [InlineData("AQ==")]
    public async Task Post_InvalidRowVersion_Returns400(string rowVersion)
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        using HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/settings/UpdateChannel", new { channel = "stable", rowVersion });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_TwoTabsWithSameRevision_RejectsStaleSaveAndAllowsExplicitReload()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        const string endpoint = "/api/settings/UpdateChannel";
        JsonObject original = (await admin.GetFromJsonAsync<JsonObject>(endpoint))!;
        string originalRevision = original["rowVersion"]!.GetValue<string>();
        original["channel"] = "insider";
        original["insiderAcknowledged"] = true;

        using HttpResponseMessage first = await admin.PostAsJsonAsync(endpoint, original);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonObject saved = (await first.Content.ReadFromJsonAsync<JsonObject>())!;
        saved["rowVersion"]!.GetValue<string>().Should().NotBe(originalRevision);
        first.Headers.ETag!.Tag.Should().Be($"\"{saved["rowVersion"]!.GetValue<string>()}\"");

        original["channel"] = "stable";
        original["insiderAcknowledged"] = false;
        using HttpResponseMessage stale = await admin.PostAsJsonAsync(endpoint, original);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonObject error = (await stale.Content.ReadFromJsonAsync<JsonObject>())!;
        error["message"]!.GetValue<string>().Should().Contain("Reload");
        error["errors"]!["UpdateChannel"]!.GetValue<string>().Should().Contain("modified");

        JsonObject all = (await admin.GetFromJsonAsync<JsonObject>("/api/settings"))!;
        all["UpdateChannel"]!["rowVersion"]!.GetValue<string>()
            .Should().Be(saved["rowVersion"]!.GetValue<string>());
        all["UpdateChannel"]!["channel"]!.GetValue<string>().Should().Be("insider");
        using HttpResponseMessage reloaded = await PostWithCurrentRevisionAsync(
            admin, endpoint, new UpdateChannelSettings { Channel = "stable" });
        reloaded.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> PostWithCurrentRevisionAsync<T>(
        HttpClient client, string endpoint, T values)
    {
        JsonObject current = (await client.GetFromJsonAsync<JsonObject>(endpoint))!;
        JsonObject payload = JsonSerializer.SerializeToNode(values)!.AsObject();
        payload["rowVersion"] = current["rowVersion"]!.GetValue<string>();
        return await client.PostAsJsonAsync(endpoint, payload);
    }
}
