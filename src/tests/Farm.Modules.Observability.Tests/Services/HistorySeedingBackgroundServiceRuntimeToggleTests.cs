using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Modules.Observability.Services.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Modules.Observability.Tests.Services;

public class HistorySeedingBackgroundServiceRuntimeToggleTests
{
    [Fact]
    public async Task HistorySeedingService_WhenDisabledAtStartup_ResumesAfterEnableToggle()
    {
        TestOptionsMonitor monitor = new(new HistorySeedingSettings
        {
            Enabled = false,
            IntervalMinutes = 1,
            InitialDelaySeconds = 0,
            ActiveSyncEnabled = true,
            ActiveSyncIntervalSeconds = 3600,
            ActiveSyncInitialDelaySeconds = 3600,
        });

        // Signaled deterministically by the mock callback the moment the service invokes it,
        // so the test reacts to the real event instead of guessing how long the service's
        // internal disabled-settings poll (a fixed 5s cadence) takes to notice the toggle.
        TaskCompletionSource<bool> seedInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IPrintJobManagementService> jobService = new();
        jobService.Setup(s => s.SeedHistoryFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => seedInvoked.TrySetResult(true))
            .Returns(Task.CompletedTask);

        using ServiceProvider provider = BuildServiceProvider(jobService.Object);
        BackgroundServiceMonitor serviceMonitor = new();
        using HistorySeedingBackgroundService service = new(
            provider,
            NullLogger<HistorySeedingBackgroundService>.Instance,
            monitor,
            serviceMonitor);

        await service.StartAsync(CancellationToken.None);

        // The service is disabled and stays disabled until we toggle it below, so no wall-clock
        // race is involved here: this window only needs to be long enough to let the initial
        // disabled iteration run, not to detect a change.
        Task settleWindow = Task.Delay(TimeSpan.FromSeconds(2));
        Task firstSignal = await Task.WhenAny(seedInvoked.Task, settleWindow);
        Assert.NotSame(seedInvoked.Task, firstSignal);
        jobService.Verify(s => s.SeedHistoryFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);

        monitor.Update(new HistorySeedingSettings
        {
            Enabled = true,
            IntervalMinutes = 1,
            InitialDelaySeconds = 0,
            ActiveSyncEnabled = true,
            ActiveSyncIntervalSeconds = 3600,
            ActiveSyncInitialDelaySeconds = 3600,
        });

        // Wait for the actual resumed-seeding signal rather than a fixed delay: the service's
        // disabled-settings poll runs on a 5s cadence unaligned with the test clock, so a fixed
        // sleep here left only ~1s of margin under CI scheduler jitter. The safety timeout below
        // is only an upper-bound guard against a genuine hang, not a guess at the exact latency.
        Task resumedSignal = await Task.WhenAny(seedInvoked.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(seedInvoked.Task, resumedSignal);

        jobService.Verify(s => s.SeedHistoryFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        BackgroundServiceStatus? status = serviceMonitor.GetStatus("HistorySeedingService");
        Assert.NotNull(status);
        Assert.True(status!.IsEnabled);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ActiveExternalSyncService_WhenDisabledAtStartup_ResumesAfterEnableToggle()
    {
        TestOptionsMonitor monitor = new(new HistorySeedingSettings
        {
            Enabled = true,
            IntervalMinutes = 3600,
            InitialDelaySeconds = 3600,
            ActiveSyncEnabled = false,
            ActiveSyncIntervalSeconds = 1,
            ActiveSyncInitialDelaySeconds = 0,
        });

        // Same deterministic-signal rationale as the history-seeding test above: this service's
        // disabled-settings poll also runs on a fixed 5s cadence unaligned with the test clock.
        TaskCompletionSource<bool> syncInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IPrintJobManagementService> jobService = new();
        jobService.Setup(s => s.SyncActiveExternalJobsFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => syncInvoked.TrySetResult(true))
            .Returns(Task.CompletedTask);

        using ServiceProvider provider = BuildServiceProvider(jobService.Object);
        BackgroundServiceMonitor serviceMonitor = new();
        using ActiveExternalJobSyncBackgroundService service = new(
            provider,
            NullLogger<ActiveExternalJobSyncBackgroundService>.Instance,
            monitor,
            serviceMonitor);

        await service.StartAsync(CancellationToken.None);

        Task settleWindow = Task.Delay(TimeSpan.FromSeconds(2));
        Task firstSignal = await Task.WhenAny(syncInvoked.Task, settleWindow);
        Assert.NotSame(syncInvoked.Task, firstSignal);
        jobService.Verify(s => s.SyncActiveExternalJobsFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.Never);

        monitor.Update(new HistorySeedingSettings
        {
            Enabled = true,
            IntervalMinutes = 3600,
            InitialDelaySeconds = 3600,
            ActiveSyncEnabled = true,
            ActiveSyncIntervalSeconds = 1,
            ActiveSyncInitialDelaySeconds = 0,
        });

        Task resumedSignal = await Task.WhenAny(syncInvoked.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(syncInvoked.Task, resumedSignal);

        jobService.Verify(s => s.SyncActiveExternalJobsFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        BackgroundServiceStatus? status = serviceMonitor.GetStatus("ActiveExternalJobSyncService");
        Assert.NotNull(status);
        Assert.True(status!.IsEnabled);

        await service.StopAsync(CancellationToken.None);
    }

    private static ServiceProvider BuildServiceProvider(IPrintJobManagementService jobService)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => jobService);
        return services.BuildServiceProvider();
    }

    private sealed class TestOptionsMonitor(HistorySeedingSettings initialValue) : IOptionsMonitor<HistorySeedingSettings>
    {
        private readonly ConcurrentDictionary<string, Action<HistorySeedingSettings, string>> _listeners = new();
        private HistorySeedingSettings _current = initialValue;

        public HistorySeedingSettings CurrentValue => _current;

        public HistorySeedingSettings Get(string? name) => _current;

        public IDisposable OnChange(Action<HistorySeedingSettings, string> listener)
        {
            string key = Guid.NewGuid().ToString("N");
            _listeners[key] = listener;
            return new ListenerRegistration(_listeners, key);
        }

        public void Update(HistorySeedingSettings next)
        {
            _current = next;
            foreach (Action<HistorySeedingSettings, string> listener in _listeners.Values)
            {
                listener(next, Options.DefaultName);
            }
        }

        private sealed class ListenerRegistration(
            ConcurrentDictionary<string, Action<HistorySeedingSettings, string>> listeners,
            string key) : IDisposable
        {
            public void Dispose()
            {
                listeners.TryRemove(key, out _);
            }
        }
    }
}
