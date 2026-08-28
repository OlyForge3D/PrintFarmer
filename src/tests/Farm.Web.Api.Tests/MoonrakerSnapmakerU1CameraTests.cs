using System.Net;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

public class MoonrakerSnapmakerU1CameraTests
{
    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenMonitorStarts_ReturnsJpegAndStopsAfterIdle()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new();
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(25));
        MoonrakerClient client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(jpeg)
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().Equal(jpeg);
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenCalledRapidly_RateLimitsStartMonitor()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new();
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpeg)
            };
        }, manager);

        byte[]? first = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        byte[]? second = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");

        first.Should().Equal(jpeg);
        second.Should().Equal(jpeg);
        httpFetches.Should().Be(2);
        rpc.Methods.Count(m => m == "camera.start_monitor").Should().Be(1);
        rpc.Methods.Should().NotContain("camera.stop_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenCalledConcurrently_CoalescesStartMonitor()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xd9];
        RecordingJsonRpcClient rpc = new() { StartDelay = TimeSpan.FromMilliseconds(50) };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            Interlocked.Increment(ref httpFetches);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpeg)
            };
        }, manager);

        byte[]?[] results = await Task.WhenAll(
            client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local"),
            client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local"));

        results.Should().OnlyContain(result => result.SequenceEqual(jpeg));
        httpFetches.Should().Be(2);
        rpc.Count("camera.start_monitor").Should().Be(1);
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenWebSocketStartFails_ReturnsNullWithoutHttpFetch()
    {
        RecordingJsonRpcClient rpc = new() { FailStart = true };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");

        result.Should().BeNull();
        httpFetches.Should().Be(0);
        rpc.Methods.Should().Equal("camera.start_monitor");
    }

    [Fact]
    public async Task GetSnapmakerU1CameraSnapshotAsync_WhenStartWasSentThenReplyFails_SchedulesCleanupStop()
    {
        RecordingJsonRpcClient rpc = new() { FailStartAfterSend = true };
        SnapmakerU1CameraMonitorManager manager = new(rpc, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(25));
        int httpFetches = 0;
        MoonrakerClient client = CreateClient(_ =>
        {
            httpFetches++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, manager);

        byte[]? result = await client.GetSnapmakerU1CameraSnapshotAsync("http://u1.local");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        result.Should().BeNull();
        httpFetches.Should().Be(0);
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task EnsureMonitorStartedAsync_WhenStopFails_RetriesBeforeClearingState()
    {
        RecordingJsonRpcClient rpc = new() { StopFailuresBeforeSuccess = 1 };
        ControlledTimeProvider clock = new(new DateTime(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc));
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            maxStopRetries: 2,
            timeProvider: clock);

        bool started = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        _ = await clock.ReleaseLatestTimerAndAwaitAsync();
        await clock.SecondTimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        _ = await clock.ReleaseLatestTimerAndAwaitAsync();
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        started.Should().BeTrue();
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task EnsureMonitorStartedAsync_WhenRateLimitPreventsRestartWhileStopped_ReturnsFalse()
    {
        RecordingJsonRpcClient rpc = new();
        ControlledTimeProvider clock = new(new DateTime(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc));
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60),
            timeProvider: clock);

        bool firstStart = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        Task<RecordingJsonRpcClient.StopInvocation> stopAttempt = rpc.StopInvocationAtAsync(0);
        ControlledTimeProvider.TimerFireResult fired = await clock.ReleaseLatestTimerAndAwaitAsync();
        fired.CallbackInvoked.Should().BeTrue("the authoritative idle-stop timer must execute");
        await stopAttempt.WaitAsync(TimeSpan.FromSeconds(10));

        bool secondStart = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);

        firstStart.Should().BeTrue();
        secondStart.Should().BeFalse("rate limit prevents restart while stopped");
        clock.TimerCount.Should().Be(1, "the stopped-and-rate-limited Ensure call must not schedule a second timer");
        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor");
    }

    [Fact]
    public async Task EnsureMonitorStartedAsync_WhenCalledRepeatedly_AllCallsReturnTrue()
    {
        RecordingJsonRpcClient rpc = new();
        ControlledTimeProvider clock = new(new DateTime(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc));
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10),
            timeProvider: clock);

        List<bool> results = [];
        for (int i = 0; i < 5; i++)
        {
            results.Add(await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None));
        }

        results.Should().AllBeEquivalentTo(true, "every repeated Ensure call must return true while the monitor is running");
        rpc.Count("camera.start_monitor").Should().Be(1);
        rpc.Count("camera.stop_monitor").Should().Be(0);
    }

    [Fact]
    public async Task ScheduleIdleStop_WhenCalledRepeatedly_ObsoleteTimersDoNotStopMonitor()
    {
        // Verifies that cancelling a superseded CTS (Cancel then Dispose) prevents the old Task.Delay
        // from issuing camera.stop_monitor at the stale deadline, and that only the latest authoritative
        // idle-stop timer can stop the monitor.
        RecordingJsonRpcClient rpc = new();
        ControlledTimeProvider clock = new(new DateTime(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc));
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10),
            timeProvider: clock);

        // Each successive call cancels the previous idle-stop CTS and creates a new one.
        // timer[0] and timer[1] end up disposed; timer[2] is the authoritative latest.
        bool r1 = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        bool r2 = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        bool r3 = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);

        r1.Should().BeTrue();
        r2.Should().BeTrue();
        r3.Should().BeTrue();
        rpc.Count("camera.start_monitor").Should().Be(1);
        await clock.TimerCreatedAtAsync(2).WaitAsync(TimeSpan.FromSeconds(1));

        // Firing obsolete timers is a no-op because their backing CancellationTokenSource was
        // cancelled (not merely disposed), so Task.Delay disposed those timers already.
        ControlledTimeProvider.TimerFireResult stale0 = await clock.ReleaseTimerAtAndAwaitAsync(0);
        ControlledTimeProvider.TimerFireResult stale1 = await clock.ReleaseTimerAtAndAwaitAsync(1);
        stale0.CallbackInvoked.Should().BeFalse("stale timer[0] is cancelled/disposed and must not execute its callback");
        stale1.CallbackInvoked.Should().BeFalse("stale timer[1] is cancelled/disposed and must not execute its callback");
        stale0.WasAlreadyCompleted.Should().BeTrue("stale timer[0] should already be completed by cancellation disposal");
        stale1.WasAlreadyCompleted.Should().BeTrue("stale timer[1] should already be completed by cancellation disposal");
        rpc.StopInvocationCount.Should().Be(0, "no stale timer callback should reach camera.stop_monitor");
        rpc.Count("camera.stop_monitor").Should().Be(0, "stale timers must not stop the monitor");

        // Only the authoritative latest idle-stop timer should stop the monitor.
        Task<RecordingJsonRpcClient.StopInvocation> firstStopAttempt = rpc.StopInvocationAtAsync(0);
        ControlledTimeProvider.TimerFireResult latest = await clock.ReleaseLatestTimerAndAwaitAsync();
        latest.CallbackInvoked.Should().BeTrue("the latest authoritative timer must execute");
        RecordingJsonRpcClient.StopInvocation stop = await firstStopAttempt.WaitAsync(TimeSpan.FromSeconds(1));
        stop.Succeeded.Should().BeTrue("the first observed stop attempt should be the successful authoritative stop");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        rpc.Count("camera.stop_monitor").Should().Be(1, "exactly one stop must be issued by the authoritative latest timer");
    }

    [Fact]
    public async Task RescheduleStopRetry_WhenEnsureCalledBeforeRetryFires_CancelsRetryAndReschedulesIdleStop()
    {
        // Verifies that a pending stop-retry schedule is properly cancelled (not merely disposed)
        // when a new EnsureMonitorStartedAsync call supersedes it, preventing the stale retry from
        // issuing camera.stop_monitor at the old backoff deadline.
        RecordingJsonRpcClient rpc = new() { StopFailuresBeforeSuccess = 1 };
        ControlledTimeProvider clock = new(new DateTime(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc));
        SnapmakerU1CameraMonitorManager manager = new(
            rpc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10),
            maxStopRetries: 2,
            timeProvider: clock);

        // Start monitor → timer[0] is the idle-stop schedule.
        bool started = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        started.Should().BeTrue();

        // Fire timer[0]; stop fails → RescheduleStopRetry creates timer[1] (retry backoff).
        _ = await clock.ReleaseTimerAtAndAwaitAsync(0);
        await clock.TimerCreatedAtAsync(1).WaitAsync(TimeSpan.FromSeconds(1));

        // Access camera before retry fires; ScheduleIdleStop cancels timer[1] and creates timer[2].
        bool restarted = await manager.EnsureMonitorStartedAsync("http://u1.local", null, CancellationToken.None);
        restarted.Should().BeTrue();
        await clock.TimerCreatedAtAsync(2).WaitAsync(TimeSpan.FromSeconds(1));

        // timer[1] (retry) is now disposed; firing it must be a no-op.
        ControlledTimeProvider.TimerFireResult staleRetry = await clock.ReleaseTimerAtAndAwaitAsync(1);
        staleRetry.CallbackInvoked.Should().BeFalse("superseded retry timer must be cancelled/disposed before firing");
        staleRetry.WasAlreadyCompleted.Should().BeTrue("superseded retry timer should already be completed via cancellation disposal");
        rpc.Count("camera.stop_monitor").Should().Be(1, "only the failed idle-stop attempt has run so far");

        // Fire the new authoritative idle-stop timer[2]; stop succeeds.
        Task<RecordingJsonRpcClient.StopInvocation> secondStopAttempt = rpc.StopInvocationAtAsync(1);
        ControlledTimeProvider.TimerFireResult authoritative = await clock.ReleaseLatestTimerAndAwaitAsync();
        authoritative.CallbackInvoked.Should().BeTrue("the latest authoritative timer must execute");
        RecordingJsonRpcClient.StopInvocation successfulStop = await secondStopAttempt.WaitAsync(TimeSpan.FromSeconds(1));
        successfulStop.Succeeded.Should().BeTrue("the second observed stop attempt should be the successful authoritative stop");
        await rpc.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        rpc.Methods.Should().Equal("camera.start_monitor", "camera.stop_monitor", "camera.stop_monitor");
    }

    private static MoonrakerClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        ISnapmakerU1CameraMonitorManager manager)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                req.RequestUri?.AbsolutePath.Should().Be("/server/files/camera/monitor.jpg");
                return responder(req);
            });

#pragma warning disable CA2000
        HttpClient http = new(handler.Object);
#pragma warning restore CA2000
        return new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings(), manager);
    }

    private sealed class RecordingJsonRpcClient : IMoonrakerJsonRpcClient
    {
        private readonly object _stopSync = new();
        private readonly List<StopInvocation> _stopInvocations = [];
        private readonly List<TaskCompletionSource<StopInvocation>> _stopSignals = [];

        public List<string> Methods { get; } = [];

        public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailStart { get; set; }

        public bool FailStartAfterSend { get; set; }

        public int StopFailuresBeforeSuccess { get; set; }

        public TimeSpan StartDelay { get; set; }

        public int StopInvocationCount
        {
            get
            {
                lock (_stopSync)
                {
                    return _stopInvocations.Count;
                }
            }
        }

        public int Count(string method)
        {
            lock (Methods)
            {
                return Methods.Count(m => m == method);
            }
        }

        public Task<StopInvocation> StopInvocationAtAsync(int index)
        {
            lock (_stopSync)
            {
                while (_stopSignals.Count <= index)
                {
                    _stopSignals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                if (index < _stopInvocations.Count)
                {
                    return Task.FromResult(_stopInvocations[index]);
                }

#pragma warning disable VSTHRD003 // returns a TaskCompletionSource's task the test controls to synchronize a fake stop-signal; not a foreign/UI-thread task.
                return _stopSignals[index].Task;
#pragma warning restore VSTHRD003
            }
        }

        public Task SendMethodAsync(Uri baseUrl, string method, Farm.Infrastructure.Domain.PrinterCredential? credential, CancellationToken ct)
        {
            lock (Methods)
            {
                Methods.Add(method);
            }

            if (method == "camera.start_monitor" && FailStart)
            {
                throw new InvalidOperationException("connect timeout");
            }

            if (method == "camera.start_monitor" && StartDelay > TimeSpan.Zero)
            {
                return SendStartAfterDelayAsync(ct);
            }

            if (method == "camera.start_monitor" && FailStartAfterSend)
            {
                throw new MoonrakerJsonRpcException("reply failed", requestSent: true);
            }

            if (method == "camera.stop_monitor")
            {
                bool shouldFail = StopFailuresBeforeSuccess > 0;
                if (shouldFail)
                {
                    StopFailuresBeforeSuccess--;
                }

                RecordStopInvocation(!shouldFail);

                if (shouldFail)
                {
                    throw new MoonrakerJsonRpcException("stop failed", requestSent: true);
                }

                StopObserved.TrySetResult();
            }

            return Task.CompletedTask;
        }

        private async Task SendStartAfterDelayAsync(CancellationToken ct)
        {
            await Task.Delay(StartDelay, ct);
            if (FailStartAfterSend)
            {
                throw new MoonrakerJsonRpcException("reply failed", requestSent: true);
            }
        }

        private void RecordStopInvocation(bool succeeded)
        {
            lock (_stopSync)
            {
                int idx = _stopInvocations.Count;
                StopInvocation invocation = new(idx, succeeded);
                _stopInvocations.Add(invocation);

                while (_stopSignals.Count <= idx)
                {
                    _stopSignals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                _stopSignals[idx].TrySetResult(invocation);
            }
        }

        public sealed record StopInvocation(int Index, bool Succeeded);
    }

    private sealed class ControlledTimeProvider(DateTime nowUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ControlledTimer> _timers = [];
        private readonly DateTimeOffset _now = new(nowUtc, TimeSpan.Zero);
        private readonly TaskCompletionSource _firstTimerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondTimerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TaskCompletionSource> _timerSignals = [];

        public Task FirstTimerCreated => _firstTimerCreated.Task;

        public Task SecondTimerCreated => _secondTimerCreated.Task;

        public int TimerCount
        {
            get
            {
                lock (_sync)
                {
                    return _timers.Count;
                }
            }
        }

        /// <summary>Returns a task that completes when the timer at <paramref name="index"/> (0-based) has been created.</summary>
        public Task TimerCreatedAtAsync(int index)
        {
            lock (_sync)
            {
                while (_timerSignals.Count <= index)
                {
                    _timerSignals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

#pragma warning disable VSTHRD003 // returns a TaskCompletionSource's task the test controls to synchronize a fake timer signal; not a foreign/UI-thread task.
                return _timerSignals[index].Task;
#pragma warning restore VSTHRD003
            }
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ControlledTimer timer = new(callback, state);
            lock (_sync)
            {
                int idx = _timers.Count;
                _timers.Add(timer);

                while (_timerSignals.Count <= idx)
                {
                    _timerSignals.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                _timerSignals[idx].TrySetResult();

                if (idx == 0)
                {
                    _firstTimerCreated.TrySetResult();
                }
                else if (idx == 1)
                {
                    _secondTimerCreated.TrySetResult();
                }
            }

            return timer;
        }

        public async Task<TimerFireResult> ReleaseLatestTimerAndAwaitAsync()
        {
            ControlledTimer? timer;
            lock (_sync)
            {
                timer = _timers.LastOrDefault();
            }

            if (timer is null)
            {
                return new TimerFireResult(CallbackInvoked: false, WasAlreadyCompleted: true);
            }

            return await timer.FireAndAwaitAsync().ConfigureAwait(false);
        }

        public async Task<TimerFireResult> ReleaseTimerAtAndAwaitAsync(int index)
        {
            ControlledTimer? timer;
            lock (_sync)
            {
                timer = index < _timers.Count ? _timers[index] : null;
            }

            if (timer is null)
            {
                return new TimerFireResult(CallbackInvoked: false, WasAlreadyCompleted: true);
            }

            return await timer.FireAndAwaitAsync().ConfigureAwait(false);
        }

        public readonly record struct TimerFireResult(bool CallbackInvoked, bool WasAlreadyCompleted);

        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _completed;
            private readonly TaskCompletionSource _fireSettled =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _completed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _completed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public async Task<TimerFireResult> FireAndAwaitAsync()
            {
                int previous = Interlocked.Exchange(ref _completed, 1);
                bool callbackInvoked = previous == 0;
                try
                {
                    if (callbackInvoked)
                    {
                        callback(state);
                    }
                }
                finally
                {
                    _fireSettled.TrySetResult();
                }

#pragma warning disable VSTHRD003 // _fireSettled is a TaskCompletionSource this test fixture controls to signal that timer-fire callback processing settled; not a foreign/UI-thread task.
                await _fireSettled.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                return new TimerFireResult(CallbackInvoked: callbackInvoked, WasAlreadyCompleted: previous != 0);
            }
        }
    }
}
