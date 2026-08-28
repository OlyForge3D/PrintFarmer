using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Sdcp;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Services.Sdcp;

/// <summary>
/// Unit tests for the printer-row caching + invalidation behavior added to
/// <see cref="SdcpPollingService"/> for issue #1763, and the durable invalidation-generation
/// fence added afterward (PR #1786 review) to close a race where <c>PollPrinterAsync</c>'s
/// cache-miss fallback could publish a stale row if an invalidation arrived while the fetch was
/// still in flight.
/// </summary>
public class SdcpPollingServiceCacheTests : IDisposable
{
    private readonly Mock<ISdcpClient> _sdcpClient = new(MockBehavior.Loose);
    private readonly Mock<IPrintersRepository> _printersRepository = new(MockBehavior.Loose);
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Loose);
    private readonly SdcpPollingService _service;
    private readonly MethodInfo _pollPrinterAsync;
    private readonly MethodInfo _onPrinterInvalidated;
    private readonly MethodInfo _tryPublishCachedPrinter;
    private readonly FieldInfo _printerStatesField;
    private readonly FieldInfo _invalidationGenerationsField;
    private readonly Type _pollingStateType;

    public SdcpPollingServiceCacheTests()
    {
        _unitOfWork.Setup(u => u.Printers).Returns(_printersRepository.Object);

        var spoolProvider = new ManagedSpoolProviderHelper(
            new Mock<ISpoolmanStatusCache>(MockBehavior.Loose).Object,
            NullLogger<ManagedSpoolProviderHelper>.Instance);

        Mock<IServiceProvider> serviceProvider = new(MockBehavior.Loose);
        serviceProvider.Setup(p => p.GetService(typeof(ISdcpClient))).Returns(_sdcpClient.Object);
        serviceProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_unitOfWork.Object);
        serviceProvider.Setup(p => p.GetService(typeof(ManagedSpoolProviderHelper))).Returns(spoolProvider);

        Mock<IServiceScope> scope = new(MockBehavior.Loose);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new(MockBehavior.Loose);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IClientProxy> clientProxy = new(MockBehavior.Loose);
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _service = new SdcpPollingService(
            hub: hub.Object,
            scopeFactory: scopeFactory.Object,
            logger: NullLogger<SdcpPollingService>.Instance,
            statusCacheWriter: new Mock<IPrinterStatusCacheWriter>(MockBehavior.Loose).Object);

        Type serviceType = typeof(SdcpPollingService);
        _pollPrinterAsync = serviceType.GetMethod("PollPrinterAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onPrinterInvalidated = serviceType.GetMethod("OnPrinterInvalidated", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _tryPublishCachedPrinter = serviceType.GetMethod("TryPublishCachedPrinter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _printerStatesField = serviceType.GetField("_printerStates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _invalidationGenerationsField = serviceType.GetField("_invalidationGenerations", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _pollingStateType = serviceType.GetNestedType("PrinterPollingState", BindingFlags.NonPublic)!;
    }

    public void Dispose() => _service.Dispose();

    private static PrinterCompositeStatus IdleStatus() => new(
        IsOnline: true,
        State: "IDLE",
        Progress: null,
        JobName: null,
        ThumbnailUrl: null,
        CameraStreamUrl: null,
        CameraSnapshotUrl: null);

    private void SeedCachedPrinter(Guid printerId, Printer printer)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        object state = Activator.CreateInstance(_pollingStateType)!;
        _pollingStateType.GetProperty("PrinterId")!.SetValue(state, printerId);
        _pollingStateType.GetProperty("LastKnownIsOnline")!.SetValue(state, false);
        _pollingStateType.GetProperty("CachedPrinter")!.SetValue(state, printer);
        states[printerId] = state;
    }

    private Printer? GetCachedPrinter(Guid printerId)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        object? state = states[printerId];
        return state is null ? null : (Printer?)_pollingStateType.GetProperty("CachedPrinter")!.GetValue(state);
    }

    /// <summary>
    /// Reads the durable, per-printer invalidation generation counter directly (bypassing
    /// <c>GetOrAdd</c>'s side effect of creating a missing entry), returning 0 for a printer id
    /// with no entry yet -- matching the fallback the production code itself uses when snapshotting
    /// a printer's generation for the very first time.
    /// </summary>
    private long GetInvalidationGeneration(Guid printerId)
    {
        var generations = (System.Collections.IDictionary)_invalidationGenerationsField.GetValue(_service)!;
        return generations.Contains(printerId) ? (long)generations[printerId]! : 0L;
    }

    private object GetOrCreateEmptyState(Guid printerId)
    {
        var states = (System.Collections.IDictionary)_printerStatesField.GetValue(_service)!;
        if (states[printerId] is { } existing)
        {
            return existing;
        }

        object state = Activator.CreateInstance(_pollingStateType)!;
        _pollingStateType.GetProperty("PrinterId")!.SetValue(state, printerId);
        _pollingStateType.GetProperty("LastKnownIsOnline")!.SetValue(state, false);
        states[printerId] = state;
        return state;
    }

    private async Task RunOneTickAsync(Guid printerId, PrinterCompositeStatus status)
    {
        using CancellationTokenSource cts = new();
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(status);
            });

        try
        {
            await (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }
    }

    [Fact]
    public async Task PollPrinterAsync_CachedPrinterPresent_DoesNotQueryRepository()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Cached Printer", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
        SeedCachedPrinter(printerId, printer);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "a seeded CachedPrinter must satisfy the poll tick without a fresh DB read");
    }

    [Fact]
    public async Task PollPrinterAsync_NoCachedPrinter_FallsBackToRepositoryAndPopulatesCache()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Name = "Fallback Printer", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        await RunOneTickAsync(printerId, IdleStatus());

        _printersRepository.Verify(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()), Times.Once);
        GetCachedPrinter(printerId).Should().BeSameAs(printer, "the cache-miss path must populate the cache for subsequent ticks");
    }

    [Fact]
    public void OnPrinterInvalidated_MatchingPrinterId_ClearsCachedPrinter()
    {
        Guid printerId = Guid.NewGuid();
        var printer = new Printer { Id = printerId, Backend = (int)PrinterBackend.SDCP };
        SeedCachedPrinter(printerId, printer);

        _onPrinterInvalidated.Invoke(_service, [printerId]);

        GetCachedPrinter(printerId).Should().BeNull("invalidation must force the very next poll to re-fetch fresh data");
    }

    [Fact]
    public void OnPrinterInvalidated_UnknownPrinterId_IsNoOp()
    {
        Action act = () => _onPrinterInvalidated.Invoke(_service, [Guid.NewGuid()]);

        act.Should().NotThrow("invalidation for a printer with no active polling state must be a safe no-op");
    }

    /// <summary>
    /// Regression test for the race flagged in PR #1786 review: <c>PollPrinterAsync</c>'s
    /// cache-miss fallback used to write back whatever <c>GetPrinterAsync</c> returned
    /// unconditionally, even if <see cref="IPrinterCacheInvalidator"/> invalidated the printer
    /// while that fetch was still in flight - resurrecting a stale row into the cache right after
    /// the edit that was supposed to clear it. The durable invalidation-generation fence must
    /// detect that race and decline to publish the stale result.
    /// </summary>
    [Fact]
    public async Task PollPrinterAsync_InvalidationRacesCacheMissFetch_DoesNotPublishStaleData()
    {
        Guid printerId = Guid.NewGuid();
        var stale = new Printer { Id = printerId, Name = "Stale", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };

        var fetchStarted = new TaskCompletionSource();
        var releaseFetch = new TaskCompletionSource();

        _printersRepository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                fetchStarted.SetResult();
                await releaseFetch.Task;
                return stale;
            });

        using CancellationTokenSource cts = new();
        _sdcpClient
            .Setup(c => c.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                return Task.FromResult(IdleStatus());
            });

        Task pollTask = (Task)_pollPrinterAsync.Invoke(_service, [printerId, cts.Token])!;

        await fetchStarted.Task;
        // Invalidate the printer while the cache-miss fetch above is still in flight.
        _onPrinterInvalidated.Invoke(_service, [printerId]);
        releaseFetch.SetResult();

        try
        {
            await pollTask;
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exits via cancellation once the single tick completes.
        }

        GetCachedPrinter(printerId).Should().BeNull(
            "an invalidation racing the cache-miss fetch must prevent the stale row from being published");
    }

    /// <summary>
    /// Regression test for Hicks's follow-up review finding: the test above only proves the
    /// generation fence rejects a fetch that captured its generation *before* an invalidation ran
    /// to completion (i.e. it never actually contends with <c>_cacheGates</c>'s lock, because the
    /// TCS pauses well before the critical section is entered). It does not prove the
    /// <c>_cacheGates</c> lock added afterward actually serializes <c>TryPublishCachedPrinter</c>'s
    /// check-and-write against a concurrent <c>OnPrinterInvalidated</c> call. This test races the
    /// two directly, many times, with no artificial pause, and asserts a deterministic invariant
    /// that only holds if the two are genuinely mutually exclusive on the same per-printer lock:
    /// since both operations start from the same generation captured up front, no matter which one
    /// wins the race for the lock, <c>CachedPrinter</c> must end up null every round --
    ///   * if the publish's critical section runs first, it writes the printer, but the
    ///     invalidation's critical section then runs next (serialized behind the same lock) and
    ///     clears it again -- final state: null.
    ///   * if the invalidation's critical section runs first, it bumps the generation past what the
    ///     publish captured, so the publish's generation check then fails and it declines to write
    ///     -- final state: null (unchanged by the clear).
    /// Without the lock serializing the two, the publish's check-then-write could observe a
    /// matching generation, get preempted before its write, let the invalidation's clear run, and
    /// then resume and publish the stale printer anyway -- leaving a non-null, stale
    /// <c>CachedPrinter</c> after both complete. Many rounds under real thread-pool contention make
    /// that narrow window likely to be hit at least once if the lock ever regresses.
    /// </summary>
    [Fact]
    public async Task OnPrinterInvalidated_ConcurrentWithTryPublishCachedPrinter_NeverResurrectsStaleData()
    {
        Guid printerId = Guid.NewGuid();
        object state = GetOrCreateEmptyState(printerId);

        const int rounds = 200;
        for (int i = 0; i < rounds; i++)
        {
            var printer = new Printer { Id = printerId, Name = $"Stale-{i}", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
            long capturedGeneration = GetInvalidationGeneration(printerId);

            var tasks = new Task[2];
            tasks[0] = Task.Run(() => _tryPublishCachedPrinter.Invoke(_service, [printerId, printer, capturedGeneration, state, null]));
            tasks[1] = Task.Run(() => _onPrinterInvalidated.Invoke(_service, [printerId]));

            await Task.WhenAll(tasks);

            GetCachedPrinter(printerId).Should().BeNull(
                "TryPublishCachedPrinter and OnPrinterInvalidated must serialize on the same " +
                "per-printer lock: whichever runs first, the loser's outcome (a fresh " +
                "bump-and-clear running after the write, or a generation check that now observes " +
                "the bump) must leave the cache empty rather than resurrecting the stale printer " +
                "snapshot captured before either ran");
        }
    }

    /// <summary>
    /// Regression test for the second round of review on the concurrency stress test above
    /// (Hicks and Vasquez): 200 rounds of unconstrained <c>Task.Run</c> racing is still only
    /// probabilistic evidence -- it never *proves* the two calls are mutually exclusive, it only
    /// shows the invariant held in however many interleavings the thread pool happened to produce.
    /// This test instead forces the exact interleaving deterministically, using the
    /// <c>onGenerationCheckPassedForTestingOnly</c> seam added to <c>TryPublishCachedPrinter</c>:
    /// the publish call is paused *while still holding the per-printer <c>_cacheGates</c> lock*,
    /// immediately after its generation check has passed but before it writes
    /// <c>CachedPrinter</c>. A concurrent <c>OnPrinterInvalidated</c> call is then started and
    /// positively observed to NOT complete while the publish call holds the lock paused -- proving
    /// real mutual exclusion by direct observation, not by absence of a failure over many
    /// iterations. Only after that is confirmed is the publish call allowed to resume (completing
    /// its write), at which point the waiting invalidation call proceeds and clears the cache.
    /// </summary>
    [Fact]
    public async Task TryPublishCachedPrinter_PausedInsideLockAfterGenerationCheck_BlocksConcurrentOnPrinterInvalidatedUntilReleased()
    {
        Guid printerId = Guid.NewGuid();
        object state = GetOrCreateEmptyState(printerId);
        var printer = new Printer { Id = printerId, Name = "Stale", ServerUrl = "192.168.1.60", Backend = (int)PrinterBackend.SDCP };
        long capturedGeneration = GetInvalidationGeneration(printerId);

        using ManualResetEventSlim insideCriticalSection = new(initialState: false);
        using ManualResetEventSlim releasePublish = new(initialState: false);

        Task publishTask = Task.Run(() =>
        {
            void OnGenerationCheckPassed()
            {
                insideCriticalSection.Set();
                // Blocking wait is intentional and safe here: this callback runs synchronously
                // inside TryPublishCachedPrinter's `lock (gate)` block, on a dedicated thread-pool
                // thread reserved for exactly this purpose by Task.Run -- it must hold the lock
                // open until the test explicitly releases it.
                releasePublish.Wait();
            }

            _tryPublishCachedPrinter.Invoke(_service, [printerId, printer, capturedGeneration, state, (Action)OnGenerationCheckPassed]);
        });

        // Widened from 5s: under full test-suite parallelism (maxParallelThreads=0),
        // thread-pool contention from dozens of concurrently-running hosts can legitimately
        // delay the Task.Run scheduling this waits on past a short timeout.
        insideCriticalSection.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue(
            "the publish call must reach its paused callback inside the lock within the timeout");

        Task invalidateTask = Task.Run(() => _onPrinterInvalidated.Invoke(_service, [printerId]));

        // The invalidation call needs the same _cacheGates lock the paused publish call is still
        // holding, so it must not be able to complete yet. A generous bounded wait (rather than a
        // fixed sleep) keeps this fast when it genuinely blocks, while still giving a
        // false-negative-prone regression (e.g. the lock silently removed) ample time to complete
        // and be caught.
        Task completedFirst = await Task.WhenAny(invalidateTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        completedFirst.Should().NotBeSameAs(invalidateTask,
            "OnPrinterInvalidated must genuinely block on the same per-printer lock while " +
            "TryPublishCachedPrinter's critical section is paused mid-way through -- if it were " +
            "able to complete here, the two calls are not actually serialized on the same lock");

        releasePublish.Set();

        await Task.WhenAll(publishTask, invalidateTask);

        GetCachedPrinter(printerId).Should().BeNull(
            "once released, the paused publish completes its now-stale write, and the invalidation " +
            "that was blocked behind it then runs and clears the cache -- the same end state as the " +
            "probabilistic test above, but this time reached via a provably forced, not merely " +
            "likely, interleaving");
    }
}
