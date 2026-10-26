using System.Collections.Immutable;
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
                factory, Mock.Of<IPrintersRepository>(), Mock.Of<IBackendCapabilityFactory>(),
                Mock.Of<IPrinterFileLeaseCoordinator>(), Mock.Of<IPrinterStatusCacheReader>(),
                NullLogger<PrinterFileCatalogService>.Instance);
        }

        public Guid PrinterId { get; } = Guid.NewGuid();

        public IDbContextFactory<AppDbContext> Factory { get; }

        public Mock<IPrinterFileLease> Lease { get; } = new();

        public PrinterFileCatalogService Service { get; }

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
