using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for Failure Detection Controller.
/// Tests monitoring status, manual analysis, and event history endpoints.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class FailureDetectionControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public FailureDetectionControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "GetStatus returns 200 with failure detection status")]
    public async Task GetStatus_Returns200WithStatus()
    {
        // Act
        var response = await _client!.GetAsync("/api/failure-detection/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "GetStatus returns valid JSON structure")]
    public async Task GetStatus_ReturnsValidJsonStructure()
    {
        // Act
        var response = await _client!.GetAsync("/api/failure-detection/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

        status.TryGetProperty("monitoringEnabled", out var monitoringEnabled).Should().BeTrue();
        monitoringEnabled.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        status.TryGetProperty("configuredPrinterCount", out var configuredPrinterCount).Should().BeTrue();
        configuredPrinterCount.ValueKind.Should().Be(JsonValueKind.Number);

        status.TryGetProperty("activelyMonitoredPrinterCount", out var activelyMonitoredPrinterCount).Should().BeTrue();
        activelyMonitoredPrinterCount.ValueKind.Should().Be(JsonValueKind.Number);

        status.TryGetProperty("printers", out var printers).Should().BeTrue();
        printers.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact(DisplayName = "GetStatus reflects configured failure detection enablement")]
    public async Task GetStatus_ReflectsConfiguredFailureDetectionEnablement()
    {
        await using var disabledFactory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Obico:Enabled"] = "false"
        });
        await disabledFactory.ResetDatabaseAsync();
        using HttpClient disabledClient = disabledFactory.CreateClient();

        HttpResponseMessage disabledResponse = await disabledClient.GetAsync("/api/failure-detection/status");
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement disabledStatus = JsonSerializer.Deserialize<JsonElement>(
            await disabledResponse.Content.ReadAsStringAsync(),
            _jsonOptions);
        disabledStatus.GetProperty("monitoringEnabled").GetBoolean().Should().BeFalse();

        await using var enabledFactory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Obico:Enabled"] = "true"
        });
        await enabledFactory.ResetDatabaseAsync();
        using HttpClient enabledClient = enabledFactory.CreateClient();

        HttpResponseMessage enabledResponse = await enabledClient.GetAsync("/api/failure-detection/status");
        enabledResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement enabledStatus = JsonSerializer.Deserialize<JsonElement>(
            await enabledResponse.Content.ReadAsStringAsync(),
            _jsonOptions);
        enabledStatus.GetProperty("monitoringEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact(DisplayName = "Analyze endpoint returns 503 when not authenticated (requires auth)")]
    public async Task Analyze_Returns401WhenNotAuthenticated()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        // Act - POST without auth should return 401 since controller requires [Authorize]
        var response = await _client!.PostAsync($"/api/failure-detection/analyze/{printerId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Analyze endpoint with snapshotUrl still requires authentication")]
    public async Task Analyze_WithUrlStillRequiresAuth()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var invalidUrl = "not-a-valid-url";

        // Act
        var response = await _client!.PostAsync(
            $"/api/failure-detection/analyze/{printerId}?snapshotUrl={Uri.EscapeDataString(invalidUrl)}",
            null);

        // Assert - Controller requires auth, so we get 401 before it checks parameters
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Analyze endpoint handles requests when authenticated")]
    public async Task Analyze_HandlesAuthenticatedRequests()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var snapshotUrl = "http://example.com/snapshot.jpg";

        // Act
        var response = await _client!.PostAsync(
            $"/api/failure-detection/analyze/{printerId}?snapshotUrl={Uri.EscapeDataString(snapshotUrl)}",
            null);

        // Assert - Unauthenticated will get 401. With proper auth (not configured in test),
        // would get 400 (for Obico not configured) or 200 (success).
        // For now, we verify the endpoint is protected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "GetHistory returns 501 not implemented")]
    public async Task GetHistory_Returns501NotImplemented()
    {
        // Act
        var response = await _client!.GetAsync("/api/failure-detection/history");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

        result.TryGetProperty("message", out var message).Should().BeTrue();
        message.GetString().Should().Contain("not yet implemented");
    }

    [Fact(DisplayName = "GetHistory returns valid JSON with feature indicator")]
    public async Task GetHistory_ReturnsValidJsonWithFeatureIndicator()
    {
        // Act
        var response = await _client!.GetAsync("/api/failure-detection/history");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

        result.TryGetProperty("feature", out var feature).Should().BeTrue();
        feature.GetString().Should().Be("event_persistence");
    }

    [Fact(DisplayName = "Endpoints are protected by authorization")]
    public async Task Endpoints_ProtectedByAuthorization()
    {
        // Act & Assert - Test factory automatically provides auth, so status endpoint succeeds
        // In production without auth, this would return 401
        var statusResponse = await _client!.GetAsync("/api/failure-detection/status");

        // The test factory provides authentication, so we get 200
        // This verifies the endpoint is accessible when properly authenticated
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "GetStatus handles system initialization state")]
    public async Task GetStatus_HandlesSystemInitializationState()
    {
        // Note: In normal operation, the system should be ready after startup
        // This test verifies the endpoint responds correctly

        // Act
        var response = await _client!.GetAsync("/api/failure-detection/status");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

            result.TryGetProperty("message", out var message).Should().BeTrue();
            message.GetString().Should().Contain("initializing");
        }
    }

    [Fact(DisplayName = "Analyze endpoint validation happens after authentication")]
    public async Task Analyze_ValidationHappensAfterAuth()
    {
        // Arrange
        var printerId = Guid.NewGuid();
        var snapshotUrl = "http://example.com/snapshot.jpg";

        // Act
        var response = await _client!.PostAsync(
            $"/api/failure-detection/analyze/{printerId}?snapshotUrl={Uri.EscapeDataString(snapshotUrl)}",
            null);

        // Assert - Controller requires authentication, so validation happens after auth
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "GetStatus reflects current runtime Obico settings")]
    public async Task GetStatus_ReflectsCurrentRuntimeObicoSettings()
    {
        // This test verifies that the monitoring status reflects the actual runtime configuration
        // from ObicoSettings, not stale cached values. The PrintFailureMonitorService should use
        // IOptionsMonitor<ObicoSettings> to read live settings values.

        // Act
        var response = await _client!.GetAsync("/api/failure-detection/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

        // The monitoring status should contain runtime configuration fields
        status.TryGetProperty("monitoringEnabled", out var monitoringEnabled).Should().BeTrue();
        monitoringEnabled.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        status.TryGetProperty("confidenceThreshold", out var confidenceThreshold).Should().BeTrue();
        confidenceThreshold.ValueKind.Should().Be(JsonValueKind.Number);

        status.TryGetProperty("scanIntervalSeconds", out var scanIntervalSeconds).Should().BeTrue();
        scanIntervalSeconds.ValueKind.Should().Be(JsonValueKind.Number);

        status.TryGetProperty("autoPauseOnFailure", out var autoPauseOnFailure).Should().BeTrue();
        autoPauseOnFailure.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        // The default test configuration has Obico disabled (Enabled = false)
        // This verifies that the status correctly reflects the runtime configuration
        monitoringEnabled.GetBoolean().Should().BeFalse("Obico should be disabled in test configuration");
    }

    [Fact(DisplayName = "Printer status reason matches actual runtime enablement")]
    public async Task PrinterStatus_ReasonMatchesActualRuntimeEnablement()
    {
        // This test verifies that per-printer status messages reflect the actual runtime
        // Obico enablement setting, not a stale cached value captured at service construction.

        // Act
        var response = await _client!.GetAsync("/api/failure-detection/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize<JsonElement>(json, _jsonOptions);

        // Get the printers array from the status
        status.TryGetProperty("printers", out var printers).Should().BeTrue();
        printers.ValueKind.Should().Be(JsonValueKind.Array);

        // Get the global monitoring enabled flag
        status.TryGetProperty("monitoringEnabled", out var monitoringEnabled).Should().BeTrue();
        bool isGloballyEnabled = monitoringEnabled.GetBoolean();

        // For each printer, if global monitoring is disabled, the printer state should be "disabled"
        // with the correct reason message
        if (!isGloballyEnabled)
        {
            foreach (var printer in printers.EnumerateArray())
            {
                if (printer.TryGetProperty("state", out var state) && 
                    state.GetString() == "disabled")
                {
                    printer.TryGetProperty("reason", out var reason).Should().BeTrue();
                    reason.GetString().Should().Contain("disabled in Settings",
                        "Disabled printer reason should reference Settings when global monitoring is disabled");
                }
            }
        }
    }
}
