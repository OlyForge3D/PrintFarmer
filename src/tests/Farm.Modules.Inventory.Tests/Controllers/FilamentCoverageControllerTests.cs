using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Testing.Shared;
using Farm.Web.Api.Tests;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Farm.Modules.Inventory.Tests.Controllers;

/// <summary>
/// Integration tests for <c>/api/printers/filament-coverage</c> and
/// <c>/api/printers/{id}/filament-coverage</c> (issue #709). Focused on the
/// controller pipeline: authorization, JSON contract (camelCase + string
/// enums), and fleet performance envelope. Deep coverage math is covered by
/// <see cref="Farm.Web.Api.Tests.Services.FilamentCoverageServiceTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public class FilamentCoverageControllerTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private const string PerPrinterRoute = "/api/printers/{0}/filament-coverage";
    private const string FleetRoute = "/api/printers/filament-coverage";
    private static readonly CompositeFormat PerPrinterRouteFormat = CompositeFormat.Parse(PerPrinterRoute);

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public FilamentCoverageControllerTests(CustomWebApplicationFactory factory)
    {
        // No service overrides are needed: the coverage service degrades
        // gracefully when the (unconfigured) Spoolman client throws, and no
        // live-progress lookup is issued when there is no active job.
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Auth
    //
    // Coverage endpoints are protected with a plain [Authorize] attribute so
    // they follow the AuthorizationFallbackPolicy — we spin up a dedicated
    // factory with Security:DevModeBypassAuth=false to prove that anonymous
    // callers are rejected. The default factory bypasses auth in dev mode.
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetFleet_WithoutAuth_Returns401()
    {
        await using CustomWebApplicationFactory strictFactory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await strictFactory.ResetDataAsync();
        using HttpClient anon = strictFactory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync(FleetRoute);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetForPrinter_WithoutAuth_Returns401()
    {
        await using CustomWebApplicationFactory strictFactory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await strictFactory.ResetDataAsync();
        using HttpClient anon = strictFactory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync(string.Format(CultureInfo.InvariantCulture, PerPrinterRouteFormat, Guid.NewGuid()));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------
    // 404 for unknown printer
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetForPrinter_UnknownId_Returns404()
    {
        HttpResponseMessage response = await _client!.GetAsync(string.Format(CultureInfo.InvariantCulture, PerPrinterRouteFormat, Guid.NewGuid()));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // Serialization contract — camelCase properties & string enums
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetForPrinter_ReturnsCamelCaseJson_WithStringEnumStatus()
    {
        Printer printer = await SeedPrinterWithToolheadAsync();

        HttpResponseMessage response = await _client!.GetAsync(string.Format(CultureInfo.InvariantCulture, PerPrinterRouteFormat, printer.Id));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // camelCase top-level properties
        root.TryGetProperty("printerId", out _).Should().BeTrue("responses must be camelCase");
        root.TryGetProperty("printerName", out _).Should().BeTrue();
        root.TryGetProperty("toolheads", out JsonElement toolheads).Should().BeTrue();
        root.TryGetProperty("evaluatedAtUtc", out _).Should().BeTrue();
        root.TryGetProperty("assignedQueuedJobCount", out _).Should().BeTrue();
        root.TryGetProperty("status", out JsonElement status).Should().BeTrue();

        // No PascalCase leakage
        root.TryGetProperty("PrinterId", out _).Should().BeFalse();
        root.TryGetProperty("Status", out _).Should().BeFalse();

        // Enum serialized as string, not integer
        status.ValueKind.Should().Be(JsonValueKind.String, "FilamentCoverageStatus must serialize as a string");
        status.GetString().Should().BeOneOf("covers", "runout", "unknown");

        // camelCase nested toolhead properties
        JsonElement slot = toolheads.EnumerateArray().First();
        slot.TryGetProperty("toolheadIndex", out _).Should().BeTrue();
        slot.TryGetProperty("toolheadName", out _).Should().BeTrue();
        slot.TryGetProperty("status", out JsonElement slotStatus).Should().BeTrue();
        slotStatus.ValueKind.Should().Be(JsonValueKind.String);
        slotStatus.GetString().Should().BeOneOf("covers", "runout", "unknown");
    }

    [Theory]
    [InlineData(FilamentCoverageStatus.Unknown, "\"unknown\"")]
    [InlineData(FilamentCoverageStatus.Covers, "\"covers\"")]
    [InlineData(FilamentCoverageStatus.Runout, "\"runout\"")]
    public void FilamentCoverageStatus_SerializesCanonicalLowercase(
        FilamentCoverageStatus status,
        string expectedJson)
    {
        JsonSerializer.Serialize(status).Should().Be(expectedJson);
    }

    [Fact]
    public void FilamentCoverageStatus_IntegerJson_IsRejected()
    {
        Action deserialize = () => JsonSerializer.Deserialize<FilamentCoverageStatus>("1");
        deserialize.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task GetFleet_FeatureDisabled_Returns404ProblemDetails_WithoutCoverageWork()
    {
        await using CustomWebApplicationFactory disabledFactory = new(
            new Dictionary<string, string?>
            {
                ["OperatorFeatures:FilamentCoverageEnabled"] = "false",
            });
        Mock<IFilamentCoverageService> coverage = new(MockBehavior.Strict);
        await using var host = disabledFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFilamentCoverageService>();
                services.AddSingleton(coverage.Object);
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client!.DefaultRequestHeaders.Authorization;

        HttpResponseMessage response = await client.GetAsync(FleetRoute);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("featureDisabled");
        doc.RootElement.GetProperty("feature").GetString().Should().Be("filamentCoverageEnabled");
        coverage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetForPrinter_FeatureDisabled_Returns404ProblemDetails_WithoutCoverageWork()
    {
        await using CustomWebApplicationFactory disabledFactory = new(
            new Dictionary<string, string?>
            {
                ["OperatorFeatures:FilamentCoverageEnabled"] = "false",
            });
        Mock<IFilamentCoverageService> coverage = new(MockBehavior.Strict);
        await using var host = disabledFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFilamentCoverageService>();
                services.AddSingleton(coverage.Object);
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client!.DefaultRequestHeaders.Authorization;

        HttpResponseMessage response = await client.GetAsync(string.Format(CultureInfo.InvariantCulture, PerPrinterRouteFormat, Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("code").GetString().Should().Be("featureDisabled");
        doc.RootElement.GetProperty("feature").GetString().Should().Be("filamentCoverageEnabled");
        coverage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetFleet_ReturnsCamelCaseJson_WithPrintersArray()
    {
        _ = await SeedPrinterWithToolheadAsync();

        HttpResponseMessage response = await _client!.GetAsync(FleetRoute);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("printers", out JsonElement printers).Should().BeTrue();
        root.TryGetProperty("evaluatedAtUtc", out _).Should().BeTrue();
        printers.ValueKind.Should().Be(JsonValueKind.Array);
        printers.GetArrayLength().Should().BeGreaterThan(0);
    }

    // ------------------------------------------------------------------
    // Large-fleet pipeline — every printer the service produces is returned
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetFleet_LargeFleet_ReturnsEveryPrinterFromService()
    {
        // Deterministic: the coverage service is mocked to return a large fleet instantly, so the
        // test proves the controller pipeline (routing, serialization) surfaces every printer the
        // service produced without any wall-clock/load sensitivity. The service's own bounded
        // parallel fan-out over many printers is covered deterministically by
        // FilamentCoverageServiceTests.FleetEndpoint_BatchesSpoolAndJobQueries_ForManyPrinters.
        const int printerCount = 24;
        DateTime evaluatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var printers = Enumerable.Range(0, printerCount)
            .Select(i => new PrinterFilamentCoverageDto(
                Guid.NewGuid(),
                $"perf-{i:D2}",
                FilamentCoverageStatus.Covers,
                Array.Empty<ToolheadCoverageDto>(),
                ActiveJobId: null,
                ActiveJobName: null,
                ActiveJobProgress: null,
                EarliestPredictedRunoutAt: null,
                AssignedQueuedJobCount: 0,
                EvaluatedAtUtc: evaluatedAt))
            .ToList();
        FleetFilamentCoverageDto fleetResult = new(printers, evaluatedAt);

        Mock<IFilamentCoverageService> coverage = new(MockBehavior.Strict);
        _ = coverage
            .Setup(s => s.GetForFleetAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(fleetResult);

        await using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFilamentCoverageService>();
                services.AddSingleton(coverage.Object);
            });
        });
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = _client!.DefaultRequestHeaders.Authorization;

        HttpResponseMessage response = await client.GetAsync(FleetRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FleetFilamentCoverageDto? fleet = await response.Content.ReadFromJsonAsync<FleetFilamentCoverageDto>();
        fleet.Should().NotBeNull();
        fleet!.Printers.Should().HaveCount(printerCount);
        fleet.Printers.Select(p => p.PrinterName)
            .Should().BeEquivalentTo(printers.Select(p => p.PrinterName));
        coverage.Verify(s => s.GetForFleetAsync(It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<Printer> SeedPrinterWithToolheadAsync(string? name = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (Guid manufacturerId, Guid modelId) = await AppDbTestHelpers.GetUnknownCatalogIdsAsync(ctx);

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"cov-{Guid.NewGuid():N}".Substring(0, 20),
            ServerUrl = $"http://cov-{Guid.NewGuid()}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };
        ctx.Printers.Add(printer);

        Toolhead th = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Extruder 1",
            IsPrimary = true,
            CurrentSpoolId = 42,
            CurrentMaterial = "PLA"
        };
        ctx.Toolheads.Add(th);

        await ctx.SaveChangesAsync();
        return printer;
    }
}
