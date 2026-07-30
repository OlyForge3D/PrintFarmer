using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Verifies the lowercase SignalR event contract for filament coverage
/// invalidations (issue #709). Wire name is <c>filamentcoveragechanged</c>
/// — must stay lowercase to match existing PrintFarmer conventions
/// (see <c>printerupdated</c>, <c>jobqueueupdate</c>). Payload shape and
/// reason vocabulary are pinned by Dallas's F4 addendum.
/// </summary>
public class FilamentCoverageBroadcasterTests
{
    [Fact]
    public async Task BroadcastPrinterChangedAsync_SendsLowercaseEvent_WithPrinterIdAndReasonPayload()
    {
        Guid printerId = Guid.NewGuid();

        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).PrinterId == printerId
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.SpoolBinding
                    && ((FilamentCoverageChangedEvent)args[0]).OccurredAt != default),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(
            hub.Object,
            ScopeFactory(enabled: true),
            NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.SpoolBinding, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public async Task BroadcastFleetChangedAsync_SendsLowercaseEvent_WithNullPrinterIdAndReason()
    {
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).PrinterId == null
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.ThresholdChanged),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(
            hub.Object,
            ScopeFactory(enabled: true),
            NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastFleetChangedAsync(FilamentCoverageChangeReasons.ThresholdChanged, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public async Task Broadcast_EmptyReason_FallsBackToQueueChanged()
    {
        // Defensive: callers should never send an empty reason, but if they
        // do we must still emit a valid string on the wire so clients can
        // parse it. "queueChanged" is the most conservative refetch trigger.
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.Is<object[]>(args =>
                    args.Length == 1
                    && args[0] is FilamentCoverageChangedEvent
                    && ((FilamentCoverageChangedEvent)args[0]).Reason == FilamentCoverageChangeReasons.QueueChanged),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        FilamentCoverageBroadcaster broadcaster = new(
            hub.Object,
            ScopeFactory(enabled: true),
            NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastPrinterChangedAsync(Guid.NewGuid(), string.Empty, CancellationToken.None);

        clientProxy.Verify();
    }

    [Fact]
    public void FilamentCoverageChangeReasons_ExposesExactlyTheDallasVocabulary()
    {
        // Pin the string vocabulary against the F4 addendum so a future
        // rename here is caught by a failing test.
        FilamentCoverageChangeReasons.JobProgress.Should().Be("jobProgress");
        FilamentCoverageChangeReasons.JobAssignment.Should().Be("jobAssignment");
        FilamentCoverageChangeReasons.QueueChanged.Should().Be("queueChanged");
        FilamentCoverageChangeReasons.SpoolBinding.Should().Be("spoolBinding");
        FilamentCoverageChangeReasons.SpoolWeight.Should().Be("spoolWeight");
        FilamentCoverageChangeReasons.ThresholdChanged.Should().Be("thresholdChanged");
    }

    [Fact]
    public async Task BroadcastPrinterChangedAsync_FeatureDisabled_DoesNotSend()
    {
        Mock<IHubContext<PrinterHub>> hub = new(MockBehavior.Strict);
        FilamentCoverageBroadcaster broadcaster = new(
            hub.Object,
            ScopeFactory(enabled: false),
            NullLogger<FilamentCoverageBroadcaster>.Instance);

        await broadcaster.BroadcastPrinterChangedAsync(
            Guid.NewGuid(),
            FilamentCoverageChangeReasons.JobProgress,
            CancellationToken.None);

        hub.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BroadcastPrinterChangedAsync_BurstSendsLeadingAndOneLatestTrailingEvent()
    {
        Guid printerId = Guid.NewGuid();
        DateTime first = new(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        DateTime second = first.AddMilliseconds(10);
        DateTime latest = first.AddMilliseconds(20);
        Queue<DateTime> timestamps = new([first, second, latest]);
        ControlledDelay delay = new();
        SendRecorder sent = new();
        FilamentCoverageBroadcaster broadcaster = Broadcaster(
            sent,
            delay,
            enabled: () => true,
            utcNow: () => timestamps.Dequeue());

        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.JobProgress, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.JobProgress, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.JobProgress, CancellationToken.None);

        sent.Snapshot().Should().ContainSingle().Which.OccurredAt.Should().Be(first);
        delay.CompletePending();
        await sent.WaitForCountAsync(2);
        FilamentCoverageChangedEvent[] snapshot = sent.Snapshot();
        snapshot[1].OccurredAt.Should().Be(latest);
        snapshot[1].PrinterId.Should().Be(printerId);
    }

    [Fact]
    public async Task BroadcastPrinterChangedAsync_NoSuppressedEvent_DoesNotSendTrailingEvent()
    {
        ControlledDelay delay = new();
        SendRecorder sent = new();
        FilamentCoverageBroadcaster broadcaster = Broadcaster(sent, delay, enabled: () => true);

        await broadcaster.BroadcastPrinterChangedAsync(
            Guid.NewGuid(),
            FilamentCoverageChangeReasons.QueueChanged,
            CancellationToken.None);
        delay.CompletePending();

        sent.Snapshot().Should().ContainSingle();
    }

    [Fact]
    public async Task BroadcastPrinterChangedAsync_GateDisabledBeforeTrailing_SuppressesTrailingEvent()
    {
        bool enabled = true;
        ControlledDelay delay = new();
        SendRecorder sent = new();
        FilamentCoverageBroadcaster broadcaster = Broadcaster(sent, delay, enabled: () => enabled);
        Guid printerId = Guid.NewGuid();

        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        enabled = false;
        delay.CompletePending();

        sent.Snapshot().Should().ContainSingle();
    }

    [Fact]
    public async Task BroadcastPrinterChangedAsync_TrailingSendFailure_IsObservedAndLogged()
    {
        ControlledDelay delay = new();
        int sendCount = 0;
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref sendCount) == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("send failed")));
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        Mock<ILogger<FilamentCoverageBroadcaster>> logger = new();
        FilamentCoverageBroadcaster broadcaster = new(
            hub.Object,
            ScopeFactory(() => true),
            logger.Object,
            delay.DelayAsync);
        Guid printerId = Guid.NewGuid();

        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.SpoolWeight, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(printerId, FilamentCoverageChangeReasons.SpoolWeight, CancellationToken.None);
        delay.CompletePending();

        Volatile.Read(ref sendCount).Should().Be(2);
        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) => value.ToString()!.Contains("Failed to broadcast")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Broadcast_DifferentScopesAndReasons_CoalesceIndependently()
    {
        ControlledDelay delay = new();
        SendRecorder sent = new();
        FilamentCoverageBroadcaster broadcaster = Broadcaster(sent, delay, enabled: () => true);
        Guid firstPrinter = Guid.NewGuid();
        Guid secondPrinter = Guid.NewGuid();

        await broadcaster.BroadcastPrinterChangedAsync(firstPrinter, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(firstPrinter, FilamentCoverageChangeReasons.SpoolWeight, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(secondPrinter, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastFleetChangedAsync(FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(firstPrinter, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(firstPrinter, FilamentCoverageChangeReasons.SpoolWeight, CancellationToken.None);
        await broadcaster.BroadcastPrinterChangedAsync(secondPrinter, FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);
        await broadcaster.BroadcastFleetChangedAsync(FilamentCoverageChangeReasons.QueueChanged, CancellationToken.None);

        sent.Snapshot().Should().HaveCount(4);
        delay.CompletePending();
        await sent.WaitForCountAsync(8);
        sent.Snapshot()
            .GroupBy(e => (e.PrinterId, e.Reason))
            .Should()
            .OnlyContain(group => group.Count() == 2);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task BroadcastJobProgressIfChangedAsync_EmitsOnlyForRealProgressChanges(
        bool progressChanged,
        int expectedCalls)
    {
        Guid printerId = Guid.NewGuid();
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        if (expectedCalls > 0)
        {
            broadcaster
                .Setup(b => b.BroadcastPrinterChangedAsync(
                    printerId,
                    FilamentCoverageChangeReasons.JobProgress,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        await broadcaster.Object.BroadcastJobProgressIfChangedAsync(
            printerId,
            progressChanged,
            CancellationToken.None);

        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.JobProgress,
                It.IsAny<CancellationToken>()),
            Times.Exactly(expectedCalls));
    }

    private static FilamentCoverageBroadcaster Broadcaster(
        SendRecorder sent,
        ControlledDelay delay,
        Func<bool> enabled,
        Func<DateTime>? utcNow = null)
    {
        Mock<IClientProxy> clientProxy = new(MockBehavior.Strict);
        clientProxy
            .Setup(c => c.SendCoreAsync(
                "filamentcoveragechanged",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
                sent.Record((FilamentCoverageChangedEvent)args[0]))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> clients = new();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return new FilamentCoverageBroadcaster(
            hub.Object,
            ScopeFactory(enabled),
            NullLogger<FilamentCoverageBroadcaster>.Instance,
            delay.DelayAsync,
            utcNow);
    }

    private static IServiceScopeFactory ScopeFactory(bool enabled)
        => ScopeFactory(() => enabled);

    private static IServiceScopeFactory ScopeFactory(Func<bool> enabled)
    {
        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.IsEnabled(OperatorFeature.FilamentCoverage)).Returns(enabled);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.FilamentCoverage, It.IsAny<CancellationToken>())).ReturnsAsync(enabled);
        ServiceCollection services = new();
        services.AddScoped(_ => gate.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class SendRecorder
    {
        private readonly Lock _sync = new();
        private readonly ConcurrentQueue<FilamentCoverageChangedEvent> _sent = new();
        private readonly Dictionary<int, TaskCompletionSource> _countWaiters = [];

        public void Record(FilamentCoverageChangedEvent payload)
        {
            lock (_sync)
            {
                _sent.Enqueue(payload);
                int count = _sent.Count;
                foreach (int target in _countWaiters.Keys.Where(target => target <= count).ToArray())
                {
                    TaskCompletionSource waiter = _countWaiters[target];
                    _countWaiters.Remove(target);
                    _ = waiter.TrySetResult();
                }
            }
        }

        public FilamentCoverageChangedEvent[] Snapshot()
        {
            lock (_sync)
            {
                return [.. _sent];
            }
        }

        public Task WaitForCountAsync(int expectedCount)
        {
            Task wait;
            lock (_sync)
            {
                if (_sent.Count >= expectedCount)
                {
                    return Task.CompletedTask;
                }

                if (!_countWaiters.TryGetValue(expectedCount, out TaskCompletionSource? waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _countWaiters.Add(expectedCount, waiter);
                }

                wait = waiter.Task;
            }

            return wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ControlledDelay
    {
        private readonly Lock _sync = new();
        private readonly List<TaskCompletionSource> _pending = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            _ = delay;
            // Synchronous continuations make CompletePending an explicit barrier:
            // when it returns, each released broadcaster window has completed its
            // state/version check and any synchronously-completing send seam.
            TaskCompletionSource completion = new();
            _ = token.Register(() => completion.TrySetCanceled(token));
            lock (_sync)
            {
                _pending.Add(completion);
            }

            return completion.Task;
        }

        public void CompletePending()
        {
            TaskCompletionSource[] pending;
            lock (_sync)
            {
                pending = [.. _pending.Where(item => !item.Task.IsCompleted)];
            }

            foreach (TaskCompletionSource completion in pending)
            {
                _ = completion.TrySetResult();
            }
        }
    }
}
