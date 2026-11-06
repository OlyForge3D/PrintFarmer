using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public sealed class ObicoServerAssignmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IObicoConnectivityProbe> _probe = new();
    private readonly ObicoSettings _settings = new() { Enabled = true, ObicoApiUrl = "http://global.invalid" };

    public ObicoServerAssignmentServiceTests()
    {
        string connectionString = $"Data Source=obico-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        using AppDbContext db = CreateDbContext();
        db.Database.EnsureCreated();
        _probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(true, 1, null));
    }

    [Fact]
    public async Task GetRouteAsync_ActivePrint_UsesItsReservationWithoutGlobalFallback()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:print")], _settings);

        ObicoPrintRoute route = await CreateService().GetRouteAsync(printer.Id, _settings);

        route.Url.Should().Be(server.Url);
        _probe.Verify(probe => probe.ProbeAsync(_settings.ObicoApiUrl, null, It.IsAny<CancellationToken>()), Times.Never);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);
    }

    [Fact]
    public async Task GetRouteAsync_SelectedServerFails_DoesNotUseOtherCapacityOrGlobal()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer selected = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:print")], _settings);
        ObicoServer other = await AddServerAsync(4);
        _probe.Setup(probe => probe.ProbeAsync(selected.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(false, 0, "offline"));

        ObicoPrintRoute route = await CreateService().GetRouteAsync(printer.Id, _settings);

        route.Url.Should().BeNull();
        route.Source.Should().Be("pooled");
        route.UnavailableReason.Should().Contain("Reservation retained");
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(selected.Id);
        _probe.Verify(probe => probe.ProbeAsync(other.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _probe.Verify(probe => probe.ProbeAsync(_settings.ObicoApiUrl, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRouteAsync_IdlePrinterWithPool_DoesNotAllocateOrBypassCapacity()
    {
        Printer printer = await AddPrinterAsync();
        await AddServerAsync(1);

        ObicoPrintRoute route = await CreateService().GetRouteAsync(printer.Id, _settings);

        route.Url.Should().BeNull();
        route.UnavailableReason.Should().Contain("cannot bypass");
        using AppDbContext db = CreateDbContext();
        (await db.PrinterServiceStates.AnyAsync()).Should().BeFalse();
        _probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRouteAsync_LegacyGlobalOnly_DoesNotPermanentlyAssignIdlePrinter()
    {
        Printer printer = await AddPrinterAsync();

        ObicoPrintRoute route = await CreateService().GetRouteAsync(printer.Id, _settings);

        route.Url.Should().Be(_settings.ObicoApiUrl);
        using AppDbContext db = CreateDbContext();
        (await db.PrinterServiceStates.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ReconcileAsync_LegacyIdlePin_ReleasesCapacityWithoutAssigningIdlePrinters()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        await SetRouteAsync(printer.Id, server.Id, null);

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync(
            [new(printer.Id, null)], _settings);

        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().BeNull();
        routes[printer.Id].Url.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_HealthyUnequalServers_UsesLowestUtilizationAndRetainsOtherPrints()
    {
        Printer existing = await AddPrinterAsync();
        Printer starting = await AddPrinterAsync();
        ObicoServer small = await AddServerAsync(1);
        ObicoServer large = await AddServerAsync(4);
        await SetRouteAsync(existing.Id, small.Id, "external:existing");
        ObicoServer disabled = await AddServerAsync(10, enabled: false);
        ObicoServer unhealthy = await AddServerAsync(10);
        _probe.Setup(probe => probe.ProbeAsync(unhealthy.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(false, 1, "unavailable"));

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync(
            [new(existing.Id, "external:existing"), new(starting.Id, "external:starting")], _settings);

        (await ReadStateAsync(starting.Id)).ObicoServerId.Should().Be(large.Id);
        (await ReadStateAsync(existing.Id)).ObicoServerId.Should().Be(small.Id);
        routes[starting.Id].Target.Should().Be(large.Name);
        _probe.Verify(probe => probe.ProbeAsync(disabled.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _probe.Verify(probe => probe.ProbeAsync(_settings.ObicoApiUrl, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_PartiallyLoadedServers_PrefersLowerNormalizedLoad()
    {
        Printer first = await AddPrinterAsync();
        Printer second = await AddPrinterAsync();
        Printer next = await AddPrinterAsync();
        ObicoServer small = await AddServerAsync(2);
        ObicoServer large = await AddServerAsync(8);
        await SetRouteAsync(first.Id, small.Id, "external:first");
        await SetRouteAsync(second.Id, large.Id, "external:second");

        await CreateService().ReconcileAsync([new(next.Id, "external:next")], _settings);

        (await ReadStateAsync(next.Id)).ObicoServerId.Should().Be(large.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileAsync_NoEligiblePoolServer_DoesNotOverflowToLegacyGlobal(bool enabled)
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1, enabled);
        _probe.Setup(probe => probe.ProbeAsync(server.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(false, 0, "unavailable"));

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync(
            [new(printer.Id, "external:print")], _settings);

        routes[printer.Id].Url.Should().BeNull();
        routes[printer.Id].UnavailableReason.Should().Contain("printing continues");
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().BeNull();
        _probe.Verify(probe => probe.ProbeAsync(_settings.ObicoApiUrl, null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_ServerLossAcrossServiceRestart_RetainsReservationAndRecoversSameServer()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        ObicoPrintActivity activity = new(printer.Id, "external:print");
        await CreateService().ReconcileAsync([activity], _settings);
        await AddServerAsync(8);
        _probe.Setup(probe => probe.ProbeAsync(server.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(false, 0, "offline"));

        IReadOnlyDictionary<Guid, ObicoPrintRoute> failed = await CreateService().ReconcileAsync([activity], _settings);

        failed[printer.Id].Url.Should().BeNull();
        failed[printer.Id].Source.Should().Be("pooled");
        failed[printer.Id].UnavailableReason.Should().Contain("Reservation retained");
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);

        _probe.Setup(probe => probe.ProbeAsync(server.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(true, 1, null));
        IReadOnlyDictionary<Guid, ObicoPrintRoute> recovered = await CreateService().ReconcileAsync([activity], _settings);
        recovered[printer.Id].Url.Should().Be(server.Url);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);
    }

    [Fact]
    public async Task ReconcileAsync_DisabledOrReducedCapacityServer_DoesNotMoveExistingPrint()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        ObicoPrintActivity activity = new(printer.Id, "external:print");
        await CreateService().ReconcileAsync([activity], _settings);
        using (AppDbContext db = CreateDbContext())
        {
            ObicoServer saved = await db.ObicoServers.SingleAsync();
            saved.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await AddServerAsync(10);
        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync([activity], _settings);
        routes[printer.Id].Url.Should().BeNull();
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);
    }

    [Fact]
    public async Task ReconcileAsync_EndAndNewStartInSameBatch_ReleasesSlotBeforeAllocation()
    {
        Printer ended = await AddPrinterAsync();
        Printer next = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(ended.Id, "external:ended")], _settings);

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync(
            [new(next.Id, "external:next"), new(ended.Id, null)], _settings);

        (await ReadStateAsync(ended.Id)).ObicoServerId.Should().BeNull();
        (await ReadStateAsync(ended.Id)).ObicoPrintKey.Should().BeNull();
        (await ReadStateAsync(next.Id)).ObicoServerId.Should().Be(server.Id);
        routes[next.Id].Url.Should().Be(server.Url);
    }

    [Fact]
    public async Task ReconcileAsync_UnknownExternalPrint_RetainsCapacityUntilConfirmedEnd()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:print")], _settings);

        await CreateService().ReconcileAsync([new(printer.Id, null, RetainExisting: true)], _settings);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);

        await CreateService().ReconcileAsync([new(printer.Id, null)], _settings);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_TrackedJobEndsWhileOffline_ReleasesReservation()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        Guid jobId = Guid.NewGuid();
        using (AppDbContext db = CreateDbContext())
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "print",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Printing
            });
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileAsync([new(printer.Id, $"job:{jobId:N}")], _settings);
        await CreateService().ReconcileAsync([new(printer.Id, null, RetainExisting: true)], _settings);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(server.Id);
        using (AppDbContext db = CreateDbContext())
        {
            (await db.PrintJobs.SingleAsync()).Status = PrintJobStatus.Completed;
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileAsync([new(printer.Id, null, RetainExisting: true)], _settings);
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_SyntheticExternalJobArrives_AdoptsExistingReservation()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer server = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:observed")], _settings);
        await AddServerAsync(10);
        Guid jobId = Guid.NewGuid();
        using (AppDbContext db = CreateDbContext())
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "print",
                AssignedPrinterId = printer.Id,
                IsExternalPrint = true,
                Status = PrintJobStatus.Printing
            });
            await db.SaveChangesAsync();
        }

        string jobKey = $"job:{jobId:N}";

        await CreateService().ReconcileAsync([new(printer.Id, jobKey, AdoptExternalKey: "external:observed")], _settings);

        PrinterServiceState state = await ReadStateAsync(printer.Id);
        state.ObicoPrintKey.Should().Be(jobKey);
        state.ObicoServerId.Should().Be(server.Id);
    }

    [Fact]
    public async Task ReconcileAsync_StaleEndedJobObservation_DoesNotResurrectReservation()
    {
        Printer printer = await AddPrinterAsync();
        await AddServerAsync(1);
        Guid jobId = Guid.NewGuid();
        using (AppDbContext db = CreateDbContext())
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "print",
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Printing
            });
            await db.SaveChangesAsync();
        }

        ObicoPrintActivity observation = new(printer.Id, $"job:{jobId:N}");
        await CreateService().ReconcileAsync([observation], _settings);
        using (AppDbContext db = CreateDbContext())
        {
            (await db.PrintJobs.SingleAsync()).Status = PrintJobStatus.Completed;
            await db.SaveChangesAsync();
        }

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync([observation], _settings);

        routes[printer.Id].Url.Should().BeNull();
        routes[printer.Id].UnavailableReason.Should().Contain("tracked print ended");
        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_NewPrintIdentity_ReleasesPreviousPrintRoute()
    {
        Printer printer = await AddPrinterAsync();
        ObicoServer previous = await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:first")], _settings);
        ObicoServer next = await AddServerAsync(5);
        using (AppDbContext db = CreateDbContext())
        {
            (await db.ObicoServers.SingleAsync(server => server.Id == previous.Id)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileAsync([new(printer.Id, "external:second")], _settings);

        (await ReadStateAsync(printer.Id)).ObicoServerId.Should().Be(next.Id);
        (await ReadStateAsync(printer.Id)).ObicoPrintKey.Should().Be("external:second");
    }

    [Fact]
    public async Task ReconcileAsync_LegacyGlobalPrintAndNewPool_PreservesExistingRouteWithoutGlobalOverflow()
    {
        Printer existing = await AddPrinterAsync();
        Printer pooled = await AddPrinterAsync();
        await CreateService().ReconcileAsync([new(existing.Id, "external:existing")], _settings);
        ObicoServer server = await AddServerAsync(1);

        IReadOnlyDictionary<Guid, ObicoPrintRoute> routes = await CreateService().ReconcileAsync(
            [new(existing.Id, "external:existing"), new(pooled.Id, "external:pooled")], _settings);

        routes[existing.Id].Source.Should().Be("global");
        routes[existing.Id].Url.Should().Be(_settings.ObicoApiUrl);
        routes[pooled.Id].Source.Should().Be("pooled");
        (await ReadStateAsync(pooled.Id)).ObicoServerId.Should().Be(server.Id);
        await CreateService().ReconcileAsync([new(existing.Id, null)], _settings);
        (await ReadStateAsync(existing.Id)).ObicoFallbackUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileAsync_MonitoringDisabledOrOptedOut_ReleasesReservations(bool disableGlobal)
    {
        Printer printer = await AddPrinterAsync();
        await AddServerAsync(1);
        await CreateService().ReconcileAsync([new(printer.Id, "external:print")], _settings);
        if (disableGlobal)
        {
            _settings.Enabled = false;
        }
        else
        {
            using AppDbContext db = CreateDbContext();
            (await db.Printers.SingleAsync()).ObicoEnabled = false;
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileAsync([], _settings);

        PrinterServiceState state = await ReadStateAsync(printer.Id);
        state.ObicoServerId.Should().BeNull();
        state.ObicoPrintKey.Should().BeNull();
    }

#pragma warning disable VSTHRD003 // The tasks are deliberately concurrent and both are awaited before fixture disposal.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_ConcurrentStartsAcrossInstances_NeverExceedsCapacity(bool samePrinter)
    {
        Printer first = await AddPrinterAsync();
        Printer second = await AddPrinterAsync();
        await AddServerAsync(1);
        var probesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        _probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    probesReady.SetResult();
                }

                await probesReady.Task.WaitAsync(ct);
                return new ObicoConnectivityResult(true, 1, null);
            });

        Task<IReadOnlyDictionary<Guid, ObicoPrintRoute>> firstStart = Task.Run(() =>
            CreateService().ReconcileAsync([new(first.Id, "external:first")], _settings));
        Task<IReadOnlyDictionary<Guid, ObicoPrintRoute>> secondStart = Task.Run(() =>
            CreateService().ReconcileAsync([new(samePrinter ? first.Id : second.Id, samePrinter ? "external:first" : "external:second")], _settings));
        IReadOnlyDictionary<Guid, ObicoPrintRoute>[] results = await Task.WhenAll(firstStart, secondStart).WaitAsync(TimeSpan.FromSeconds(15));

        using AppDbContext db = CreateDbContext();
        (await db.PrinterServiceStates.CountAsync(state => state.ObicoServerId != null)).Should().Be(1);
        results.SelectMany(result => result.Values).Count(route => route.Url is not null).Should().Be(samePrinter ? 2 : 1);
        if (!samePrinter)
        {
            results.SelectMany(result => result.Values).Single(route => route.Url is null)
                .UnavailableReason.Should().Contain("capacity");
        }
    }
#pragma warning restore VSTHRD003

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateDbContext() => new(_options);

    private ObicoServerAssignmentService CreateService()
    {
        var factory = new Mock<IDbContextFactory<AppDbContext>>();
        factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateDbContext());
        return new(factory.Object, _probe.Object, NullLogger<ObicoServerAssignmentService>.Instance);
    }

    private async Task<Printer> AddPrinterAsync()
    {
        using AppDbContext db = CreateDbContext();
        Guid id = Guid.NewGuid();
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = $"maker-{id:N}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = $"model-{id:N}", Manufacturer = manufacturer };
        Printer printer = new()
        {
            Id = id,
            Name = $"printer-{id:N}",
            Manufacturer = manufacturer,
            Model = model,
            ServerUrl = $"http://printer-{id:N}.invalid",
            ObicoEnabled = true
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }

    private async Task<ObicoServer> AddServerAsync(int capacity, bool enabled = true)
    {
        using AppDbContext db = CreateDbContext();
        Guid id = Guid.NewGuid();
        ObicoServer server = new()
        {
            Id = id,
            Name = $"server-{id:N}",
            Url = $"http://server-{id:N}.invalid",
            MaxConcurrentAnalyses = capacity,
            IsEnabled = enabled
        };
        db.ObicoServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private async Task SetRouteAsync(Guid printerId, Guid serverId, string? key)
    {
        using AppDbContext db = CreateDbContext();
        db.PrinterServiceStates.Add(new PrinterServiceState
        {
            PrinterId = printerId,
            ObicoServerId = serverId,
            ObicoPrintKey = key
        });
        await db.SaveChangesAsync();
    }

    private async Task<PrinterServiceState> ReadStateAsync(Guid printerId)
    {
        using AppDbContext db = CreateDbContext();
        return await db.PrinterServiceStates.AsNoTracking().SingleAsync(state => state.PrinterId == printerId);
    }
}
