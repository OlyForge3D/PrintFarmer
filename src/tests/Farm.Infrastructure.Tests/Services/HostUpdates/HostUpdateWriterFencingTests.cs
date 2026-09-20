using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Electricity;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Proves the host-update fence coverage extended in issue #2663 beyond the outbox publisher:
/// <see cref="PowerReadingPruneService"/> and <see cref="QueueRetentionPruneService"/> both
/// honor a pending <see cref="IHostUpdateWriterActivityFlag"/> pause request by skipping their
/// delete pass entirely (never opening a DB scope for it) and acknowledging quiescence, rather
/// than racing an in-flight <c>ExecuteDeleteAsync</c> against a coordinated backup/migration.
/// A counting <see cref="IServiceScopeFactory"/> wrapper is used instead of seeding full
/// relational fixtures: what matters here is whether the writer opens a scope to do work at
/// all while paused, not the specific rows it would have deleted (that behavior is already
/// covered by each service's own pre-existing prune tests).
/// </summary>
public class HostUpdateWriterFencingTests : IDisposable
{
    // A paused writer acknowledges once per ~250 ms loop iteration; two observations prove
    // the loop is live and re-checking rather than latched after its first acknowledgement.
    private const int AcknowledgementsPerPausedIteration = 2;

    private readonly SqliteConnection _connection;

    public HostUpdateWriterFencingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Counts how many times a fresh scope was actually opened, without changing behavior.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _scopesOpened;
        private int _scopesDisposed;

        public TaskCompletionSource FirstScopeOpened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstScopeDisposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScopesOpened => Volatile.Read(ref _scopesOpened);
        public int ScopesDisposed => Volatile.Read(ref _scopesDisposed);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _scopesOpened) == 1)
            {
                FirstScopeOpened.TrySetResult();
            }

            return new CountingScope(inner.CreateScope(), () =>
            {
                if (Interlocked.Increment(ref _scopesDisposed) == 1)
                {
                    FirstScopeDisposed.TrySetResult();
                }
            });
        }

        private sealed class CountingScope(IServiceScope inner, Action disposed) : IServiceScope, IAsyncDisposable
        {
            private int _disposeStarted;

            public IServiceProvider ServiceProvider => inner.ServiceProvider;

            public void Dispose()
            {
                inner.Dispose();
                SignalDisposed();
            }

            public async ValueTask DisposeAsync()
            {
                if (inner is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    inner.Dispose();
                }

                SignalDisposed();
            }

            private void SignalDisposed()
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
                {
                    disposed();
                }
            }
        }
    }

    private sealed class BedClearAcknowledgementSpy : IBedClearAcknowledgementService
    {
        public int InvalidationCount { get; private set; }

        public Task<AcknowledgeBedClearResult> AcknowledgeAsync(
            AcknowledgeBedClearRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task InvalidateStaleAcknowledgementsAsync(Guid printerId, CancellationToken ct = default)
        {
            InvalidationCount++;
            return Task.CompletedTask;
        }
    }

    private CountingScopeFactory BuildCountingScopeFactory()
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(_connection));
        ServiceProvider sp = services.BuildServiceProvider();
        return new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
    }

    private CountingScopeFactory BuildCountingScopeFactory(BedClearAcknowledgementSpy spy)
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(_connection));
        _ = services.AddScoped<IBedClearAcknowledgementService>(_ => spy);
        ServiceProvider sp = services.BuildServiceProvider();
        return new CountingScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task WaitForPauseAcknowledgementsAsync(
        IHostUpdateWriterActivityFlag fence,
        Func<int> acknowledgementCount,
        int expectedAcknowledgements)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await fence.IsPausedAsync(timeout.Token) || acknowledgementCount() < expectedAcknowledgements)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            int observed = acknowledgementCount();
            bool paused = await fence.IsPausedAsync(CancellationToken.None);
            int missing = Math.Max(0, expectedAcknowledgements - observed);
            throw new Xunit.Sdk.XunitException(
                $"Writer pause wait timed out: IsPausedAsync={paused}, observed {observed} " +
                $"acknowledgement(s), expected {expectedAcknowledgements}, missing {missing} " +
                "within 10 seconds.");
        }
    }

    private static async Task WaitForIntervalBoundaryAcknowledgementAsync(
        IHostUpdateWriterActivityFlag fence,
        Func<int> acknowledgementCount,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (!await fence.IsPausedAsync(cancellation.Token) || acknowledgementCount() < 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
            }
        }

        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            int observed = acknowledgementCount();
            bool paused = await fence.IsPausedAsync(CancellationToken.None);
            throw new Xunit.Sdk.XunitException(
                $"Interval-boundary pause acknowledgement timed out after {timeout}: " +
                $"IsPausedAsync={paused}, observed {observed} acknowledgement(s). " +
                "The pause must be observed during the interruptible interval wait.");
        }
    }

    private static async Task WaitForScopeDisposalsAsync(
        CountingScopeFactory scopeFactory,
        int expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (scopeFactory.ScopesDisposed < expected)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected {expected} completed work scope(s) within {timeout}, " +
                $"but observed {scopeFactory.ScopesDisposed}.");
        }
    }

    [Fact]
    public async Task PowerReadingPruneService_WhilePauseRequested_NeverOpensScope()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new PowerReadingPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            AcknowledgementsPerPausedIteration + 1);
        await sut.StopAsync(CancellationToken.None);

        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
    }

    [Fact]
    public async Task PowerReadingPruneService_WithoutPauseRequested_OpensScopeNormally()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new PowerReadingPruneFenceFlag();

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(CancellationToken.None);

        scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
    }

    [Fact]
    public async Task QueueRetentionPruneService_WhilePauseRequested_NeverRunsOncePass()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueRetentionPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var settings = new QueueRetentionSettings { PruneInterval = TimeSpan.FromHours(6) };
        var sut = new QueueRetentionPruneService(
            scopeFactory,
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            AcknowledgementsPerPausedIteration + 1);
        await sut.StopAsync(CancellationToken.None);

        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
    }

    [Fact]
    public async Task QueueRetentionPruneService_WithoutPauseRequested_RunsOncePassNormally()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueRetentionPruneFenceFlag();

        var settings = new QueueRetentionSettings { PruneInterval = TimeSpan.FromHours(6) };
        var sut = new QueueRetentionPruneService(
            scopeFactory,
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(CancellationToken.None);

        scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
    }

    [Fact]
    public async Task BackendStartCommandConsumerService_WhilePauseRequested_AcknowledgesWithoutOpeningScope()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new BackendStartCommandConsumerFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new BackendStartCommandConsumerService(
            scopeFactory,
            NullLogger<BackendStartCommandConsumerService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            expectedAcknowledgements: AcknowledgementsPerPausedIteration);
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not start queue work while paused");
    }

    [Fact]
    public async Task BackendStartCommandConsumerService_PauseDuringInterval_AcknowledgesBeforePollInterval()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new BackendStartCommandConsumerFenceFlag();
        var sut = new BackendStartCommandConsumerService(
            scopeFactory,
            NullLogger<BackendStartCommandConsumerService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await WaitForScopeDisposalsAsync(scopeFactory, 2, TimeSpan.FromSeconds(10));
        int scopesBeforePause = scopeFactory.ScopesOpened;
        await fence.RequestPauseAsync(CancellationToken.None);

        await WaitForIntervalBoundaryAcknowledgementAsync(
            fence,
            () => fence.AcknowledgementCount,
            TimeSpan.FromSeconds(2));
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
        scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
            "the interval-boundary acknowledgement must stop the next work pass before it opens another scope");
    }

    [Fact]
    public async Task BackendControlCommandConsumerService_WhilePauseRequested_AcknowledgesWithoutOpeningScope()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new BackendControlCommandConsumerFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new BackendControlCommandConsumerService(
            scopeFactory,
            NullLogger<BackendControlCommandConsumerService>.Instance,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            expectedAcknowledgements: AcknowledgementsPerPausedIteration);
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
        scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not start queue work while paused");
    }

    [Fact]
    public async Task BackendControlCommandConsumerService_PauseDuringInterval_AcknowledgesBeforePollInterval()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new BackendControlCommandConsumerFenceFlag();
        var sut = new BackendControlCommandConsumerService(
            scopeFactory,
            NullLogger<BackendControlCommandConsumerService>.Instance,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await WaitForScopeDisposalsAsync(scopeFactory, 2, TimeSpan.FromSeconds(10));
        int scopesBeforePause = scopeFactory.ScopesOpened;
        await fence.RequestPauseAsync(CancellationToken.None);

        await WaitForIntervalBoundaryAcknowledgementAsync(
            fence,
            () => fence.AcknowledgementCount,
            TimeSpan.FromSeconds(2));
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
        scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
            "the interval-boundary acknowledgement must stop the next work pass before it opens another scope");
    }

    [Fact]
    public async Task BedClearAcknowledgementExpiryService_WhilePauseRequested_DoesNotDelegateWrites()
    {
        var spy = new BedClearAcknowledgementSpy();
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(spy);
        using (AppDbContext seedDb = new(
                   new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options))
        {
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Test manufacturer" });
            seedDb.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test model",
                ManufacturerId = manufacturerId,
            });
            Printer printer = new PrinterBuilder()
                .WithId(Guid.NewGuid())
                .WithName("Test printer")
                .WithServerUrl("http://192.168.1.2")
                .Build();
            printer.ManufacturerId = manufacturerId;
            printer.ModelId = modelId;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printer.Id,
                AcknowledgedJobId = Guid.NewGuid(),
                AcknowledgedAtUtc = DateTime.UtcNow,
            };
            seedDb.Printers.Add(printer);
            await seedDb.SaveChangesAsync();
        }

        using var positiveMetrics = new BedClearAcknowledgementExpiryMetrics();
        var unpausedSut = new BedClearAcknowledgementExpiryService(
            scopeFactory,
            NullLogger<BedClearAcknowledgementExpiryService>.Instance,
            positiveMetrics,
            new BedClearAcknowledgementExpiryFenceFlag());
        await unpausedSut.ScanAsync(CancellationToken.None);
        spy.InvalidationCount.Should().Be(1, "the positive control must reach the delegated acknowledgement writer");

        var fence = new BedClearAcknowledgementExpiryFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);
        using var metrics = new BedClearAcknowledgementExpiryMetrics();

        var sut = new BedClearAcknowledgementExpiryService(
            scopeFactory,
            NullLogger<BedClearAcknowledgementExpiryService>.Instance,
            metrics,
            fence);
        await sut.StartAsync(CancellationToken.None);
        await WaitForPauseAcknowledgementsAsync(
            fence,
            () => fence.AcknowledgementCount,
            expectedAcknowledgements: AcknowledgementsPerPausedIteration);
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
        spy.InvalidationCount.Should().Be(1, "the paused scanner must not delegate acknowledgement writes");
        scopeFactory.ScopesOpened.Should().Be(1, "the positive control opens the only scanner scope; the paused scanner must not open another");
    }

    [Fact]
    public async Task BedClearAcknowledgementExpiryService_PauseDuringInterval_AcknowledgesBeforeScanInterval()
    {
        var spy = new BedClearAcknowledgementSpy();
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(spy);
        using (AppDbContext seedDb = new(
                   new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options))
        {
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Test manufacturer" });
            seedDb.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "Test model",
                ManufacturerId = manufacturerId,
            });
            Printer printer = new PrinterBuilder()
                .WithId(Guid.NewGuid())
                .WithName("Test printer")
                .WithServerUrl("http://192.168.1.3")
                .Build();
            printer.ManufacturerId = manufacturerId;
            printer.ModelId = modelId;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printer.Id,
                AcknowledgedJobId = Guid.NewGuid(),
                AcknowledgedAtUtc = DateTime.UtcNow,
            };
            seedDb.Printers.Add(printer);
            await seedDb.SaveChangesAsync();
        }

        using var metrics = new BedClearAcknowledgementExpiryMetrics();
        var fence = new BedClearAcknowledgementExpiryFenceFlag();
        var sut = new BedClearAcknowledgementExpiryService(
            scopeFactory,
            NullLogger<BedClearAcknowledgementExpiryService>.Instance,
            metrics,
            fence);

        await sut.StartAsync(CancellationToken.None);
        await scopeFactory.FirstScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int scopesBeforePause = scopeFactory.ScopesOpened;
        int writesBeforePause = spy.InvalidationCount;
        writesBeforePause.Should().Be(1, "the unpaused scan must complete before pausing the interval");
        await fence.RequestPauseAsync(CancellationToken.None);

        await WaitForIntervalBoundaryAcknowledgementAsync(
            fence,
            () => fence.AcknowledgementCount,
            TimeSpan.FromSeconds(2));
        await sut.StopAsync(CancellationToken.None);

        fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
        scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
            "the interval-boundary acknowledgement must stop the next scan before it opens another scope");
        spy.InvalidationCount.Should().Be(writesBeforePause,
            "the interval-boundary acknowledgement must precede any subsequent delegated write");
    }
}
