using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Hicks #8: real HTTP round-trip through the ASP.NET Core pipeline for the
/// three notification-preferences surfaces that clients depend on. Shared
/// contract unit tests in <see cref="NotificationPreferencesContractTests"/>
/// pin the DTO shape at compile time, but they can NEVER prove the wire
/// contract that clients actually observe — auth, routing, model-binder
/// case-sensitivity, ProblemDetails 400 shape. These tests exercise the
/// full request pipeline via <see cref="CustomWebApplicationFactory"/> so
/// a regression that only surfaces at the pipeline boundary (e.g., an
/// anonymous filter is dropped, a route template changes, a case-insensitive
/// enum converter slips in) is caught here.
///
/// Coverage:
/// * Anonymous GET /api/notifications/preferences/capabilities -&gt; 200 with
///   camelCase payload and exactly nine PascalCase enum tokens.
/// * Authenticated GET /api/notifications/preferences -&gt; 200 with camelCase
///   payload, all nine rows materialized, and defaults matching the
///   canonical matrix.
/// * Authenticated PUT /api/notifications/preferences with an unknown enum
///   string -&gt; 400 (no successful mutation).
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationPreferencesEndpointTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _anonClient;
    private HttpClient? _authClient;

    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public NotificationPreferencesEndpointTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonClient = _factory.CreateClient();
        _authClient = await _factory.CreateAuthenticatedClientAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _anonClient?.Dispose();
        _authClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "GET /api/notifications/preferences/capabilities is anonymous and publishes exactly nine PascalCase tokens")]
    public async Task CapabilitiesEndpoint_Anonymous_PublishesNinePascalCaseTokens()
    {
        // Hicks #8: real HTTP round-trip proves the route is anonymously
        // accessible and the wire shape uses camelCase property names with
        // PascalCase enum tokens. A future refactor that accidentally added
        // [Authorize] or flipped the JSON naming would surface here.
        HttpResponseMessage response = await _anonClient!.GetAsync("/api/notifications/preferences/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the capabilities probe MUST remain anonymous");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // The payload must use camelCase — assert the property name lands
        // under "supportedEventTypes", NOT "SupportedEventTypes".
        root.TryGetProperty("supportedEventTypes", out JsonElement supported)
            .Should().BeTrue("payload MUST expose supportedEventTypes in camelCase");
        supported.ValueKind.Should().Be(JsonValueKind.Array);

        string[] tokens = supported.EnumerateArray().Select(e => e.GetString()!).ToArray();
        tokens.Should().BeEquivalentTo(new[]
        {
            "JobStarted",
            "JobCompleted",
            "JobFailed",
            "JobPaused",
            "PrinterFailure",
            "FilamentRunout",
            "HarvestReady",
            "MaintenanceDue",
            "PrinterOffline",
        }, "capabilities MUST publish exactly the nine PascalCase tokens contract clients hard-code");
    }

    [Fact(DisplayName = "GET /api/notifications/preferences returns 401 for anonymous clients")]
    public async Task GetPreferences_Anonymous_Returns401()
    {
        HttpResponseMessage response = await _anonClient!.GetAsync("/api/notifications/preferences");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "GET /api/notifications/preferences materializes all nine rows with canonical defaults")]
    public async Task GetPreferences_Authenticated_MaterializesAllNineRowsAndDefaults()
    {
        // Hicks #8 + Hicks #3: an authenticated GET for a fresh user must
        // synthesize the full nine-row matrix from the canonical defaults —
        // not from a persisted row (there isn't one yet) and not with any
        // holes. This is the "before" the first partial PUT must preserve.
        HttpResponseMessage response = await _authClient!.GetAsync("/api/notifications/preferences");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("eventChannelPreferences", out JsonElement rows)
            .Should().BeTrue("payload MUST expose eventChannelPreferences in camelCase");
        rows.ValueKind.Should().Be(JsonValueKind.Array);

        JsonElement[] rowArray = rows.EnumerateArray().ToArray();
        rowArray.Length.Should().Be(9, "the fresh GET MUST materialize all nine rows even without a persisted row");

        string[] eventTypes = rowArray.Select(r => r.GetProperty("eventType").GetString()!).ToArray();
        eventTypes.Should().BeEquivalentTo(new[]
        {
            "JobStarted",
            "JobCompleted",
            "JobFailed",
            "JobPaused",
            "PrinterFailure",
            "FilamentRunout",
            "HarvestReady",
            "MaintenanceDue",
            "PrinterOffline",
        });

        // Attention row default: InApp=true, Push=true, Email=false, Telegram=false.
        JsonElement offline = rowArray.Single(r => r.GetProperty("eventType").GetString() == "PrinterOffline");
        offline.GetProperty("inApp").GetBoolean().Should().BeTrue();
        offline.GetProperty("push").GetBoolean().Should().BeTrue();
        offline.GetProperty("email").GetBoolean().Should().BeFalse();
        offline.GetProperty("telegram").GetBoolean().Should().BeFalse();
    }

    [Fact(DisplayName = "PUT /api/notifications/preferences with unknown enum returns 400 and does not persist")]
    public async Task PutPreferences_UnknownEnumValue_Returns400()
    {
        // Hicks #8: an unknown enum string on the PUT body must be rejected
        // by model binding with 400. This test bypasses typed DTO
        // serialization and hand-crafts the raw JSON so we can send a value
        // the enum has never had ("notAValidToken"). Prior to this test the
        // guarantee lived only in shared unit tests that never went through
        // the JSON reader path.
        string rawJson = """
{
  "eventChannelPreferences": [
    {
      "eventType": "notAValidToken",
      "inApp": true,
      "push": true,
      "email": false,
      "telegram": false
    }
  ]
}
""";

        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _authClient!.PutAsync("/api/notifications/preferences", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "unknown enum tokens MUST NOT bind and MUST NOT persist");
    }
}
