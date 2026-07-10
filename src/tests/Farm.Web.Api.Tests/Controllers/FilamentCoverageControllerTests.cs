using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for <c>/api/printers/filament-coverage</c> and
/// <c>/api/printers/{id}/filament-coverage</c> (issue #709). Focused on the
/// controller pipeline: authorization, JSON contract (camelCase + string
/// enums), and fleet performance envelope. Deep coverage math is covered by
/// <see cref="Services.FilamentCoverageServiceTests"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class FilamentCoverageControllerTests : IAsyncLifetime
{
    private const string PerPrinterRoute = "/api/printers/{0}/filament-coverage";
    private const string FleetRoute = "/api/printers/filament-coverage";

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public FilamentCoverageControllerTests()
    {
        // No service overrides are needed: the coverage service degrades
        // gracefully when the (unconfigured) Spoolman client throws, and no
        // live-progress lookup is issued when there is no active job.
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
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
        await strictFactory.ResetDatabaseAsync();
        using HttpClient anon = strictFactory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync(FleetRoute);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetForPrinter_WithoutAuth_Returns401()
    {
        await using CustomWebApplicationFactory strictFactory = new(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
        await strictFactory.ResetDatabaseAsync();
        using HttpClient anon = strictFactory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync(string.Format(PerPrinterRoute, Guid.NewGuid()));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------
    // 404 for unknown printer
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetForPrinter_UnknownId_Returns404()
    {
        HttpResponseMessage response = await _client!.GetAsync(string.Format(PerPrinterRoute, Guid.NewGuid()));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // Serialization contract — camelCase properties & string enums
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetForPrinter_ReturnsCamelCaseJson_WithStringEnumStatus()
    {
        Printer printer = await SeedPrinterWithToolheadAsync();

        HttpResponseMessage response = await _client!.GetAsync(string.Format(PerPrinterRoute, printer.Id));
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
        status.GetString().Should().BeOneOf("Covers", "Insufficient", "Unknown");

        // camelCase nested toolhead properties
        JsonElement slot = toolheads.EnumerateArray().First();
        slot.TryGetProperty("toolheadIndex", out _).Should().BeTrue();
        slot.TryGetProperty("toolheadName", out _).Should().BeTrue();
        slot.TryGetProperty("status", out JsonElement slotStatus).Should().BeTrue();
        slotStatus.ValueKind.Should().Be(JsonValueKind.String);
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
    // Performance envelope — a moderately large fleet stays snappy
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetFleet_LargeFleet_CompletesWithinReasonableBudget()
    {
        // Seed a fleet larger than the default parallelism (8) so the
        // semaphore fan-out is exercised. This is a smoke check, not a
        // benchmark — the budget is generous to avoid CI flakiness.
        const int printerCount = 24;
        for (int i = 0; i < printerCount; i++)
        {
            _ = await SeedPrinterWithToolheadAsync($"perf-{i:D2}");
        }

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response = await _client!.GetAsync(FleetRoute);
        sw.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FleetFilamentCoverageDto? fleet = await response.Content.ReadFromJsonAsync<FleetFilamentCoverageDto>();
        fleet.Should().NotBeNull();
        fleet!.Printers.Should().HaveCountGreaterThanOrEqualTo(printerCount);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "fleet endpoint must batch spool + job queries and fan out with bounded parallelism");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<Printer> SeedPrinterWithToolheadAsync(string? name = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (Guid manufacturerId, Guid modelId) = await TestInfrastructure.TestHelpers.GetUnknownCatalogIdsAsync(ctx);

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
