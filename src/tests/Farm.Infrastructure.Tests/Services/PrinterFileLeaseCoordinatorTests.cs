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
