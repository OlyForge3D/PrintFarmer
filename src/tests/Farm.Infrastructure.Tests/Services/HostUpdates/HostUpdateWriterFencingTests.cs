using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.ExceptionServices;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    private CountingScopeFactory BuildCountingScopeFactory(params IInterceptor[] interceptors)
    {
        ServiceCollection services = new();
        _ = services.AddDbContext<AppDbContext>(
            builder => builder
                .UseSqlite(_connection)
                .AddInterceptors(interceptors));
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

    private static async Task RunHostedServiceAsync(
        IHostedService service,
        Func<Task> testBody,
        TimeSpan? stopTimeout = null)
    {
        Exception? testFailure = null;
        Exception? cleanupFailure = null;
        bool started = false;
        try
        {
            await service.StartAsync(CancellationToken.None);
            started = true;
            await testBody();
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            if (started)
            {
                try
                {
                    await service.StopAsync(CancellationToken.None)
                        .WaitAsync(stopTimeout ?? TimeSpan.FromSeconds(10));
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (service is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
        }

        if (testFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                testFailure.Data["HostedServiceCleanupFailure"] = cleanupFailure.ToString();
            }

            ExceptionDispatchInfo.Capture(testFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"Hosted service cleanup did not complete within " +
                $"{stopTimeout ?? TimeSpan.FromSeconds(10)}: {cleanupFailure.Message}");
        }
    }

    [Fact]
    public async Task RunHostedServiceAsync_AssertionAndCleanupTimeout_PreservesAssertionFailure()
    {
        var service = new BlockingStopHostedService();

        Xunit.Sdk.XunitException failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(
            () => RunHostedServiceAsync(
                service,
                () => throw new Xunit.Sdk.XunitException("original assertion"),
                TimeSpan.FromMilliseconds(50)));

        failure.Message.Should().Be("original assertion");
        failure.Data.Contains("HostedServiceCleanupFailure").Should().BeTrue();
        service.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task RunHostedServiceAsync_BackgroundServiceIgnoresCancellation_ReportsCleanupTimeout()
    {
        var service = new IgnoringCancellationBackgroundService();

        Xunit.Sdk.XunitException failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(
            () => RunHostedServiceAsync(
                service,
                () =>
                {
                    service.WaitUntilStarted(TimeSpan.FromSeconds(1)).Should().BeTrue();
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(50)));

        failure.Message.Should().Contain("cleanup did not complete within");
        service.Disposed.Should().BeTrue();
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
        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => fence.AcknowledgementCount,
                AcknowledgementsPerPausedIteration + 1);

            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
        });
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

        await RunHostedServiceAsync(sut, async () =>
        {
            await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
        });
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
        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => fence.AcknowledgementCount,
                AcknowledgementsPerPausedIteration + 1);

            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
        });
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

        await RunHostedServiceAsync(sut, async () =>
        {
            await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
        });
    }

    [Fact]
    public async Task QueueReconciliationFence_AfterProcessRestart_RejectsReconciliation()
    {
        string root = Directory.CreateTempSubdirectory("pf-host-update-restart-fence-").FullName;
        try
        {
            HostUpdateExecutionOptions options = new() { RootDirectory = root };
            await new FileHostUpdateAdmissionGate(options).CloseAsync(CancellationToken.None);

            CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
            var restartedFence = new QueueReconciliationFenceFlag(new FileHostUpdateAdmissionGate(options));
            var service = new QueueReconciliationService(
                scopeFactory,
                NullLogger<QueueReconciliationService>.Instance,
                restartedFence);

            await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

            (await restartedFence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "a restarted reconciler must not touch queue state while the durable fence is closed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueReconciliationFence_WhenDurableMarkerIsCorrupt_RefusesReconciliation()
    {
        string root = Directory.CreateTempSubdirectory("pf-host-update-corrupt-fence-").FullName;
        try
        {
            HostUpdateExecutionOptions options = new() { RootDirectory = root };
            Directory.CreateDirectory(options.StateDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.StateDirectory, "admission.closed"), "{truncated");

            CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
            var fence = new QueueReconciliationFenceFlag(new FileHostUpdateAdmissionGate(options));
            var service = new QueueReconciliationService(
                scopeFactory,
                NullLogger<QueueReconciliationService>.Instance,
                fence);

            await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "an unreadable durable fence must block reconciliation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueReconciliationService_PauseDuringInterval_AcknowledgesWithinFenceWindow()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new QueueReconciliationFenceFlag();
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);

        await RunHostedServiceAsync(service, async () =>
        {
            await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fence.RequestPauseAsync(CancellationToken.None);
            await WaitForPauseAcknowledgementsAsync(fence, () => fence.AcknowledgementCount, 1);

            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        });
    }

    [Fact]
    public async Task QueueReconciliationService_TwoPauseEpochsWithoutObservedResume_AcknowledgesBoth()
    {
        await DisableForeignKeysAsync();
        await SeedStaleAttemptsWithMatchingDispatchStateAsync(2);

        var fence = new QueueReconciliationFenceFlag();
        var epochController = new TwoEpochPauseInterceptor(fence);
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(epochController);
        var service = new QueueReconciliationService(
            scopeFactory,
            epochController,
            fence);

        await RunHostedServiceAsync(service, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(fence, () => fence.AcknowledgementCount, 2);

            epochController.EpochFlips.Should().Be(1);
            fence.AcknowledgementCount.Should().Be(2);
            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
        });
    }

    [Fact]
    public async Task QueueReconciliationService_PauseAfterAttempt_SavesBeforeAcknowledging()
    {
        await DisableForeignKeysAsync();
        await SeedStaleAttemptsWithMatchingDispatchStateAsync(2);

        var fence = new QueueReconciliationFenceFlag();
        var saveObserver = new FenceAwareSaveChangesInterceptor(fence);
        var pauseTrigger = new PauseOnDispatchStateReadInterceptor(fence);
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(saveObserver, pauseTrigger);
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);
        saveObserver.IsEnabled = true;
        pauseTrigger.IsEnabled = true;

        await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

        saveObserver.AcknowledgementCounts.Should().NotBeEmpty();
        saveObserver.AcknowledgementCounts.Should().OnlyContain(
            count => count == 0,
            "all staged queue mutations must commit before the writer acknowledges quiescence");
        fence.AcknowledgementCount.Should().Be(1);
    }

    [Fact]
    public async Task QueueReconciliationService_PauseAfterNullAttemptRecovery_SavesBeforeAcknowledging()
    {
        await DisableForeignKeysAsync();
        Guid printerId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        await using (AppDbContext seed = new(
                         new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options))
        {
            seed.PrinterDispatchStates.Add(new PrinterDispatchState
            {
                PrinterId = printerId,
                ActiveJobId = jobId,
                ActiveDispatchAttemptId = attemptId,
            });
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = 1,
                AggregateType = nameof(PrintJob),
                AggregateId = jobId,
                EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
                Status = QueueOutboxEventStatus.Processing,
                FailureCode = "backend_outcome_unknown",
                PrinterId = printerId,
                AttemptId = null,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var fence = new QueueReconciliationFenceFlag();
        var saveObserver = new FenceAwareSaveChangesInterceptor(fence);
        var pauseTrigger = new PauseOnDispatchStateReadInterceptor(fence);
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory(saveObserver, pauseTrigger);
        var service = new QueueReconciliationService(
            scopeFactory,
            NullLogger<QueueReconciliationService>.Instance,
            fence);
        saveObserver.IsEnabled = true;
        pauseTrigger.IsEnabled = true;

        await service.ReconcileStaleAttemptsAsync(CancellationToken.None);

        saveObserver.AcknowledgementCounts.Should().ContainSingle();
        saveObserver.AcknowledgementCounts.Should().OnlyContain(
            count => count == 0,
            "null-attempt recovery must commit before the writer acknowledges quiescence");
        fence.AcknowledgementCount.Should().Be(1);
    }

    private async Task DisableForeignKeysAsync()
    {
        await using DbCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedStaleAttemptsWithMatchingDispatchStateAsync(int count)
    {
        await using AppDbContext seed = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        foreach (int _ in Enumerable.Range(0, count))
        {
            Guid attemptId = Guid.NewGuid();
            Guid printerId = Guid.NewGuid();
            seed.QueueDispatchAttempts.Add(new QueueDispatchAttempt
            {
                Id = attemptId,
                PrinterId = printerId,
                ActorSubject = "host-update-fence-test",
                StartPathKind = "Manual",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-15),
                Outcome = DispatchAttemptOutcome.InProgress,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            });
            seed.PrinterDispatchStates.Add(new PrinterDispatchState
            {
                PrinterId = printerId,
                ActiveDispatchAttemptId = attemptId,
            });
        }

        await seed.SaveChangesAsync();
    }

    private sealed class FenceAwareSaveChangesInterceptor(QueueReconciliationFenceFlag fence)
        : SaveChangesInterceptor
    {
        private readonly ConcurrentQueue<int> _acknowledgementCounts = new();

        public bool IsEnabled { get; set; }

        public IReadOnlyCollection<int> AcknowledgementCounts => _acknowledgementCounts.ToArray();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsEnabled)
            {
                _acknowledgementCounts.Enqueue(fence.AcknowledgementCount);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class PauseOnDispatchStateReadInterceptor(QueueReconciliationFenceFlag fence)
        : DbCommandInterceptor
    {
        private int _pauseRequested;

        public bool IsEnabled { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsEnabled &&
                command.CommandText.Contains("PrinterDispatchStates", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _pauseRequested, 1) == 0)
            {
                await fence.RequestPauseAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class TwoEpochPauseInterceptor(QueueReconciliationFenceFlag fence)
        : DbCommandInterceptor, ILogger<QueueReconciliationService>
    {
        private int _firstPauseRequested;
        private int _epochFlips;

        public int EpochFlips => Volatile.Read(ref _epochFlips);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning &&
                formatter(state, exception).Contains(
                    "reason=pause_observed_after_attempt",
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _epochFlips, 1) == 0)
            {
                Task resume = fence.ResumeAsync(CancellationToken.None);
                if (!resume.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException(
                        "The in-memory test fence must transition epochs synchronously.");
                }

                Task request = fence.RequestPauseAsync(CancellationToken.None);
                if (!request.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException(
                        "The in-memory test fence must transition epochs synchronously.");
                }
            }
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PrinterDispatchStates", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _firstPauseRequested, 1) == 0)
            {
                await fence.RequestPauseAsync(cancellationToken);
            }

            return result;
        }
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
        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => fence.AcknowledgementCount,
                expectedAcknowledgements: AcknowledgementsPerPausedIteration);

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
            scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not start queue work while paused");
        });
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

        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForScopeDisposalsAsync(scopeFactory, 2, TimeSpan.FromSeconds(10));
            int scopesBeforePause = scopeFactory.ScopesOpened;
            await fence.RequestPauseAsync(CancellationToken.None);
            await WaitForIntervalBoundaryAcknowledgementAsync(
                fence,
                () => fence.AcknowledgementCount,
                TimeSpan.FromSeconds(2));

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
            scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
                "the interval-boundary acknowledgement must stop the next work pass before it opens another scope");
        });
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
        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => fence.AcknowledgementCount,
                expectedAcknowledgements: AcknowledgementsPerPausedIteration);

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
            scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not start queue work while paused");
        });
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

        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForScopeDisposalsAsync(scopeFactory, 2, TimeSpan.FromSeconds(10));
            int scopesBeforePause = scopeFactory.ScopesOpened;
            await fence.RequestPauseAsync(CancellationToken.None);
            await WaitForIntervalBoundaryAcknowledgementAsync(
                fence,
                () => fence.AcknowledgementCount,
                TimeSpan.FromSeconds(2));

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
            scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
                "the interval-boundary acknowledgement must stop the next work pass before it opens another scope");
        });
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
        await RunHostedServiceAsync(sut, async () =>
        {
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => fence.AcknowledgementCount,
                expectedAcknowledgements: AcknowledgementsPerPausedIteration);

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(AcknowledgementsPerPausedIteration);
            spy.InvalidationCount.Should().Be(1, "the paused scanner must not delegate acknowledgement writes");
            scopeFactory.ScopesOpened.Should().Be(1, "the positive control opens the only scanner scope; the paused scanner must not open another");
        });
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

        await RunHostedServiceAsync(sut, async () =>
        {
            await scopeFactory.FirstScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            int scopesBeforePause = scopeFactory.ScopesOpened;
            int writesBeforePause = spy.InvalidationCount;
            writesBeforePause.Should().Be(1, "the unpaused scan must complete before pausing the interval");
            await fence.RequestPauseAsync(CancellationToken.None);
            await WaitForIntervalBoundaryAcknowledgementAsync(
                fence,
                () => fence.AcknowledgementCount,
                TimeSpan.FromSeconds(2));

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
            scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
                "the interval-boundary acknowledgement must stop the next scan before it opens another scope");
            spy.InvalidationCount.Should().Be(writesBeforePause,
                "the interval-boundary acknowledgement must precede any subsequent delegated write");
        });
    }

    private sealed class BlockingStopHostedService : IHostedService, IDisposable
    {
        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public void Dispose() => Disposed = true;
    }

    private sealed class IgnoringCancellationBackgroundService : BackgroundService
    {
        private readonly ManualResetEventSlim started = new();
        private readonly SemaphoreSlim release = new(0, 1);

        public bool WaitUntilStarted(TimeSpan timeout) => started.Wait(timeout);

        public bool Disposed { get; private set; }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            started.Set();
            return release.WaitAsync();
        }

        public override void Dispose()
        {
            Disposed = true;
            release.Release();
            base.Dispose();
        }
    }
}
