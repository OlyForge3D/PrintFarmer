using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// authorization metadata changes, a route template changes, a case-insensitive
/// enum converter slips in) is caught here.
///
/// Coverage:
/// * Authenticated GET /api/notifications/preferences/capabilities -&gt; 200 with
///   camelCase payload and exactly nine PascalCase enum tokens.
/// * Authenticated GET /api/notifications/preferences -&gt; 200 with camelCase
///   payload, all nine rows materialized, and defaults matching the
///   canonical matrix.
/// * Authenticated PUT /api/notifications/preferences with an unknown enum
///   string -&gt; 400 (no successful mutation).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationPreferencesEndpointTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _anonClient;
    private HttpClient? _authClient;

    public NotificationPreferencesEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _anonClient = _factory.CreateClient();
        _authClient = await _factory.CreateAuthenticatedClientAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _anonClient?.Dispose();
        _authClient?.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Authenticated GET /api/notifications/preferences/capabilities publishes exactly nine PascalCase tokens")]
    public async Task CapabilitiesEndpoint_Authenticated_PublishesNinePascalCaseTokens()
    {
        // Hicks #8: real HTTP round-trip proves the route is authenticated
        // and the wire shape uses camelCase property names with
        // PascalCase enum tokens. A future JSON naming regression surfaces here.
        HttpResponseMessage response = await _authClient!.GetAsync("/api/notifications/preferences/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // The payload must use camelCase — assert the property name lands
        // under "supportedEventTypes", NOT "SupportedEventTypes".
        root.TryGetProperty("supportedEventTypes", out JsonElement supported)
            .Should().BeTrue("payload MUST expose supportedEventTypes in camelCase");
        supported.ValueKind.Should().Be(JsonValueKind.Array);

        string[] tokens = supported.EnumerateArray().Select(e => e.GetString()!).ToArray();
        tokens.Should().Equal(new[]
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

    [Fact(DisplayName = "GET /api/notifications/preferences pins exact ordered nine-row raw response and defaults")]
    public async Task GetPreferences_Authenticated_MaterializesAllNineRowsAndDefaults()
    {
        // Hicks post-merge #3: real HTTP round-trip must pin the exact
        // canonical row order and every per-row default so a silent change
        // to NotificationPreferencesDefaults or the serializer is caught.
        // Earlier revisions used BeEquivalentTo (unordered) which allowed
        // wire reordering. This assertion walks the raw array positionally.
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

        // Canonical row-order + per-row default matrix. Any drift in
        // NotificationPreferencesDefaults, serializer configuration, or
        // controller row emission will fail this assertion.
        (string EventType, bool InApp, bool Email, bool Push, bool Telegram)[] expected = new (string, bool, bool, bool, bool)[]
        {
            ("JobStarted",     false, false, false, false),
            ("JobCompleted",   true,  false, true,  false),
            ("JobFailed",      true,  false, true,  false),
            ("JobPaused",      true,  false, true,  false),
            ("PrinterFailure", true,  false, true,  false),
            ("FilamentRunout", true,  false, true,  false),
            ("HarvestReady",   true,  false, true,  false),
            ("MaintenanceDue", true,  false, true,  false),
            ("PrinterOffline", true,  false, true,  false),
        };

        for (int i = 0; i < expected.Length; i++)
        {
            JsonElement row = rowArray[i];
            row.GetProperty("eventType").GetString().Should().Be(expected[i].EventType,
                $"row {i} MUST be {expected[i].EventType} in canonical order");
            row.GetProperty("inApp").GetBoolean().Should().Be(expected[i].InApp,
                $"row {i} ({expected[i].EventType}) inApp default drift");
            row.GetProperty("email").GetBoolean().Should().Be(expected[i].Email,
                $"row {i} ({expected[i].EventType}) email default drift");
            row.GetProperty("push").GetBoolean().Should().Be(expected[i].Push,
                $"row {i} ({expected[i].EventType}) push default drift");
            row.GetProperty("telegram").GetBoolean().Should().Be(expected[i].Telegram,
                $"row {i} ({expected[i].EventType}) telegram default drift");
        }
    }

    [Fact(DisplayName = "PUT /api/notifications/preferences with unknown enum returns 400 and never mutates persisted state")]
    public async Task PutPreferences_UnknownEnumValue_Returns400()
    {
        // Hicks post-merge #3: an unknown enum string on the PUT body must
        // be rejected by model binding with 400 AND must never mutate any
        // preferences row. Prior tests only checked the HTTP status; this
        // test additionally reads the persisted preferences row through a
        // fresh scope both before and after the request and asserts that
        // every serialized column is byte-identical. That catches any
        // partial-bind side-effect (e.g., a valid sibling row that got
        // written before validation rejected the unknown enum).
        string beforeSnapshot = await CapturePersistedPreferencesSnapshotAsync();

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

        string afterSnapshot = await CapturePersistedPreferencesSnapshotAsync();
        afterSnapshot.Should().Be(beforeSnapshot,
            "persisted preferences row MUST be byte-identical across the rejected PUT (no partial-bind side-effect)");
    }

    /// <summary>
    /// Reads the persisted <see cref="NotificationPreferences"/> row for the
    /// authenticated test user through a fresh, no-tracking scope and
    /// serializes it to a stable JSON string for byte-comparison. Returns
    /// the literal <c>"__NO_ROW__"</c> sentinel when no row exists yet so
    /// before/after equality still detects a spurious insert.
    /// </summary>
    private async Task<string> CapturePersistedPreferencesSnapshotAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The authenticated client seeds the "test-admin" user; capture its
        // preferences row (if any) via a no-tracking read on a fresh scope
        // so any cross-request cached state cannot mask a spurious write.
        Guid userId = await context.Users
            .AsNoTracking()
            .Where(u => u.Username == "test-admin")
            .Select(u => u.Id)
            .FirstAsync();

        NotificationPreferences? row = await context.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (row is null)
        {
            return "__NO_ROW__";
        }

        // Serialize the deterministic column projection (Id and timestamps
        // excluded so a legitimate fresh scope doesn't produce differing
        // UpdatedAt values from clock jitter — every mutation MUST change
        // one of the projected columns).
        return JsonSerializer.Serialize(new
        {
            row.UserId,
            row.InAppOnJobStarted,
            row.InAppOnJobCompleted,
            row.InAppOnJobFailed,
            row.InAppOnJobPaused,
            row.EmailOnJobStarted,
            row.EmailOnJobCompleted,
            row.EmailOnJobFailed,
            row.EmailOnJobPaused,
            row.PushOnJobStarted,
            row.PushOnJobCompleted,
            row.PushOnJobFailed,
            row.PushOnJobPaused,
            row.TelegramOnJobStarted,
            row.TelegramOnJobCompleted,
            row.TelegramOnJobFailed,
            row.TelegramOnJobPaused,
            row.InAppOnPrinterFailure,
            row.EmailOnPrinterFailure,
            row.PushOnPrinterFailure,
            row.TelegramOnPrinterFailure,
            row.InAppOnFilamentRunout,
            row.EmailOnFilamentRunout,
            row.PushOnFilamentRunout,
            row.TelegramOnFilamentRunout,
            row.InAppOnHarvestReady,
            row.EmailOnHarvestReady,
            row.PushOnHarvestReady,
            row.TelegramOnHarvestReady,
            row.InAppOnMaintenanceDue,
            row.EmailOnMaintenanceDue,
            row.PushOnMaintenanceDue,
            row.TelegramOnMaintenanceDue,
            row.InAppOnPrinterOffline,
            row.EmailOnPrinterOffline,
            row.PushOnPrinterOffline,
            row.TelegramOnPrinterOffline,
            row.EnableInAppNotifications,
            row.EnableEmailNotifications,
            row.EnablePushNotifications,
            row.EnableTelegramNotifications,
            row.NotifyOnStart,
            row.NotifyOnCompletion,
            row.NotifyOnFailure,
            row.NotifyOnPause,
            Frequency = row.Frequency.ToString(),
            row.RetentionDays,
        });
    }
}
