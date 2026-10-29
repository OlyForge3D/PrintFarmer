using System.Data.Common;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Xunit.Abstractions;

namespace Farm.Infrastructure.Tests.Services;

[Collection(ProviderDatabaseTestCollection.Name)]
[Trait("Category", "DbHeavy")]
public sealed class PrinterFileLeaseProviderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AcquireForRefreshAsync_PostgresSerializationConflict_RetriesWithoutLeakingOwnership()
    {
        var barrier = new SlotReadBarrier();
        await using Fixture fixture = await Fixture.CreateAsync(2, 2, barrier);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        IPrinterFileLease?[] leases = new IPrinterFileLease?[2];
        try
        {
            await Task.WhenAll(Enumerable.Range(0, leases.Length).Select(async index =>
            {
                leases[index] = await fixture.Coordinator.TryAcquireForRefreshAsync(fixture.Printers[index], timeout.Token);
            }));

            Assert.All(leases, lease => Assert.NotNull(lease));
            Assert.Equal(2, leases.Select(lease => lease!.OwnerId).Distinct().Count());
            Assert.True(fixture.SerializationConflicts > 0, "The synchronized transactions must produce a real PostgreSQL 40001.");
            await using AppDbContext db = fixture.CreateContext();
            foreach (IPrinterFileLease? lease in leases)
            {
                Assert.NotNull(lease);
                Assert.True(await lease.IsCurrentAsync(timeout.Token));
                PrinterFileScanState state = await db.PrinterFileScanStates.SingleAsync(x => x.SourcePrinterId == lease.PrinterId, timeout.Token);
                PrinterFileIoSlot slot = await db.PrinterFileIoSlots.SingleAsync(x => x.SourcePrinterId == lease.PrinterId, timeout.Token);
                Assert.Equal(lease.OwnerId, state.LeaseOwner);
                Assert.Equal(lease.OwnerId, slot.LeaseOwner);
                Assert.Equal(1, state.LeaseFence);
                Assert.Equal(0, state.ConsecutiveFailures);
            }

            output.WriteLine("Real PostgreSQL serialization conflicts recovered: {0}; concurrent owners: 2.", fixture.SerializationConflicts);
        }
        finally
        {
            foreach (IPrinterFileLease? lease in leases)
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
            }
        }

        await fixture.AssertReleasedAsync(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task AcquireForRefreshAsync_PostgresConcurrentPrinters_UsesCapacityWithoutOversubscription(int capacity)
    {
        await using Fixture fixture = await Fixture.CreateAsync(capacity, 32);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource capacityReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        object sync = new();
        HashSet<Guid> activePrinters = [];
        int peak = 0;
        int completed = 0;

        async Task AcquireAndReleaseAsync(Guid printerId)
        {
            await start.Task.WaitAsync(timeout.Token);
            IPrinterFileLease? lease;
            do
            {
                lease = await fixture.Coordinator.TryAcquireForRefreshAsync(printerId, timeout.Token);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
                }
            }
            while (lease is null);

            await using (lease)
            {
                lock (sync)
                {
                    Assert.True(activePrinters.Add(printerId), "One printer must never have two concurrent owners.");
                    Assert.InRange(activePrinters.Count, 1, capacity);
                    peak = Math.Max(peak, activePrinters.Count);
                    if (peak == capacity)
                    {
                        capacityReached.TrySetResult();
                    }
                }

                try
                {
                    // Hold the first wave until every configured slot is occupied; sequential success is insufficient.
                    await capacityReached.Task.WaitAsync(timeout.Token);
                    Assert.True(await lease.IsCurrentAsync(timeout.Token));
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
                }
                finally
                {
                    lock (sync)
                    {
                        Assert.True(activePrinters.Remove(printerId));
                    }
                }
            }

            Interlocked.Increment(ref completed);
        }

        Task[] contenders = fixture.Printers.SelectMany(id => new[] { id, id }).Select(AcquireAndReleaseAsync).ToArray();
        start.SetResult();
        try
        {
            await Task.WhenAll(contenders);
        }
        finally
        {
            output.WriteLine("Configured capacity: {0}; observed peak: {1}; completed leases: {2}; recovered PostgreSQL 40001 conflicts: {3}.",
                capacity, peak, completed, fixture.SerializationConflicts);
        }

        Assert.Equal(capacity, peak);
        Assert.Equal(64, completed);
        Assert.Empty(activePrinters);
        await fixture.AssertReleasedAsync(2);
    }

    private sealed class SlotReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM \"PrinterFileIoSlots\"", StringComparison.Ordinal))
            {
                int arrival = Interlocked.Increment(ref _arrivals);
                if (arrival == 2)
                {
                    _bothRead.TrySetResult();
                }

                if (arrival <= 2)
                {
                    await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed class Fixture(DbContextOptions<AppDbContext> options) : IAsyncDisposable
    {
        public Guid[] Printers { get; private set; } = [];

        public Mock<ILogger<PrinterFileLeaseCoordinator>> Logger { get; } = new();

        public PrinterFileLeaseCoordinator Coordinator { get; private set; } = null!;

        public int SerializationConflicts => Logger.Invocations
            .Where(call => call.Method.Name == nameof(ILogger.Log))
            .Select(call => call.Arguments[3])
            .OfType<Exception>()
            .Select(exception => exception.GetBaseException())
            .OfType<PostgresException>()
            .Count(exception => exception.SqlState == PostgresErrorCodes.SerializationFailure);

        public AppDbContext CreateContext() => new(options);

        public static async Task<Fixture> CreateAsync(int capacity, int printerCount, DbCommandInterceptor? interceptor = null)
        {
            string? configured = Environment.GetEnvironmentVariable("PFARM_TEST_POSTGRES_CONN");
            Assert.False(string.IsNullOrWhiteSpace(configured),
                "Native PostgreSQL lease verification did not run: set PFARM_TEST_POSTGRES_CONN to an isolated test server.");
            var connection = new NpgsqlConnectionStringBuilder(configured)
            {
                Database = $"pfarm_lease_{Guid.NewGuid():N}",
            };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connection.ConnectionString, provider => provider.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
                .Options;
            var fixture = new Fixture(options);
            try
            {
                await using AppDbContext db = fixture.CreateContext();
                await db.Database.MigrateAsync();
                var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Native lease test" };
                var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Native lease test", Manufacturer = manufacturer };
                Printer[] printers = Enumerable.Range(0, printerCount).Select(index => new Printer
                {
                    Id = Guid.NewGuid(),
                    Name = $"Printer {index}",
                    ServerUrl = $"http://lease-{index}.invalid",
                    Manufacturer = manufacturer,
                    Model = model,
                    IsEnabled = true,
                }).ToArray();
                db.Printers.AddRange(printers);
                db.PrinterFileScanStates.AddRange(printers.Select(printer => new PrinterFileScanState
                {
                    Id = printer.Id,
                    SourcePrinterId = printer.Id,
                    RegisteredPrinterId = printer.Id,
                    ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer),
                    NextScanAtUtc = DateTime.UtcNow,
                }));
                db.PrinterFileIoSlots.AddRange(Enumerable.Range(1, capacity).Select(id => new PrinterFileIoSlot { Id = id }));
                db.AppSettingsEntities.Add(new AppSettingsEntity
                {
                    Key = PrinterFileIndexSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(new PrinterFileIndexSettings { MaxConcurrentIo = capacity }),
                });
                await db.SaveChangesAsync();
                fixture.Printers = printers.Select(printer => printer.Id).ToArray();
                var leaseOptions = new DbContextOptionsBuilder<AppDbContext>(options);
                if (interceptor is not null)
                {
                    leaseOptions.AddInterceptors(interceptor);
                }

                var factory = new Mock<IDbContextFactory<AppDbContext>>();
                factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new AppDbContext(leaseOptions.Options));
                var status = new Mock<IPrinterStatusCacheReader>();
                DateTime observedAtUtc = DateTime.UtcNow;
                status.Setup(x => x.GetSnapshot(It.IsAny<Guid>())).Returns((Guid printerId) =>
                    new PrinterStatusCacheSnapshot(
                        new PrinterStatusDto(printerId, true, "ready") { RawActivityState = "ready" }, observedAtUtc));
                fixture.Coordinator = new PrinterFileLeaseCoordinator(factory.Object, status.Object, fixture.Logger.Object);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task AssertReleasedAsync(int expectedFence)
        {
            await using AppDbContext db = CreateContext();
            PrinterFileScanState[] states = await db.PrinterFileScanStates.ToArrayAsync();
            Assert.Equal(Printers.Length, states.Length);
            Assert.All(states, state =>
            {
                Assert.Null(state.LeaseOwner);
                Assert.Null(state.LeaseExpiresAtUtc);
                Assert.Equal(expectedFence, state.LeaseFence);
                Assert.Equal(0, state.ConsecutiveFailures);
                Assert.Equal(PrinterFileScanOutcome.NeverScanned, state.Outcome);
            });
            Assert.All(await db.PrinterFileIoSlots.ToArrayAsync(), slot =>
            {
                Assert.Null(slot.LeaseOwner);
                Assert.Null(slot.LeaseExpiresAtUtc);
            });
        }

        public async ValueTask DisposeAsync()
        {
            await using AppDbContext db = CreateContext();
            // The factory always replaces the configured database name with a unique test-owned name.
            await db.Database.EnsureDeletedAsync();
        }
    }
}
