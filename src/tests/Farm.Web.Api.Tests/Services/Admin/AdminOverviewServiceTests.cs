using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Services.Admin;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Admin;

/// <summary>
/// Unit tests for <see cref="AdminOverviewService"/>. These verify that the aggregation:
/// 1. Composes the existing <see cref="HealthCheckService"/> results into the expected tiles.
/// 2. Splits the <c>comprehensive</c> health check's Data payload into finer database and
///    backends tiles when possible.
/// 3. Degrades gracefully when the health check service throws.
/// 4. Ranks attention items by severity (Error > Warning > Info).
/// 5. Extracts per-printer attention items from ExternalServices.FailedServicesDetails.
/// 6. Hides the spoolman tile when it reports "not configured".
/// 7. Sends enum values as strings on the wire.
/// </summary>
public class AdminOverviewServiceTests
{
    private readonly Mock<HealthCheckService> _healthCheckService = new();

    private AdminOverviewService CreateService()
        => new(_healthCheckService.Object, NullLogger<AdminOverviewService>.Instance);

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenAllChecksHealthy_ReturnsAllHealthyTiles()
    {
        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("All systems operational", BuildComprehensiveData(
                databaseStatus: "Healthy",
                externalServicesStatus: "Healthy",
                checkedCount: 3,
                failedCount: 0)),
            signalr: HealthCheckResult.Healthy("SignalR fully operational"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        overview.Subsystems.Should().HaveCount(4); // api, database, signalr, backends (spoolman hidden)
        overview.Subsystems.Select(s => s.Key).Should().ContainInOrder("api", "database", "signalr", "backends");
        overview.Subsystems.Should().OnlyContain(s => s.Status == SubsystemStatus.Healthy);
        overview.Attention.Should().BeEmpty();
        overview.CheckedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetOverviewAsync_WhenSpoolmanConfiguredAndHealthy_ShowsSpoolmanTile()
    {
        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("ok", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("OK via /api/v1/health"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        overview.Subsystems.Should().Contain(s => s.Key == "spoolman" && s.Status == SubsystemStatus.Healthy);
    }

    // ─── Degraded subsystem ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenBackendsDegraded_MarksBackendsTileDegradedAndAddsAttention()
    {
        object externalServices = new Dictionary<string, object>
        {
            ["Status"] = "Degraded",
            ["CheckedCount"] = 3,
            ["FailedCount"] = 1,
            ["FailedServicesDetails"] = new[]
            {
                new
                {
                    Id = "11111111-1111-1111-1111-111111111111",
                    Name = "printer-02",
                    ServerUrl = "http://printer-02.local:7125",
                    AttemptedUrl = "http://printer-02.local:7125/server/info",
                    StatusCode = (int?)null,
                    ErrorMessage = "Connection refused",
                },
            },
        };

        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Degraded,
                "External services unreachable (1/3)",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Healthy", Provider = "Npgsql.EntityFrameworkCore.PostgreSQL", ManufacturerCount = 8, Initialized = true },
                    ["ExternalServices"] = externalServices,
                }),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        SubsystemHealthDto backends = overview.Subsystems.Single(s => s.Key == "backends");
        backends.Status.Should().Be(SubsystemStatus.Degraded);
        backends.Detail.Should().Be("2 / 3 reachable");

        // Database sub-tile picked from Comprehensive.Data
        SubsystemHealthDto database = overview.Subsystems.Single(s => s.Key == "database");
        database.Status.Should().Be(SubsystemStatus.Healthy);
        database.Detail.Should().Contain("PostgreSQL").And.Contain("8 manufacturers");

        // Attention: printer-specific item, not just a generic "backends degraded"
        overview.Attention.Should().Contain(a => a.Key == "printer-11111111-1111-1111-1111-111111111111-unreachable");
        AttentionItemDto printerItem = overview.Attention.Single(a => a.Key == "printer-11111111-1111-1111-1111-111111111111-unreachable");
        printerItem.Severity.Should().Be(AttentionSeverity.Warning);
        printerItem.Title.Should().Contain("printer-02");
        printerItem.Detail.Should().Contain("Connection refused");
        // /printers has no ADMIN_DESTINATIONS registry entry (it's a top-level operational page),
        // so this attention item routes via ActionRoute directly rather than a destination id.
        printerItem.ActionDestinationId.Should().BeNull();
        printerItem.ActionRoute.Should().Be("/printers");
    }

    [Fact]
    public async Task GetOverviewAsync_WhenBackendsDegradedWithConnectionHealthShape_AddsAttentionForOfflinePrinter()
    {
        // Regression test for #1870: ComprehensiveHealthCheck now emits FailedServicesDetails
        // entries shaped from IPrinterConnectionHealthProvider aggregation (Id, Name, Backend,
        // ConnectionState, LastConnectedUtc, LastDisconnectedUtc, ErrorMessage) instead of the
        // old HTTP-probe shape (ServerUrl/AttemptedUrl/StatusCode). This proves AdminOverviewService
        // still surfaces an offline printer correctly with the *current* producer contract, not
        // just the legacy shape asserted elsewhere in this file.
        Guid offlinePrinterId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        object externalServices = new Dictionary<string, object>
        {
            ["Status"] = "Degraded",
            ["CheckedCount"] = 2,
            ["FailedCount"] = 1,
            ["FailedServicesDetails"] = new[]
            {
                new
                {
                    Id = offlinePrinterId,
                    Name = "printer-offline",
                    Backend = "Moonraker",
                    ConnectionState = "Offline",
                    LastConnectedUtc = (DateTime?)DateTime.UtcNow.AddMinutes(-10),
                    LastDisconnectedUtc = (DateTime?)DateTime.UtcNow.AddMinutes(-1),
                    ErrorMessage = "Printer is Offline",
                },
            },
        };

        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Healthy,
                "Server systems operational",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Healthy", Provider = "Npgsql.EntityFrameworkCore.PostgreSQL", ManufacturerCount = 8, Initialized = true },
                    ["ExternalServices"] = externalServices,
                }),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        SubsystemHealthDto backends = overview.Subsystems.Single(s => s.Key == "backends");
        backends.Status.Should().Be(SubsystemStatus.Degraded);
        backends.Detail.Should().Be("1 / 2 reachable");

        string expectedKey = $"printer-{offlinePrinterId}-unreachable";
        overview.Attention.Should().Contain(a => a.Key == expectedKey);
        AttentionItemDto printerItem = overview.Attention.Single(a => a.Key == expectedKey);
        printerItem.Severity.Should().Be(AttentionSeverity.Warning);
        printerItem.Title.Should().Contain("printer-offline");
        printerItem.Detail.Should().Contain("Printer is Offline");
    }

    [Fact]
    public async Task GetOverviewAsync_WhenDatabaseNotInitialized_AddsErrorAttentionAndDegradesTile()
    {
        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Unhealthy,
                "Database not initialized",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Unhealthy", Provider = "Microsoft.EntityFrameworkCore.Sqlite", ManufacturerCount = 0, Initialized = false },
                }),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        SubsystemHealthDto db = overview.Subsystems.Single(s => s.Key == "database");
        db.Status.Should().Be(SubsystemStatus.Unhealthy);
        db.Detail.Should().Contain("not initialized");

        AttentionItemDto dbItem = overview.Attention.Single(a => a.Key == "database-unhealthy");
        dbItem.Severity.Should().Be(AttentionSeverity.Error);
        // Backend emits a stable destination id from ADMIN_DESTINATIONS; the frontend
        // resolves it to the current canonical path.
        dbItem.ActionDestinationId.Should().Be("ops-status");
        dbItem.ActionRoute.Should().BeNull();
    }

    // ─── Failing probe (graceful degradation) ─────────────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenHealthServiceThrows_ReturnsDegradedResponseNotError()
    {
        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated probe failure"));

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        // No exception surfaces to the caller. Response still populated.
        overview.Subsystems.Should().NotBeEmpty();
        overview.Subsystems.Should().Contain(s => s.Key == "api" && s.Status == SubsystemStatus.Degraded);
        overview.Subsystems.Where(s => s.Key != "api").Should().OnlyContain(s => s.Status == SubsystemStatus.Unknown);

        overview.Attention.Should().Contain(a => a.Key == "admin-overview-probe-failed"
            && a.Severity == AttentionSeverity.Error
            && a.Detail.Contains("simulated probe failure", StringComparison.Ordinal)
            && a.ActionDestinationId == "ops-status"
            && a.ActionRoute == null);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Health service will throw OCE tied to the cancelled token
        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = () => CreateService().GetOverviewAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── Empty attention when everything is healthy ───────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenEverythingHealthy_ReturnsEmptyAttentionList()
    {
        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("All systems operational", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("SignalR fully operational"),
            spoolman: HealthCheckResult.Healthy("OK via /api/v1/health"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        overview.Attention.Should().BeEmpty();
    }

    // ─── Ranking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOverviewAsync_RanksAttentionByErrorThenWarning()
    {
        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Degraded,
                "backends degraded",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Unhealthy", Provider = "Microsoft.EntityFrameworkCore.Sqlite", ManufacturerCount = 0, Initialized = false },
                    ["ExternalServices"] = new Dictionary<string, object>
                    {
                        ["Status"] = "Degraded",
                        ["CheckedCount"] = 2,
                        ["FailedCount"] = 1,
                        ["FailedServicesDetails"] = new[]
                        {
                            new { Id = "abc", Name = "printer-A", ServerUrl = "http://a.local", ErrorMessage = "timeout" },
                        },
                    },
                }),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        // The database Error must sort ahead of the printer Warning.
        overview.Attention.Should().HaveCountGreaterThanOrEqualTo(2);
        overview.Attention[0].Severity.Should().Be(AttentionSeverity.Error);
        overview.Attention.Last().Severity.Should().Be(AttentionSeverity.Warning);
    }

    // ─── Serialization contract ───────────────────────────────────────────────

    [Fact]
    public void Enums_SerializeAsStrings_ForClientCompatibility()
    {
        // The client (React/TypeScript) expects string enum values via JsonStringEnumConverter,
        // which is registered globally in Program.cs. This test locks in that contract for the
        // subsystem status and attention severity enums.
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        AdminOverviewDto dto = new()
        {
            CheckedAt = new DateTime(2026, 7, 25, 17, 4, 0, DateTimeKind.Utc),
            Subsystems = new[]
            {
                new SubsystemHealthDto { Key = "api", Name = "API", Status = SubsystemStatus.Healthy, Detail = "Responding" },
                new SubsystemHealthDto { Key = "backends", Name = "Printer Backends", Status = SubsystemStatus.Degraded, Detail = "2 / 3 reachable" },
            },
            Attention = new[]
            {
                new AttentionItemDto
                {
                    Key = "printer-abc-unreachable",
                    Severity = AttentionSeverity.Warning,
                    Title = "Printer 'A' unreachable",
                    Detail = "Timeout",
                    ActionLabel = "Open Printers",
                    ActionRoute = "/printers",
                },
                new AttentionItemDto
                {
                    Key = "database-unhealthy",
                    Severity = AttentionSeverity.Error,
                    Title = "Database is not healthy",
                    Detail = "not initialized",
                    ActionLabel = "Open System info",
                    ActionDestinationId = "ops-status",
                },
            },
        };

        string json = JsonSerializer.Serialize(dto, options);

        json.Should().Contain("\"status\":\"Healthy\"");
        json.Should().Contain("\"status\":\"Degraded\"");
        json.Should().Contain("\"severity\":\"Warning\"");
        json.Should().NotContain("\"status\":0").And.NotContain("\"severity\":1");
        json.Should().Contain("\"checkedAt\":");
        json.Should().Contain("\"actionRoute\":\"/printers\"");
        json.Should().Contain("\"actionDestinationId\":\"ops-status\"");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static HealthReport BuildReport(
        HealthCheckResult comprehensive,
        HealthCheckResult signalr,
        HealthCheckResult spoolman)
    {
        Dictionary<string, HealthReportEntry> entries = new(StringComparer.Ordinal)
        {
            ["comprehensive"] = new HealthReportEntry(
                comprehensive.Status,
                comprehensive.Description,
                TimeSpan.FromMilliseconds(30),
                comprehensive.Exception,
                comprehensive.Data),
            ["signalr"] = new HealthReportEntry(
                signalr.Status,
                signalr.Description,
                TimeSpan.FromMilliseconds(2),
                signalr.Exception,
                signalr.Data),
            ["spoolman"] = new HealthReportEntry(
                spoolman.Status,
                spoolman.Description,
                TimeSpan.FromMilliseconds(1),
                spoolman.Exception,
                spoolman.Data),
        };

        return new HealthReport(entries, TimeSpan.FromMilliseconds(50));
    }

    private static IReadOnlyDictionary<string, object> BuildComprehensiveData(
        string databaseStatus = "Healthy",
        string externalServicesStatus = "Healthy",
        int checkedCount = 0,
        int failedCount = 0)
    {
        return new Dictionary<string, object>
        {
            ["Database"] = new
            {
                Status = databaseStatus,
                Provider = "Npgsql.EntityFrameworkCore.PostgreSQL",
                ManufacturerCount = 8,
                Initialized = databaseStatus == "Healthy",
            },
            ["ExternalServices"] = new Dictionary<string, object>
            {
                ["Status"] = externalServicesStatus,
                ["CheckedCount"] = checkedCount,
                ["FailedCount"] = failedCount,
            },
        };
    }
}
