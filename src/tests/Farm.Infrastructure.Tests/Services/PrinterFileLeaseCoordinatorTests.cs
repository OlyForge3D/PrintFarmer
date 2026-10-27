using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services;

public sealed class PrinterFileLeaseCoordinatorTests
{
    [Fact]
    public async Task RenewalAsync_UnexpectedFailureCancelsOwnershipAndDisposeDoesNotMaskOutcome()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(lease);
        Mock.Get(fixture.Factory).Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated unexpected context factory failure"));
        TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = lease.Token.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(lease.Token.IsCancellationRequested);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_SharesFiniteSlotsAndExcludesSamePrinter()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await using IPrinterFileLease? first = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(first);
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, true));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.Second, true));
        await first.DisposeAsync();
        await using IPrinterFileLease? second = await fixture.Coordinator.TryAcquireAsync(fixture.Second, true);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task AcquireAsync_DiscoveryDisableDoesNotDisableExplicitCopy()
    {
        await using var fixture = await Fixture.CreateAsync(2, enabled: false);
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(lease);
        Assert.True(await lease.IsCurrentAsync());
    }

    [Fact]
    public async Task FenceAsync_ConfigurationChangeRejectsLatePublication()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(lease);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            Printer printer = await db.Printers.SingleAsync(x => x.Id == fixture.First);
            printer.ServerUrl = "http://changed.invalid";
            await db.SaveChangesAsync();
        }

        Assert.False(await lease.IsCurrentAsync());
        await using AppDbContext publication = fixture.Factory.CreateDbContext();
        await using var transaction = await publication.Database.BeginTransactionAsync();
        Assert.False(await lease.FencePublicationAsync(publication));
    }

    [Fact]
    public async Task ForceRefreshAsync_BypassesDisabledDiscoveryAndBackoffButSharesExclusiveSlots()
    {
        await using var fixture = await Fixture.CreateAsync(1, enabled: false);
        fixture.SetIdle(fixture.First);
        fixture.SetIdle(fixture.Second);
        await using (IPrinterFileLease? enrollment = await fixture.Coordinator.TryAcquireAsync(fixture.First, true))
        {
            Assert.NotNull(enrollment);
        }
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            await db.PrinterFileScanStates.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.NextScanAtUtc, DateTime.UtcNow.AddYears(1))
                .SetProperty(x => x.InteractiveRequestedAtUtc, DateTime.UtcNow)
                .SetProperty(x => x.ConsecutiveFailures, 5));
        }

        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.First);
        Assert.NotNull(lease);
        Assert.True(await lease.IsCurrentAsync());
        Assert.Null(await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.First));
        Assert.Null(await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.Second));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.Second, true));
        await lease.DisposeAsync();
        await using IPrinterFileLease? next = await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.Second);
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData("printing")]
    [InlineData("paused")]
    [InlineData("mystery")]
    public async Task ForceRefreshAsync_RejectsNonIdleAcquisitionAndPublication(string activity)
    {
        await using var fixture = await Fixture.CreateAsync(1, enabled: false);
        fixture.SetActivity(fixture.First, activity);
        Assert.Null(await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.First));
        fixture.SetIdle(fixture.First);
        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.First);
        Assert.NotNull(lease);
        fixture.SetActivity(fixture.First, activity);
        Assert.False(await lease.IsCurrentAsync());
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.False(await lease.FencePublicationAsync(db));
    }

    [Fact]
    public async Task ForceRefreshAsync_IdleLossCancelsRenewableOwnership()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        fixture.SetIdle(fixture.First);
        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.First);
        Assert.NotNull(lease);
        TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = lease.Token.Register(() => lost.TrySetResult());
        fixture.SetActivity(fixture.First, "printing");
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AcquireAsync_ExpiredOwnerCannotPublishOrReleaseReplacement()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        IPrinterFileLease? old = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(old);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            await db.PrinterFileScanStates.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            await db.PrinterFileIoSlots.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }

        await using IPrinterFileLease? replacement = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(replacement);
        Assert.True(replacement.Fence > old.Fence);
        Assert.False(await old.IsCurrentAsync());
        await old.DisposeAsync();
        Assert.True(await replacement.IsCurrentAsync());
    }

    [Fact]
    public async Task FenceAsync_PerPrinterDiscoveryDisablePreservesInteractiveAuthority()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, true);
        Assert.NotNull(lease);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            await db.PrinterFileScanStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        }

        await using AppDbContext publication = fixture.Factory.CreateDbContext();
        await using var transaction = await publication.Database.BeginTransactionAsync();
        Assert.True(await lease.FencePublicationAsync(publication));
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task AcquireAsync_UnknownRawActivityNeverCountsAsIdle()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        fixture.Status.Setup(x => x.GetSnapshot(fixture.First)).Returns(new PrinterStatusCacheSnapshot(
            new PrinterStatusDto(fixture.First, true, "Idle") { RawActivityState = "mystery" }, DateTime.UtcNow));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        await db.PrinterFileScanStates.ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.NextScanAtUtc, DateTime.UtcNow.AddDays(-1)));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        Assert.Empty(await db.PrinterFileIoSlots.ToArrayAsync());
    }

    [Fact]
    public async Task AcquireAsync_BusyDeferralMovesDueTimeWithoutRefreshingObservation()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await fixture.Coordinator.TryAcquireAsync(fixture.First, false);
        await using AppDbContext db = fixture.Factory.CreateDbContext();
        DateTime previousSeen = DateTime.UtcNow.AddHours(-1);
        await db.PrinterFileScanStates.ExecuteUpdateAsync(update => update
            .SetProperty(x => x.NextScanAtUtc, DateTime.UtcNow.AddMinutes(-1))
            .SetProperty(x => x.LastCompleteScanAtUtc, previousSeen));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        PrinterFileScanState deferred = await db.PrinterFileScanStates.AsNoTracking().SingleAsync();
        Assert.True(deferred.NextScanAtUtc > DateTime.UtcNow);
        Assert.Equal("waiting_for_confirmed_idle", deferred.ReasonCode);
        Assert.Equal(previousSeen, deferred.LastCompleteScanAtUtc);
        Assert.Empty(await db.PrinterFileIoSlots.ToArrayAsync());
    }

    [Fact]
    public async Task FenceAsync_DisablingBackgroundIndexRejectsScanButKeepsOwnedBytes()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        fixture.Status.Setup(x => x.GetSnapshot(fixture.First)).Returns(new PrinterStatusCacheSnapshot(
            new PrinterStatusDto(fixture.First, true, "Idle") { RawActivityState = "ready" }, DateTime.UtcNow));
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            await db.PrinterFileScanStates.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.NextScanAtUtc, DateTime.UtcNow.AddDays(-1)));
        }

        await using IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, false);
        Assert.NotNull(lease);
        Assert.True(await lease.IsCurrentAsync());
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            AppSettingsEntity policy = await db.AppSettingsEntities.SingleAsync();
            policy.SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings { Enabled = false });
            await db.SaveChangesAsync();
        }

        await using AppDbContext publication = fixture.Factory.CreateDbContext();
        await using var transaction = await publication.Database.BeginTransactionAsync();
        Assert.False(await lease.FencePublicationAsync(publication));
    }

    [Fact]
    public async Task AcquireAsync_DueEnrichmentClaimsIdleBackgroundLeaseBeforeNextScan()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await fixture.PrepareFutureScanAsync();
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));

        PrinterFileLocation location = await fixture.AddLocationAsync(fixture.First);
        await using (IPrinterFileLease? lease = await fixture.Coordinator.TryAcquireAsync(fixture.First, false))
        {
            Assert.NotNull(lease);
            Assert.True(await lease.IsCurrentAsync());
        }

        await using AppDbContext db = fixture.Factory.CreateDbContext();
        Assert.True((await db.PrinterFileScanStates.AsNoTracking().SingleAsync()).NextScanAtUtc > DateTime.UtcNow);
        db.PrinterFileEnrichments.Add(new PrinterFileEnrichment
        {
            LocationId = location.Id,
            SourceIdentity = PrinterFileEnrichmentIdentity.For(location),
            ExtractorVersion = PrinterFileEnrichmentLimits.ExtractorVersion,
            MetadataState = PrinterFileEnrichmentState.Ready,
            ThumbnailState = PrinterFileEnrichmentState.Absent,
            NextAttemptAtUtc = DateTime.UtcNow.AddHours(24),
        });
        await db.SaveChangesAsync();
        await fixture.PostponeScanAsync();
        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("printer")]
    [InlineData("busy")]
    [InlineData("interactive")]
    [InlineData("generation")]
    public async Task AcquireAsync_DueEnrichmentNeverBypassesBackgroundEligibility(string blocker)
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await fixture.PrepareFutureScanAsync();
        PrinterFileLocation location = await fixture.AddLocationAsync(fixture.First);
        await using (AppDbContext db = fixture.Factory.CreateDbContext())
        {
            switch (blocker)
            {
                case "settings":
                    AppSettingsEntity policy = await db.AppSettingsEntities.SingleAsync();
                    policy.SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings
                    {
                        Enabled = false,
                        MaxConcurrentIo = 2,
                    });
                    await db.SaveChangesAsync();
                    break;
                case "printer":
                    await db.PrinterFileScanStates.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
                    break;
                case "busy":
                    fixture.SetActivity(fixture.First, "printing");
                    break;
                case "interactive":
                    await db.PrinterFileScanStates.ExecuteUpdateAsync(s =>
                        s.SetProperty(x => x.InteractiveRequestedAtUtc, DateTime.UtcNow));
                    break;
                default:
                    await db.PrinterFileLocations.Where(x => x.Id == location.Id).ExecuteUpdateAsync(s =>
                        s.SetProperty(x => x.ConfigurationGeneration, x => x.ConfigurationGeneration + 1));
                    break;
            }
        }

        Assert.Null(await fixture.Coordinator.TryAcquireAsync(fixture.First, false));
        await using AppDbContext check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.PrinterFileIoSlots.ToArrayAsync());
        Assert.Null((await check.PrinterFileScanStates.AsNoTracking().SingleAsync()).LeaseOwner);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    public void NextDelay_IncludesJitterInsideSixHourCap(int failures)
    {
        TimeSpan delay = PrinterFileIndexPolicy.NextDelay(
            Guid.NewGuid(), failures, new PrinterFileIndexSettings { ScanIntervalMinutes = 1440 });
        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromHours(6));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, IDbContextFactory<AppDbContext> factory)
        {
            _connection = connection;
            Factory = factory;
            Coordinator = new PrinterFileLeaseCoordinator(factory, Status.Object,
                NullLogger<PrinterFileLeaseCoordinator>.Instance);
        }

        public Guid First { get; } = Guid.NewGuid();

        public Guid Second { get; } = Guid.NewGuid();

        public IDbContextFactory<AppDbContext> Factory { get; }

        public Mock<IPrinterStatusCacheReader> Status { get; } = new();

        public PrinterFileLeaseCoordinator Coordinator { get; }

        public void SetIdle(Guid printerId) => SetActivity(printerId, "ready");

        public void SetActivity(Guid printerId, string activity) =>
            Status.Setup(x => x.GetSnapshot(printerId)).Returns(new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(printerId, true, activity) { RawActivityState = activity }, DateTime.UtcNow));

        /// <summary>Enrolls the first printer as idle with its next inventory scan an hour away.</summary>
        public async Task PrepareFutureScanAsync()
        {
            SetIdle(First);
            await using AppDbContext db = Factory.CreateDbContext();
            Printer printer = await db.Printers.AsNoTracking().SingleAsync(x => x.Id == First);
            db.PrinterFileScanStates.Add(new PrinterFileScanState
            {
                Id = First,
                SourcePrinterId = First,
                RegisteredPrinterId = First,
                ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer),
                NextScanAtUtc = DateTime.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        public async Task PostponeScanAsync()
        {
            await using AppDbContext db = Factory.CreateDbContext();
            await db.PrinterFileScanStates.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.NextScanAtUtc, DateTime.UtcNow.AddHours(1)));
        }

        public async Task<PrinterFileLocation> AddLocationAsync(Guid printerId)
        {
            await using AppDbContext db = Factory.CreateDbContext();
            PrinterFileScanState state = await db.PrinterFileScanStates.AsNoTracking()
                .SingleAsync(x => x.SourcePrinterId == printerId);
            PrinterFileLocation location = new()
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = printerId,
                RegisteredPrinterId = printerId,
                StorageRoot = "gcodes",
                RelativePath = "part.gcode",
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "part.gcode"),
                DisplayName = "part.gcode",
                SizeBytes = 42,
                ConfigurationGeneration = state.ConfigurationGeneration,
                MutationGeneration = state.MutationGeneration,
                Availability = PrinterFileAvailability.Available,
            };
            db.PrinterFileLocations.Add(location);
            await db.SaveChangesAsync();
            return location;
        }

        public static async Task<Fixture> CreateAsync(int slots, bool enabled = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new Mock<IDbContextFactory<AppDbContext>>();
            factory.Setup(x => x.CreateDbContext()).Returns(() => new AppDbContext(options));
            factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new AppDbContext(options));
            var fixture = new Fixture(connection, factory.Object);
            await using AppDbContext db = factory.Object.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Lease test" };
            var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Lease test", Manufacturer = manufacturer };
            db.Printers.AddRange(
                new Printer
                {
                    Id = fixture.First,
                    Name = "One",
                    ServerUrl = "http://one.invalid",
                    Manufacturer = manufacturer,
                    Model = model,
                    IsEnabled = true,
                },
                new Printer
                {
                    Id = fixture.Second,
                    Name = "Two",
                    ServerUrl = "http://two.invalid",
                    Manufacturer = manufacturer,
                    Model = model,
                    IsEnabled = true,
                });
            db.AppSettingsEntities.Add(new AppSettingsEntity
            {
                Key = PrinterFileIndexSettings.SectionName,
                SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings
                {
                    Enabled = enabled,
                    MaxConcurrentIo = slots,
                }),
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
