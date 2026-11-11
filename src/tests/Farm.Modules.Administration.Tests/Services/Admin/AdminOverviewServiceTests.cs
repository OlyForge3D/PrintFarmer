using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Administration.Services.Admin;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Services.Admin;

/// <summary>
/// Unit tests for <see cref="AdminOverviewService"/>. These verify that the aggregation:
/// 1. Composes the existing <see cref="HealthCheckService"/> results into the expected tiles.
/// 2. Splits the <c>comprehensive</c> health check's Data payload into database status.
/// 3. Degrades gracefully when the health check service throws.
/// 4. Ranks attention items by severity (Error > Warning > Info).
/// 5. Extracts per-printer attention items from connection-provider snapshots.
/// 6. Hides the spoolman tile when it reports "not configured".
/// 7. Sends enum values as strings on the wire.
/// </summary>
public class AdminOverviewServiceTests
{
    private readonly Mock<HealthCheckService> _healthCheckService = new();
    private readonly List<IPrinterConnectionHealthProvider> _connectionHealthProviders = new();
    private readonly List<PrinterMonitoringSnapshot> _monitoringSnapshots = [];
    private readonly List<PrinterReviewRequiredJob> _reviewRequiredJobs = [];

    private AdminOverviewService CreateService()
    {
        Mock<IPrinterMonitoringReader> monitoring = new();
        monitoring.Setup(reader => reader.GetSnapshotsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_monitoringSnapshots);
        monitoring.Setup(reader => reader.GetReviewRequiredJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_reviewRequiredJobs);
        return new(_healthCheckService.Object, _connectionHealthProviders,
            NullLogger<AdminOverviewService>.Instance, monitoring.Object);
    }

    private void SetPrinterConnectivity(params PrinterConnectionHealth[] printers)
    {
        Mock<IPrinterConnectionHealthProvider> provider = new();
        _ = provider.Setup(p => p.GetConnectionHealth())
            .Returns(printers.ToDictionary(p => p.PrinterId));
        _connectionHealthProviders.Add(provider.Object);
        _monitoringSnapshots.AddRange(printers.Select(health => new PrinterMonitoringSnapshot(
            new Printer { Id = health.PrinterId, Name = health.PrinterName, Backend = (int)health.Backend },
            new PrinterStatusCacheSnapshot(new PrinterStatusDto(health.PrinterId,
                health.ConnectionState == PrinterConnectionState.Connected, health.ConnectionState.ToString()), DateTime.UtcNow),
            false)));
    }

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OutOfServicePrintersAreExcludedFromHealthDenominatorAndWarnings()
    {
        SetPrinterConnectivity(ConnectedPrinter("online"), new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Shutdown",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Offline,
        });
        _monitoringSnapshots[1].Printer.IsEnabled = false;
        SetHealthyReport();

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Single(tile => tile.Key == "backends").Detail.Should().Be("1 / 1 in service reachable");
        result.Subsystems.Single(tile => tile.Key == "backends").Status.Should().Be(SubsystemStatus.Healthy);
        result.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task AllPrintersOutOfService_IsNeutralWithoutInventingReachablePrinters()
    {
        _monitoringSnapshots.Add(new(new Printer { Name = "Out of service", IsEnabled = false }, null, false));
        SetHealthyReport();

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Single(tile => tile.Key == "backends").Detail.Should().Be("No printers in service");
        result.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task NoStatusWithoutAcknowledgement_IsUnknownNotAllClear()
    {
        _monitoringSnapshots.Add(new(new Printer { Name = "Unknown" }, null, false));
        SetHealthyReport();

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Single(tile => tile.Key == "backends").Status.Should().Be(SubsystemStatus.Unknown);
        result.Subsystems.Single(tile => tile.Key == "backends").Detail.Should().Contain("1 status unknown");
        result.Attention.Should().ContainSingle(item => item.Title.Contains("status is unknown"));
    }

    [Fact]
    public async Task ReviewRequiredJobRemainsActionableAlongsideProviderFailures()
    {
        Printer printer = new() { Name = "Active", IsEnabled = true };
        _monitoringSnapshots.Add(new(printer,
            new(new PrinterStatusDto(printer.Id, false, "Offline"), DateTime.UtcNow), true));
        Guid jobId = Guid.NewGuid();
        _reviewRequiredJobs.Add(new(jobId, "Active job", printer.Id, printer.Name));
        Mock<IPrinterConnectionHealthProvider> broken = new();
        broken.Setup(value => value.GetConnectionHealth()).Throws(new InvalidOperationException("Provider unavailable"));
        _connectionHealthProviders.Add(broken.Object);
        SetHealthyReport();

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Single(tile => tile.Key == "backends").Status.Should().NotBe(SubsystemStatus.Healthy);
        result.Attention.Should().Contain(item => item.Key == $"printer-{printer.Id}-job-{jobId}-review"
            && item.Title == "Review active job 'Active job'"
            && item.ActionRoute == $"/printers/{printer.Id}");
        result.Attention.Should().NotContain(item => item.Key == $"printer-{printer.Id}-unreachable");
        result.Attention.Should().Contain(item => item.Key == "backends-degraded");
    }

    private void SetHealthyReport() => _healthCheckService.Setup(service => service.CheckHealthAsync(
        It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(BuildReport(
            comprehensive: HealthCheckResult.Healthy("Ready", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("Ready"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured")));

    [Fact]
    public async Task GetOverviewAsync_WhenMonitoringRegistryThrows_DegradesBackendsAndAddsAttention()
    {
        SetHealthyReport();
        Mock<IPrinterMonitoringReader> monitoring = new();
        monitoring.Setup(reader => reader.GetSnapshotsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Registry unavailable"));
        AdminOverviewService service = new(_healthCheckService.Object, _connectionHealthProviders,
            NullLogger<AdminOverviewService>.Instance, monitoring.Object);

        AdminOverviewDto result = await service.GetOverviewAsync();

        result.Subsystems.Single(tile => tile.Key == "backends").Status.Should().Be(SubsystemStatus.Degraded);
        result.Attention.Should().ContainSingle(item => item.Key == "backends-degraded");
        result.OverallStatus.Should().Be(SubsystemStatus.Degraded);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenCallerCancelsDuringMonitoringRead_PropagatesCancellation()
    {
        SetHealthyReport();
        using CancellationTokenSource cts = new();
        Mock<IPrinterMonitoringReader> monitoring = new();
        monitoring.Setup(reader => reader.GetSnapshotsAsync(cts.Token))
            .Callback(() => cts.Cancel())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        AdminOverviewService service = new(_healthCheckService.Object, _connectionHealthProviders,
            NullLogger<AdminOverviewService>.Instance, monitoring.Object);

        Func<Task> act = async () => await service.GetOverviewAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Degraded)]
    public async Task GetOverviewAsync_PrintGuardEntry_ShowsOnlyEnabledTileAndOutageAttention(bool enabled, HealthStatus status)
    {
        const string detail = "PrintGuard configuration invalid: configure a service URL";
        var entry = new HealthReportEntry(status, detail, TimeSpan.Zero, null,
            new Dictionary<string, object> { ["enabled"] = enabled });
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["comprehensive"] = new(HealthStatus.Healthy, "Core ready", TimeSpan.Zero, null, null),
            ["signalr"] = new(HealthStatus.Healthy, "Hub ready", TimeSpan.Zero, null, null),
            ["spoolman"] = new(HealthStatus.Healthy, "Spoolman not configured", TimeSpan.Zero, null, null),
            ["printguard"] = entry,
        }, TimeSpan.Zero);
        _healthCheckService.Setup(service => service.CheckHealthAsync(
            It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(report);

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Count(tile => tile.Key == "printguard").Should().Be(enabled ? 1 : 0);
        if (enabled)
        {
            SubsystemHealthDto tile = result.Subsystems.Single(tile => tile.Key == "printguard");
            tile.Name.Should().Be("PrintGuard Failure Detection");
            tile.Detail.Should().Be(detail);
            tile.Status.Should().Be(status == HealthStatus.Healthy ? SubsystemStatus.Healthy : SubsystemStatus.Degraded);
        }

        if (status == HealthStatus.Degraded)
        {
            result.Attention.Should().ContainSingle(item => item.Key == "health-printguard"
                && item.Title == "PrintGuard failure detection is not healthy" && item.Detail == detail);
            result.OverallStatus.Should().Be(SubsystemStatus.Degraded);
        }
        else
        {
            result.Attention.Should().NotContain(item => item.Key == "health-printguard");
        }
    }

    [Theory]
    [InlineData(false, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Healthy)]
    [InlineData(true, HealthStatus.Degraded)]
    public async Task GetOverviewAsync_ObicoEntry_ShowsOnlyEnabledTileAndOutageAttention(bool enabled, HealthStatus status)
    {
        var entry = new HealthReportEntry(status, "Generic Obico availability", TimeSpan.Zero, null,
            new Dictionary<string, object> { ["enabled"] = enabled });
        var report = new HealthReport(new Dictionary<string, HealthReportEntry> { ["obico"] = entry }, TimeSpan.Zero);
        _healthCheckService.Setup(service => service.CheckHealthAsync(
            It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(report);

        AdminOverviewDto result = await CreateService().GetOverviewAsync();

        result.Subsystems.Count(tile => tile.Key == "obico").Should().Be(enabled ? 1 : 0);
        if (enabled)
        {
            SubsystemHealthDto tile = result.Subsystems.Single(tile => tile.Key == "obico");
            tile.Name.Should().Be("Obico Failure Detection");
            tile.Detail.Should().Be("Generic Obico availability");
            tile.Status.Should().Be(status == HealthStatus.Healthy ? SubsystemStatus.Healthy : SubsystemStatus.Degraded);
        }

        result.Attention.Count(item => item.Key == "health-obico").Should().Be(status == HealthStatus.Degraded ? 1 : 0);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenAllChecksHealthy_ReturnsAllHealthyTiles()
    {
        SetPrinterConnectivity(
            ConnectedPrinter("printer-01"),
            ConnectedPrinter("printer-02"),
            ConnectedPrinter("printer-03"));

        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("All systems operational", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("SignalR fully operational"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        overview.Subsystems.Should().HaveCount(4); // api, database, signalr, backends (spoolman hidden)
        overview.Subsystems.Select(s => s.Key).Should().ContainInOrder("api", "database", "signalr", "backends");
        overview.Subsystems.Should().OnlyContain(s => s.Status == SubsystemStatus.Healthy);
        overview.OverallStatus.Should().Be(SubsystemStatus.Healthy);
        overview.Attention.Should().BeEmpty();
        overview.CheckedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ─── Overall status roll-up (regression for #2222) ────────────────────────

    [Fact]
    public async Task GetOverviewAsync_WhenOnlyPrinterBackendsAreDegraded_OverallStatusIsDegradedNotHealthy()
    {
        // Regression test for #2222: the Admin Control Center reported "System Healthy"
        // in the header while the "Printer Backends" tile simultaneously reported
        // "Degraded (4/5 reachable)". Every other subsystem is Healthy here — only the
        // backends tile is Degraded — so a naive "first tile wins" or "ignore backends"
        // rollup would report Healthy. The overall status must reflect the worst
        // subsystem status across the board.
        SetPrinterConnectivity(
            ConnectedPrinter("printer-01"),
            ConnectedPrinter("printer-02"),
            ConnectedPrinter("printer-03"),
            ConnectedPrinter("printer-04"),
            new PrinterConnectionHealth
            {
                PrinterId = Guid.NewGuid(),
                PrinterName = "Moonraker Offline",
                Backend = PrinterBackend.Moonraker,
                ConnectionState = PrinterConnectionState.Offline,
            });

        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("All systems operational", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("SignalR fully operational"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        SubsystemHealthDto backends = overview.Subsystems.Single(s => s.Key == "backends");
        backends.Status.Should().Be(SubsystemStatus.Degraded);
        backends.Detail.Should().Be("4 / 5 reachable");

        overview.Subsystems.Where(s => s.Key != "backends").Should().OnlyContain(s => s.Status == SubsystemStatus.Healthy);
        overview.OverallStatus.Should().Be(SubsystemStatus.Degraded);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenDatabaseUnhealthyAndBackendsDegraded_OverallStatusIsUnhealthy()
    {
        // Unhealthy must outrank Degraded in the roll-up even though Degraded is also present.
        SetPrinterConnectivity(
            ConnectedPrinter("printer-01"),
            new PrinterConnectionHealth
            {
                PrinterId = Guid.NewGuid(),
                PrinterName = "printer-offline",
                Backend = PrinterBackend.Moonraker,
                ConnectionState = PrinterConnectionState.Offline,
            });

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

        overview.Subsystems.Single(s => s.Key == "database").Status.Should().Be(SubsystemStatus.Unhealthy);
        overview.Subsystems.Single(s => s.Key == "backends").Status.Should().Be(SubsystemStatus.Degraded);
        overview.OverallStatus.Should().Be(SubsystemStatus.Unhealthy);
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
        Guid offlinePrinterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        SetPrinterConnectivity(
            ConnectedPrinter("printer-01"),
            new PrinterConnectionHealth
            {
                PrinterId = offlinePrinterId,
                PrinterName = "printer-02",
                Backend = PrinterBackend.Moonraker,
                ConnectionState = PrinterConnectionState.Offline,
            },
            ConnectedPrinter("printer-03"));

        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Degraded,
                "External services unreachable (1/3)",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Healthy", Provider = "Npgsql.EntityFrameworkCore.PostgreSQL", ManufacturerCount = 8, Initialized = true },
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
        overview.Attention.Should().Contain(a => a.Key == $"printer-{offlinePrinterId}-unreachable");
        AttentionItemDto printerItem = overview.Attention.Single(a => a.Key == $"printer-{offlinePrinterId}-unreachable");
        printerItem.Severity.Should().Be(AttentionSeverity.Warning);
        printerItem.Title.Should().Contain("printer-02");
        printerItem.Detail.Should().Contain("Offline");
        // /printers has no ADMIN_DESTINATIONS registry entry (it's a top-level operational page),
        // so this attention item routes via ActionRoute directly rather than a destination id.
        printerItem.ActionDestinationId.Should().BeNull();
        printerItem.ActionRoute.Should().Be($"/printers/{offlinePrinterId}");
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

        SetPrinterConnectivity(
            ConnectedPrinter("printer-online"),
            new PrinterConnectionHealth
            {
                PrinterId = offlinePrinterId,
                PrinterName = "printer-offline",
                Backend = PrinterBackend.Moonraker,
                ConnectionState = PrinterConnectionState.Offline,
                LastConnectedUtc = DateTime.UtcNow.AddMinutes(-10),
                LastDisconnectedUtc = DateTime.UtcNow.AddMinutes(-1),
            });

        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Healthy,
                "Server systems operational",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Healthy", Provider = "Npgsql.EntityFrameworkCore.PostgreSQL", ManufacturerCount = 8, Initialized = true },
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
        printerItem.Detail.Should().Contain("is Offline");
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
        Guid offlinePrinterId = Guid.NewGuid();
        SetPrinterConnectivity(new PrinterConnectionHealth
        {
            PrinterId = offlinePrinterId,
            PrinterName = "printer-offline",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Offline,
        });

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated probe failure"));

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        // No exception surfaces to the caller. Response still populated.
        overview.Subsystems.Should().NotBeEmpty();
        overview.Subsystems.Should().Contain(s => s.Key == "api" && s.Status == SubsystemStatus.Degraded);
        overview.Subsystems.Should().Contain(s => s.Key == "database" && s.Status == SubsystemStatus.Unknown);
        overview.Subsystems.Should().Contain(s => s.Key == "signalr" && s.Status == SubsystemStatus.Unknown);
        overview.Subsystems.Should().Contain(s => s.Key == "backends" && s.Status == SubsystemStatus.Unhealthy);

        overview.Attention.Should().Contain(a => a.Key == "admin-overview-probe-failed"
            && a.Severity == AttentionSeverity.Error
            && a.Detail.Contains("simulated probe failure", StringComparison.Ordinal)
            && a.ActionDestinationId == "ops-status"
            && a.ActionRoute == null);
        overview.Attention.Should().Contain(a => a.Key == $"printer-{offlinePrinterId}-unreachable"
            && a.Severity == AttentionSeverity.Warning);

        // Regression: this response already mixes a confirmed Unhealthy tile (backends)
        // with Unknown tiles (database, signalr) from the same probe failure. Unhealthy
        // must win the roll-up — a confirmed failure must never be masked behind an
        // "Unknown" reported by an unrelated subsystem in the same overview.
        overview.OverallStatus.Should().Be(SubsystemStatus.Unhealthy);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenConnectionProviderThrows_DegradesBackendsAndAddsAttention()
    {
        Mock<IPrinterConnectionHealthProvider> provider = new();
        _ = provider.Setup(p => p.GetConnectionHealth())
            .Throws(new InvalidOperationException("provider crashed"));
        _connectionHealthProviders.Add(provider.Object);

        HealthReport report = BuildReport(
            comprehensive: HealthCheckResult.Healthy("ok", BuildComprehensiveData()),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));
        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        overview.Subsystems.Should().Contain(s => s.Key == "backends"
            && s.Status == SubsystemStatus.Degraded
            && s.Detail == "Printer status unavailable");
        overview.Attention.Should().Contain(a => a.Key == "backends-degraded"
            && a.Severity == AttentionSeverity.Warning
            && a.Detail.Contains("1 printer connection provider(s) failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOverviewAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Health service will throw OCE tied to the cancelled token
        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        Func<Task> act = async () => await CreateService().GetOverviewAsync(cts.Token);

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
        SetPrinterConnectivity(new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "printer-A",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Offline,
        });

        HealthReport report = BuildReport(
            comprehensive: new HealthCheckResult(
                HealthStatus.Unhealthy,
                "database unavailable",
                data: new Dictionary<string, object>
                {
                    ["Database"] = new { Status = "Unhealthy", Provider = "Microsoft.EntityFrameworkCore.Sqlite", ManufacturerCount = 0, Initialized = false },
                }),
            signalr: HealthCheckResult.Healthy("ok"),
            spoolman: HealthCheckResult.Healthy("Spoolman not configured"));

        _healthCheckService.Setup(s => s.CheckHealthAsync(It.IsAny<System.Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        AdminOverviewDto overview = await CreateService().GetOverviewAsync();

        // The database Error must sort ahead of the printer Warning.
        overview.Attention.Should().HaveCountGreaterThanOrEqualTo(2);
        overview.Attention[0].Severity.Should().Be(AttentionSeverity.Error);
        overview.Attention[^1].Severity.Should().Be(AttentionSeverity.Warning);
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
            OverallStatus = SubsystemStatus.Degraded,
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

        json.Should().Contain("\"overallStatus\":\"Degraded\"");
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

    private static Dictionary<string, object> BuildComprehensiveData(
        string databaseStatus = "Healthy")
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
        };
    }

    private static PrinterConnectionHealth ConnectedPrinter(string name) => new()
    {
        PrinterId = Guid.NewGuid(),
        PrinterName = name,
        Backend = PrinterBackend.Moonraker,
        ConnectionState = PrinterConnectionState.Connected,
    };
}
