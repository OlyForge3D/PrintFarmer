using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Integration tests for the DispatchSettingsController.
/// Uses CustomWebApplicationFactory with full DI pipeline to test
/// the singleton DispatchSettings entity CRUD via HTTP.
///
/// Tests verify:
/// - GET returns seeded default settings
/// - PUT with valid input updates and returns new values
/// - PUT with invalid input returns 400
/// - Enum serialization as strings (AutoDispatchMode)
/// - Singleton constraint (only one row)
/// </summary>
public class DispatchSettingsControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    // API serializes enums as strings via JsonStringEnumConverter
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DispatchSettingsControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    // =========================================================================
    // GET /api/dispatch-settings
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task GetSettings_ReturnsCurrentSettings()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/dispatch-settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        DispatchSettingsDto? settings = await response.Content.ReadFromJsonAsync<DispatchSettingsDto>(JsonOptions);
        settings.Should().NotBeNull();

        // Verify seeded defaults from DispatchSettingsConfiguration
        settings!.AutoDispatchEnabled.Should().BeFalse("default is opt-in disabled");
        settings.IdleThresholdSeconds.Should().Be(30, "default idle threshold is 30s");
        settings.MaxConcurrentDispatches.Should().Be(3, "default max concurrent is 3");
        settings.MinimumScoreThreshold.Should().Be(0.5, "default score threshold is 0.5");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task GetSettings_ReturnsAutoDispatchModeAsString()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/dispatch-settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read raw JSON to verify enum serialization
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"autoDispatchMode\"", "property should be camelCase");

        // The mode value should be a string, not a number
        // Default mode is "Manual" (enum value 0)
        json.Should().Contain("\"Manual\"", "AutoDispatchMode should serialize as string per JsonStringEnumConverter");
    }

    // =========================================================================
    // PUT /api/dispatch-settings
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_ValidInput_UpdatesAndReturns()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 60,
            MinimumScoreThreshold = 75.0,
            MaxConcurrentDispatches = 5,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        DispatchSettingsDto? result = await response.Content.ReadFromJsonAsync<DispatchSettingsDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.AutoDispatchEnabled.Should().BeTrue();
        result.AutoDispatchMode.Should().Be(AutoDispatchMode.Auto);
        result.IdleThresholdSeconds.Should().Be(60);
        result.MinimumScoreThreshold.Should().Be(75.0);
        result.MaxConcurrentDispatches.Should().Be(5);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_SuggestMode_SerializesCorrectly()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Suggest,
            IdleThresholdSeconds = 15,
            MinimumScoreThreshold = 50.0,
            MaxConcurrentDispatches = 2,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"Suggest\"", "Suggest mode should serialize as string");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_NegativeIdleThreshold_Returns400()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = -5,
            MinimumScoreThreshold = 50.0,
            MaxConcurrentDispatches = 3,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_ScoreAbove100_Returns400()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = 150.0,
            MaxConcurrentDispatches = 3,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_NegativeScore_Returns400()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = -10.0,
            MaxConcurrentDispatches = 3,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_MaxConcurrentZero_Returns400()
    {
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = 50.0,
            MaxConcurrentDispatches = 0,
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync("/api/dispatch-settings", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_Roundtrip_GetReflectsUpdate()
    {
        // First update
        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Suggest,
            IdleThresholdSeconds = 45,
            MinimumScoreThreshold = 60.0,
            MaxConcurrentDispatches = 2,
        };

        HttpResponseMessage putResponse = await _client.PutAsJsonAsync("/api/dispatch-settings", request);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Then read back
        HttpResponseMessage getResponse = await _client.GetAsync("/api/dispatch-settings");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        DispatchSettingsDto? result = await getResponse.Content.ReadFromJsonAsync<DispatchSettingsDto>(JsonOptions);
        result.Should().NotBeNull();
        result!.AutoDispatchEnabled.Should().BeTrue();
        result.AutoDispatchMode.Should().Be(AutoDispatchMode.Suggest);
        result.IdleThresholdSeconds.Should().Be(45);
        result.MinimumScoreThreshold.Should().Be(60.0);
        result.MaxConcurrentDispatches.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task UpdateSettings_UpdatedAtChanges()
    {
        // Read current
        HttpResponseMessage getResponse1 = await _client.GetAsync("/api/dispatch-settings");
        DispatchSettingsDto? before = await getResponse1.Content.ReadFromJsonAsync<DispatchSettingsDto>(JsonOptions);

        // Wait a moment then update
        await Task.Delay(50);

        var request = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 10,
            MinimumScoreThreshold = 80.0,
            MaxConcurrentDispatches = 1,
        };
        HttpResponseMessage putResponse = await _client.PutAsJsonAsync("/api/dispatch-settings", request);
        DispatchSettingsDto? after = await putResponse.Content.ReadFromJsonAsync<DispatchSettingsDto>(JsonOptions);

        after!.UpdatedAt.Should().BeAfter(before!.UpdatedAt, "UpdatedAt should advance on each update");
    }

    // =========================================================================
    // SINGLETON CONSTRAINT TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task DispatchSettings_DatabaseHasExactlyOneRow()
    {
        // Verify the singleton constraint via the factory's service scope
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<DispatchSettings> all = await db.DispatchSettings.ToListAsync();
        all.Should().HaveCount(1, "DispatchSettings is a singleton — exactly one row should exist");
        all[0].Id.Should().Be(1, "singleton row uses Id=1 by convention");
    }
}
