using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Electricity;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
    // The liveness observation threshold for a paused writer: two acknowledgements suffice to
    // prove the loop is re-checking rather than latched after its first acknowledgement. The
    // two loop shapes covered here reach this threshold differently — outbox-style writers
    // (e.g. QueueOutboxPublisherService, PowerReadingPruneService, QueueRetentionPruneService)
    // acknowledge twice per ~250 ms iteration (top-of-loop paused branch + interval-boundary
    // wait — see the corrected cadence block on the paused-loop cycle rate test below), while
    // continue-style consumers (e.g. BackendStartCommandConsumerService,
    // BackendControlCommandConsumerService, BedClearAcknowledgementExpiryService) acknowledge
    // once per iteration and reach the pair across two iterations.
    private const int AcknowledgementsPerPausedIteration = 2;

    // The prune services' paused-branch and interval-wait delay, advanced on the manual clock.
    private static readonly TimeSpan PausedLoopCadence = TimeSpan.FromMilliseconds(250);

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

    [Fact]
    public async Task FenceCoordinator_AtDeadline_DoesNotRepeatQuiescenceProbe()
    {
        var writer = new Mock<IFenceableWriter>();
        writer.SetupGet(candidate => candidate.Name).Returns("deadline-writer");
        writer.Setup(candidate => candidate.QuiesceAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        writer.Setup(candidate => candidate.IsQuiescedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var coordinator = new HostUpdateFenceCoordinator(
            [writer.Object],
            proofTimeout: TimeSpan.Zero,
            pollInterval: TimeSpan.FromSeconds(2));

        Func<Task> act = () => coordinator.RunAsync(
            new HostUpdateExecutionRequest(
                "release-1",
                1,
                "sha256:" + new string('a', 64),
                new string('b', 40),
                HostUpdateExecutionChannel.Stable,
                []),
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateFenceProofFailedException>();
        writer.Verify(candidate => candidate.IsQuiescedAsync(
            It.IsAny<CancellationToken>()), Times.Once);
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
        var clock = new ManualTimeProvider();
        var fence = new PowerReadingPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence,
            clock);
        await RunHostedServiceAsync(sut, async () =>
        {
            await AssertPausedPruneLoopCadenceAsync(clock, fence, () => fence.AcknowledgementCount);

            scopeFactory.ScopesOpened.Should().Be(0, "the fenced writer must not touch the database while a pause is pending");
        });
    }

    [Fact]
    public async Task PowerReadingPruneService_PauseResumePause_CountsOnlyCurrentEpochAcknowledgements()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var clock = new ManualTimeProvider();
        var fence = new PowerReadingPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence,
            clock);
        await RunHostedServiceAsync(sut, async () =>
        {
            // Epoch 1 leaves the loop parked on its paused-branch delay (two timers armed).
            await AssertPausedPruneLoopCadenceAsync(clock, fence, () => fence.AcknowledgementCount);

            await fence.ResumeAsync(CancellationToken.None);
            fence.AcknowledgementCount.Should().Be(0, "ResumeAsync closes the pause epoch and resets its acknowledgements");

            // Completing the paused delay after resume moves the loop into its 24 h interval
            // wait, which must neither acknowledge a closed epoch nor start a prune pass.
            clock.Advance(PausedLoopCadence);
            await clock.WaitForArmedTimersAsync(3, "the post-resume interval-wait delay");
            fence.AcknowledgementCount.Should().Be(0);
            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeFalse();

            // Epoch 2: the interval wait observes the new pause and acknowledges at the boundary,
            // then the next top-of-loop paused branch acknowledges again before parking.
            await fence.RequestPauseAsync(CancellationToken.None);
            clock.Advance(PausedLoopCadence);
            await clock.WaitForArmedTimersAsync(4, "the second epoch's paused-branch delay");

            fence.AcknowledgementCount.Should().Be(AcknowledgementsPerPausedIteration,
                "the second epoch must start from zero rather than inheriting epoch 1's acknowledgements");
            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
            scopeFactory.ScopesOpened.Should().Be(0, "no prune pass may run across the pause/resume/pause cycle");
        });
    }

    [Fact]
    public async Task InMemoryHostUpdateWriterActivityFlag_ResumeAsync_ResetsAcknowledgementCountEachCycle()
    {
        var fence = new InMemoryHostUpdateWriterActivityFlag();

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            await fence.RequestPauseAsync(CancellationToken.None);
            fence.AcknowledgementCount.Should().Be(0, $"cycle {cycle} must not inherit earlier epochs' acknowledgements");

            await fence.AcknowledgePausedAsync(CancellationToken.None);
            fence.AcknowledgementCount.Should().Be(1, $"cycle {cycle} observed exactly one acknowledgement");
            (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();

            await fence.ResumeAsync(CancellationToken.None);
            fence.AcknowledgementCount.Should().Be(0, $"ResumeAsync must reset the counter at the end of cycle {cycle}");
            fence.IsAcknowledged.Should().BeFalse();
            (await fence.IsPauseRequestedAsync(CancellationToken.None)).Should().BeFalse();

            await fence.AcknowledgePausedAsync(CancellationToken.None);
            fence.AcknowledgementCount.Should().Be(0, "an acknowledgement with no pending pause is ignored");
        }
    }

    [Fact]
    public async Task PowerReadingPruneService_WithoutPauseRequested_OpensScopeNormally()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var fence = new PowerReadingPruneFenceFlag();

        var sut = new PowerReadingPruneService(
            scopeFactory,
            NullLogger<PowerReadingPruneService>.Instance,
            fence,
            new ManualTimeProvider());

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
        var clock = new ManualTimeProvider();
        var fence = new QueueRetentionPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);

        var settings = new QueueRetentionSettings { PruneInterval = TimeSpan.FromHours(6) };
        var sut = new QueueRetentionPruneService(
            scopeFactory,
            Options.Create(settings),
            NullLogger<QueueRetentionPruneService>.Instance,
            fence,
            clock);
        await RunHostedServiceAsync(sut, async () =>
        {
            await AssertPausedPruneLoopCadenceAsync(clock, fence, () => fence.AcknowledgementCount);

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
            fence,
            new ManualTimeProvider());

        await RunHostedServiceAsync(sut, async () =>
        {
            await scopeFactory.FirstScopeOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scopeFactory.ScopesOpened.Should().BeGreaterThan(0, "an unpaused fence must not block normal pruning");
        });
    }

    /// <summary>
    /// Drives one paused cycle of an outbox-style prune loop on the manual clock and asserts its
    /// exact acknowledgement cadence. The top-of-loop paused branch acknowledges and parks on
    /// its 250 ms delay; advancing one cadence step completes that delay, the interval-boundary
    /// wait returns immediately while paused (second acknowledgement), and the next top-of-loop
    /// paused branch acknowledges again (third) before parking on a fresh delay. Exact counts
    /// also prove the paused loop does not busy-spin between delays.
    /// </summary>
    private static async Task AssertPausedPruneLoopCadenceAsync(
        ManualTimeProvider clock,
        IHostUpdateWriterActivityFlag fence,
        Func<int> acknowledgementCount)
    {
        await clock.WaitForArmedTimersAsync(1, "the first paused-branch delay");
        acknowledgementCount().Should().Be(1, "the top-of-loop paused branch acknowledges once before its delay");
        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();

        clock.Advance(PausedLoopCadence);
        await clock.WaitForArmedTimersAsync(2, "the second paused-branch delay");
        acknowledgementCount().Should().Be(AcknowledgementsPerPausedIteration + 1,
            "one paused cycle adds an interval-boundary and a top-of-loop acknowledgement");
        (await fence.IsPausedAsync(CancellationToken.None)).Should().BeTrue();
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
            // ResumeAsync resets the per-epoch counter, so the second epoch is observed through
            // its own count and the first epoch through the snapshot taken just before resume.
            await WaitForPauseAcknowledgementsAsync(
                fence,
                () => epochController.SecondEpochAcknowledgementCount,
                1);

            epochController.EpochFlips.Should().Be(1);
            epochController.AcknowledgementsBeforeResume.Should().Be(1,
                "the first epoch must be acknowledged before the interceptor resumes it");
            fence.AcknowledgementCount.Should().Be(1,
                "the second epoch must be acknowledged and must not inherit the first epoch's count");
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
        private int _secondEpochOpened;
        private int _acknowledgementsBeforeResume = -1;

        public int EpochFlips => Volatile.Read(ref _epochFlips);

        /// <summary>First-epoch acknowledgement count captured immediately before the resume.</summary>
        public int AcknowledgementsBeforeResume => Volatile.Read(ref _acknowledgementsBeforeResume);

        /// <summary>Second-epoch acknowledgements, or zero until the second pause has been requested.</summary>
        public int SecondEpochAcknowledgementCount =>
            Volatile.Read(ref _secondEpochOpened) == 1 ? fence.AcknowledgementCount : 0;

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
                Volatile.Write(ref _acknowledgementsBeforeResume, fence.AcknowledgementCount);
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

                Volatile.Write(ref _secondEpochOpened, 1);
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
            Options.Create(new BackendTimeoutSettings()),
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
            Options.Create(new BackendTimeoutSettings()),
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
    public async Task QueueOutboxPublisherService_PauseDuringInterval_AcknowledgesBeforePollInterval()
    {
        CountingScopeFactory scopeFactory = BuildCountingScopeFactory();
        var hub = new Mock<IHubContext<PrinterHub>>();
        var fence = new InMemoryHostUpdateWriterActivityFlag();
        var sut = new QueueOutboxPublisherService(
            scopeFactory,
            hub.Object,
            NullLogger<QueueOutboxPublisherService>.Instance,
            membershipNotifier: null,
            hostUpdateFence: fence);

        await RunHostedServiceAsync(sut, async () =>
        {
            // The first unpaused pass opens three scopes: the pre-loop stale-lease recovery,
            // then the loop's own stale-lease recovery and pending-events processing.
            await WaitForScopeDisposalsAsync(scopeFactory, 3, TimeSpan.FromSeconds(10));
            int scopesBeforePause = scopeFactory.ScopesOpened;
            await fence.RequestPauseAsync(CancellationToken.None);
            await WaitForIntervalBoundaryAcknowledgementAsync(
                fence,
                () => fence.AcknowledgementCount,
                TimeSpan.FromSeconds(2));

            fence.AcknowledgementCount.Should().BeGreaterThanOrEqualTo(1);
            scopeFactory.ScopesOpened.Should().Be(scopesBeforePause,
                "the interval-boundary acknowledgement must stop the next work pass before it opens another scope");

            // While the fence is paused, one loop iteration acknowledges twice — once at the
            // top-of-loop paused branch (before its 250 ms delay) and again at the
            // interval-boundary wait (which returns immediately while paused) — so a single
            // ~250 ms cycle produces two acknowledgements, i.e. roughly 8/s over a 1 s sample.
            // The `< 20` ceiling is a generous busy-spin guard, not an exact iteration count:
            // it must clear the expected ~8/s comfortably to absorb CI scheduling jitter while
            // still tripping if the paused loop regresses into a tight spin that racks up
            // acknowledgements far faster than the 250 ms cadence would allow.
            int acknowledgementsAfterFirst = fence.AcknowledgementCount;
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            int additionalAcknowledgements = fence.AcknowledgementCount - acknowledgementsAfterFirst;
            additionalAcknowledgements.Should().BeLessThan(20,
                "the paused loop must keep re-checking on its ~250 ms cadence, not busy-spin " +
                "without a delay between acknowledgements");
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

    /// <summary>
    /// Deterministic <see cref="TimeProvider"/> for the prune-service fence tests: wall-clock
    /// time and timers move only when <see cref="Advance"/> is called, so the services' paused
    /// cadence and interval waits are driven by the test instead of real sleeps. Real time is
    /// used only as a deadlock watchdog in <see cref="WaitForArmedTimersAsync"/>.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _timersArmed;

        public int TimersArmed
        {
            get
            {
                lock (_gate)
                {
                    return _timersArmed;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            _ = timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Moves time forward and fires every timer that became due.</summary>
        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due = [];
            lock (_gate)
            {
                _utcNow += delta;
                foreach (ManualTimer timer in _timers)
                {
                    if (timer.TryTakeDueLocked(_utcNow))
                    {
                        due.Add(timer);
                    }
                }
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        /// <summary>Waits until at least <paramref name="count"/> timers have been armed in total.</summary>
        public async Task WaitForArmedTimersAsync(int count, string expectation)
        {
            Task signal;
            lock (_gate)
            {
                if (_timersArmed >= count)
                {
                    return;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, waiter));
                signal = waiter.Task;
            }

            try
            {
                await signal.WaitAsync(Watchdog);
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Deadlock watchdog: expected {count} armed timer(s) ({expectation}) but observed " +
                    $"{TimersArmed} within {Watchdog}.");
            }
        }

        private bool Arm(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            List<TaskCompletionSource> released = [];
            lock (_gate)
            {
                if (!timer.SetScheduleLocked(dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime, period))
                {
                    return false;
                }

                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _timersArmed++;
                    for (int i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (_waiters[i].Count <= _timersArmed)
                        {
                            released.Add(_waiters[i].Signal);
                            _waiters.RemoveAt(i);
                        }
                    }
                }
            }

            foreach (TaskCompletionSource signal in released)
            {
                _ = signal.TrySetResult();
            }

            return true;
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _ = _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => clock.Arm(this, dueTime, period);

            public bool SetScheduleLocked(DateTimeOffset? due, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _due = due;
                _period = period;
                return true;
            }

            public bool TryTakeDueLocked(DateTimeOffset now)
            {
                if (_disposed || _due is not DateTimeOffset due || due > now)
                {
                    return false;
                }

                _due = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : due + _period;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate)
                {
                    _disposed = true;
                    _due = null;
                }

                clock.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
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
