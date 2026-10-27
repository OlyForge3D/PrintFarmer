using System.Collections.Immutable;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Gcode;

public sealed class PrinterFileCatalogTests
{
    [Fact]
    public async Task RunBatchAsync_UnexpectedPerPrinterFailureIsIsolatedAndBackedOff()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            Printer printer = await db.Printers.SingleAsync();
            printer.IsEnabled = true;
            PrinterFileScanState state = await db.PrinterFileScanStates.SingleAsync();
            state.ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer);
            state.NextScanAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        int attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.Factory);
        services.AddScoped<PrinterFileCatalogService>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("simulated unexpected scan failure");
        });
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        var worker = new PrinterFileIndexWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PrinterFileIndexWorker>.Instance);
        await worker.RunBatchAsync();
        await using AppDbContext current = fixture.Factory.CreateDbContext();
        PrinterFileScanState saved = await current.PrinterFileScanStates.SingleAsync();
        Assert.Equal(PrinterFileScanOutcome.Unavailable, saved.Outcome);
        Assert.Equal("scan_failed", saved.ReasonCode);
        Assert.Equal(1, saved.ConsecutiveFailures);
        Assert.True(saved.NextScanAtUtc > DateTime.UtcNow);
        Assert.Null(saved.LastCompleteScanAtUtc);
        await worker.RunBatchAsync();
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(PrinterFileInventoryOutcome.Unavailable, PrinterFileInventoryReason.AuthenticationRequired,
        PrinterFileScanOutcome.AuthenticationRequired)]
    [InlineData(PrinterFileInventoryOutcome.Unsupported, PrinterFileInventoryReason.Unsupported,
        PrinterFileScanOutcome.Unsupported)]
    public async Task PublishAsync_PausedScanWritesFiniteUtcTimestamp(
        PrinterFileInventoryOutcome outcome, PrinterFileInventoryReason reason, PrinterFileScanOutcome scanOutcome)
    {
        ScanDateObserver observer = new();
        await using Fixture fixture = await Fixture.CreateAsync(observer);
        Assert.True(await fixture.Service.PublishAsync(fixture.Lease.Object,
            new(outcome, reason, [], [])));
        Assert.NotNull(observer.PausedDate);
        Assert.Equal(DateTimeKind.Utc, observer.PausedDate.Value.Kind);
        Assert.InRange(observer.PausedDate.Value, DateTime.UtcNow.AddYears(1), DateTime.MaxValue.AddSeconds(-1));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        PrinterFileScanState saved = await db.PrinterFileScanStates.SingleAsync();
        Assert.Equal(scanOutcome, saved.Outcome);
        Assert.Equal(observer.PausedDate.Value.Ticks, saved.NextScanAtUtc.Ticks);
        Assert.False(saved.NextScanAtUtc <= DateTime.UtcNow);
    }

    [Fact]
    public async Task PublishAsync_PartialWithoutCoverageKeepsUnobservedLocations()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "old.gcode");
        bool published = await fixture.Service.PublishAsync(fixture.Lease.Object,
            new PrinterFileInventoryResult(
                PrinterFileInventoryOutcome.Partial, PrinterFileInventoryReason.EntryLimit,
                [new("gcodes", "nested/new.gcode", 0)], []));
        Assert.True(published);
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        var locations = await db.PrinterFileLocations.ToArrayAsync();
        Assert.Equal(2, locations.Length);
        Assert.All(locations, x => Assert.Equal(PrinterFileAvailability.Available, x.Availability));
        Assert.Null(Assert.Single(locations, x => x.RelativePath == "old.gcode").LastCompleteScanAtUtc);
        Assert.Equal(0, Assert.Single(locations, x => x.RelativePath == "nested/new.gcode").SizeBytes);
        Assert.Equal(PrinterFileScanOutcome.Partial, (await db.PrinterFileScanStates.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task PublishAsync_PartialCanOnlyTombstoneProvedCompleteRoot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "missing.gcode");
        await fixture.AddAsync("sdcard", "unavailable.gcode");
        await fixture.Service.PublishAsync(fixture.Lease.Object,
            new PrinterFileInventoryResult(
                PrinterFileInventoryOutcome.Partial, PrinterFileInventoryReason.StorageUnavailable,
                [], [new("gcodes", "", true, PrinterFileInventoryEvidence.TraversedDirectories),
                     new("sdcard", "", false, PrinterFileInventoryEvidence.None)]));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(PrinterFileAvailability.Missing,
            (await db.PrinterFileLocations.SingleAsync(x => x.StorageRoot == "gcodes")).Availability);
        Assert.Equal(PrinterFileAvailability.Available,
            (await db.PrinterFileLocations.SingleAsync(x => x.StorageRoot == "sdcard")).Availability);
        Assert.Single(await db.PrinterFileRootScans.ToArrayAsync());
        Assert.Null((await db.PrinterFileScanStates.SingleAsync()).LastCompleteScanAtUtc);
    }

    [Fact]
    public async Task PublishAsync_EmptyCompleteDoesNotInventCoverageOfUndeclaredRoot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("sdcard", "keep.gcode");
        await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([], [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)]));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(PrinterFileAvailability.Available, (await db.PrinterFileLocations.SingleAsync()).Availability);
        Assert.NotNull((await db.PrinterFileScanStates.SingleAsync()).LastCompleteScanAtUtc);
    }

    [Fact]
    public async Task PublishAsync_SubtreeCoverageUsesSegmentBoundary()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "one/old.gcode");
        await fixture.AddAsync("gcodes", "one-other/keep.gcode");
        await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([], [new("gcodes", "one", true, PrinterFileInventoryEvidence.TraversedDirectories)]));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(PrinterFileAvailability.Missing,
            (await db.PrinterFileLocations.SingleAsync(x => x.RelativePath == "one/old.gcode")).Availability);
        Assert.Equal(PrinterFileAvailability.Available,
            (await db.PrinterFileLocations.SingleAsync(x => x.RelativePath == "one-other/keep.gcode")).Availability);
    }

    [Fact]
    public async Task PublishAsync_MetadataChangesAdvanceObservationButDoNotCreateOwnedFiles()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "part.gcode");
        await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([new("gcodes", "part.gcode", 40, ModifiedValue: "new")], []));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync();
        Assert.Equal(2, location.ObservationRevision);
        Assert.Equal(40, location.SizeBytes);
        Assert.Equal("new", location.ModifiedValue);
        Assert.Empty(await db.GcodeFiles.ToArrayAsync());
        Assert.Empty(await db.PrinterFileOperations.ToArrayAsync());
    }

    [Fact]
    public async Task PublishAsync_DeletedObservationDoesNotResurrectFromIdenticalLateListing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "deleted.gcode", PrinterFileAvailability.Deleted);
        fixture.Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Assert.False(await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([new("gcodes", "deleted.gcode", 10)], [])));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(PrinterFileAvailability.Deleted, (await db.PrinterFileLocations.SingleAsync()).Availability);
    }

    [Fact]
    public async Task PublishAsync_FreshPostDeleteObservationAdvancesRevisionEvenWithIdenticalMetadata()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "recreated.gcode", PrinterFileAvailability.Deleted);
        Assert.True(await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([new("gcodes", "recreated.gcode", 10)],
                [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)])));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync();
        Assert.Equal(PrinterFileAvailability.Available, location.Availability);
        Assert.Equal(2, location.ObservationRevision);
        Assert.NotNull(location.LastCompleteScanAtUtc);
    }

    [Fact]
    public async Task PublishAsync_LostFenceCannotPublishEvenPositiveMetadata()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Assert.False(await fixture.Service.PublishAsync(fixture.Lease.Object,
            Result([new("gcodes", "late.gcode")], [])));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Empty(await db.PrinterFileLocations.ToArrayAsync());
        Assert.Equal(PrinterFileScanOutcome.NeverScanned, (await db.PrinterFileScanStates.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task PublishAsync_CancelledResultCannotDeclareAbsence()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "keep.gcode");
        Assert.False(await fixture.Service.PublishAsync(fixture.Lease.Object,
            new PrinterFileInventoryResult(
                PrinterFileInventoryOutcome.Cancelled, PrinterFileInventoryReason.Cancelled,
                [], [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)])));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(PrinterFileAvailability.Available, (await db.PrinterFileLocations.SingleAsync()).Availability);
        Assert.Null((await db.PrinterFileScanStates.SingleAsync()).LastAttemptAtUtc);
    }

    [Fact]
    public async Task ForceRefreshAsync_CompleteScanAdvancesUnchangedRevisionsAndResetsBackoff()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "a.gcode");
        PrinterFileInventoryResult inventory = Result([new("gcodes", "a.gcode", 10)],
            [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)]);
        Assert.True(await fixture.Service.PublishAsync(fixture.Lease.Object, inventory));
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            Assert.Equal(1, (await db.PrinterFileLocations.SingleAsync()).ObservationRevision);
            PrinterFileScanState state = await db.PrinterFileScanStates.SingleAsync();
            state.NextScanAtUtc = DateTime.MaxValue;
            state.ConsecutiveFailures = 5;
            state.Enabled = false;
            await db.SaveChangesAsync();
        }
        fixture.ArrangeForceRefresh("ready", inventory);

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.Completed, result.Status);
        Assert.Equal(1, result.FileCount);
        await using AppDbContext current = fixture.Factory.CreateDbContext();
        Assert.Equal(2, (await current.PrinterFileLocations.SingleAsync()).ObservationRevision);
        PrinterFileScanState saved = await current.PrinterFileScanStates.SingleAsync();
        Assert.Equal(PrinterFileScanOutcome.Complete, saved.Outcome);
        Assert.Equal(0, saved.ConsecutiveFailures);
        Assert.NotNull(saved.LastCompleteScanAtUtc);
        Assert.InRange(saved.NextScanAtUtc, DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
        Assert.False(saved.Enabled);
        fixture.Leases.Verify(x => x.TryAcquireForRefreshAsync(fixture.PrinterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PrinterFileOperationState.Pending, 1)]
    [InlineData(PrinterFileOperationState.AwaitingSourceConfirmation, 1)]
    [InlineData(PrinterFileOperationState.AwaitingDeleteConfirmation, 1)]
    [InlineData(PrinterFileOperationState.Queued, 2)]
    [InlineData(PrinterFileOperationState.Failed, 2)]
    public async Task ForceRefreshAsync_UnchangedFilePreservesOnlyInFlightOperationRevisions(
        PrinterFileOperationState operationState, long expectedRevision)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "a.gcode");
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync();
            db.PrinterFileOperations.Add(new PrinterFileOperation
            {
                Id = Guid.NewGuid(),
                ActorId = Guid.NewGuid(),
                SourcePrinterId = fixture.PrinterId,
                LocationId = location.Id,
                ObservationRevision = location.ObservationRevision,
                ConfigurationGeneration = location.ConfigurationGeneration,
                State = operationState,
                CommandKeyHash = new string('a', 64),
                PayloadSha256 = new string('b', 64),
                CommandJson = "{}",
            });
            await db.SaveChangesAsync();
        }
        fixture.ArrangeForceRefresh("ready", Result([new("gcodes", "a.gcode", 10)],
            [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)]));

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.Completed, result.Status);
        await using AppDbContext current = fixture.Factory.CreateDbContext();
        Assert.Equal(expectedRevision, (await current.PrinterFileLocations.SingleAsync()).ObservationRevision);
        Assert.Equal(1, (await current.PrinterFileOperations.SingleAsync()).ObservationRevision);
    }

    [Theory]
    [InlineData("printing")]
    [InlineData("paused")]
    [InlineData("unknown")]
    public async Task ForceRefreshAsync_NotIdleDoesNotAcquireLeaseOrReadPrinter(string activity)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.ArrangeForceRefresh(activity, Result([], []));

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.NotIdle, result.Status);
        Assert.Equal("waiting_for_confirmed_idle", result.ReasonCode);
        fixture.Leases.Verify(x => x.TryAcquireForRefreshAsync(It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Inventory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForceRefreshAsync_BusyDoesNotReadPrinter()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.ArrangeForceRefresh("ready", Result([], []));
        fixture.Leases.Setup(x => x.TryAcquireForRefreshAsync(fixture.PrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPrinterFileLease?)null);

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.Busy, result.Status);
        Assert.Equal("source_busy", result.ReasonCode);
        fixture.Inventory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PrinterFileInventoryOutcome.Unavailable, PrinterFileInventoryReason.Timeout)]
    [InlineData(PrinterFileInventoryOutcome.Partial, PrinterFileInventoryReason.EntryLimit)]
    [InlineData(PrinterFileInventoryOutcome.Unsupported, PrinterFileInventoryReason.Unsupported)]
    public async Task ForceRefreshAsync_IncompleteScanDoesNotReportCompletion(
        PrinterFileInventoryOutcome outcome, PrinterFileInventoryReason reason)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.ArrangeForceRefresh("ready", new(outcome, reason, [], []));

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.Incomplete, result.Status);
        Assert.Equal(reason.ToString(), result.ReasonCode);
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        PrinterFileScanState state = await db.PrinterFileScanStates.SingleAsync();
        Assert.Null(state.LastCompleteScanAtUtc);
        Assert.NotNull(state.LastAttemptAtUtc);
    }

    [Fact]
    public async Task ForceRefreshAsync_LostFenceDoesNotReportCompletionOrInvalidatePreviews()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.AddAsync("gcodes", "a.gcode");
        fixture.ArrangeForceRefresh("ready", Result([new("gcodes", "a.gcode", 10)], []));
        fixture.Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        PrinterFileForceRefreshResult result = await fixture.Service.ForceRefreshAsync(fixture.PrinterId);

        Assert.Equal(PrinterFileForceRefreshStatus.Superseded, result.Status);
        Assert.Equal("scan_state_changed", result.ReasonCode);
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.Equal(1, (await db.PrinterFileLocations.SingleAsync()).ObservationRevision);
        Assert.Null((await db.PrinterFileScanStates.SingleAsync()).LastCompleteScanAtUtc);
    }

    private static PrinterFileInventoryResult Result(
        ImmutableArray<PrinterFileInventoryEntry> entries, ImmutableArray<PrinterFileInventoryCoverage> coverage) =>
        new(PrinterFileInventoryOutcome.Complete, PrinterFileInventoryReason.None, entries, coverage);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, IDbContextFactory<AppDbContext> factory)
        {
            _connection = connection;
            Factory = factory;
            Lease.SetupGet(x => x.PrinterId).Returns(PrinterId);
            Lease.SetupGet(x => x.ConfigurationGeneration).Returns(1);
            Lease.Setup(x => x.FencePublicationAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Service = new PrinterFileCatalogService(
                factory, Printers.Object, Capabilities.Object, Leases.Object, Status.Object,
                NullLogger<PrinterFileCatalogService>.Instance);
        }

        public Guid PrinterId { get; } = Guid.NewGuid();

        public IDbContextFactory<AppDbContext> Factory { get; }

        public Mock<IPrinterFileLease> Lease { get; } = new();

        public Mock<IPrintersRepository> Printers { get; } = new();

        public Mock<IBackendCapabilityFactory> Capabilities { get; } = new();

        public Mock<IPrinterFileLeaseCoordinator> Leases { get; } = new();

        public Mock<IPrinterStatusCacheReader> Status { get; } = new();

        public Mock<ISupportsFileInventory> Inventory { get; } = new();

        public PrinterFileCatalogService Service { get; }

        public void ArrangeForceRefresh(string activity, PrinterFileInventoryResult result)
        {
            Status.Setup(x => x.GetSnapshot(PrinterId)).Returns(new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(PrinterId, true, activity) { RawActivityState = activity }, DateTime.UtcNow));
            Lease.SetupGet(x => x.Token).Returns(CancellationToken.None);
            Lease.Setup(x => x.IsCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Leases.Setup(x => x.TryAcquireForRefreshAsync(PrinterId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Lease.Object);
            Printers.Setup(x => x.FindByIdWithIncludesAsync(PrinterId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Printer { Id = PrinterId, Name = "Catalog printer", ServerUrl = "http://catalog.invalid" });
            ISupportsFileInventory? client = Inventory.Object;
            Capabilities.Setup(x => x.TryGetFileInventoryClientTyped(It.IsAny<PrinterBackend>(), out client))
                .Returns(true);
            Inventory.Setup(x => x.GetFileInventoryAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(),
                    It.IsAny<PrinterFileInventoryOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }

        public static async Task<Fixture> CreateAsync(IInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            var options = builder.Options;
            var factory = new Mock<IDbContextFactory<AppDbContext>>();
            factory.Setup(x => x.CreateDbContext()).Returns(() => new AppDbContext(options));
            factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AppDbContext(options));
            var fixture = new Fixture(connection, factory.Object);
            await using AppDbContext db = fixture.Factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Catalog test" };
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Catalog test", Manufacturer = manufacturer };
            db.Printers.Add(new Printer
            {
                Id = fixture.PrinterId,
                Name = "Catalog printer",
                ServerUrl = "http://catalog.invalid",
                Manufacturer = manufacturer,
                Model = model,
            });
            db.PrinterFileScanStates.Add(new PrinterFileScanState
            {
                Id = fixture.PrinterId,
                SourcePrinterId = fixture.PrinterId,
                RegisteredPrinterId = fixture.PrinterId,
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task AddAsync(
            string root, string path, PrinterFileAvailability availability = PrinterFileAvailability.Available)
        {
            await using AppDbContext db = Factory.CreateDbContext();
            db.PrinterFileLocations.Add(new PrinterFileLocation
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = PrinterId,
                RegisteredPrinterId = PrinterId,
                StorageRoot = root,
                RelativePath = path,
                IdentityKey = PrinterInventoryPath.ComputeIdentity(root, path),
                SizeBytes = 10,
                FirstSeenAtUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenAtUtc = DateTime.UtcNow.AddDays(-1),
                Availability = availability,
            });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class ScanDateObserver : SaveChangesInterceptor
    {
        public DateTime? PausedDate { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            PausedDate = eventData.Context!.ChangeTracker.Entries<PrinterFileScanState>()
                .Where(x => x.Entity.Outcome is PrinterFileScanOutcome.AuthenticationRequired or PrinterFileScanOutcome.Unsupported)
                .Select(x => (DateTime?)x.Entity.NextScanAtUtc).SingleOrDefault() ?? PausedDate;
            return ValueTask.FromResult(result);
        }
    }
}
