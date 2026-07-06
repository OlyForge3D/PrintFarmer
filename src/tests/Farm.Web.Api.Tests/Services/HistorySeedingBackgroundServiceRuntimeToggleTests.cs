using System.Collections.Concurrent;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Web.Api.Services.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Services;

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

        Mock<IPrintJobManagementService> jobService = new();
        jobService.Setup(s => s.SeedHistoryFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using ServiceProvider provider = BuildServiceProvider(jobService.Object);
        BackgroundServiceMonitor serviceMonitor = new();
        using HistorySeedingBackgroundService service = new(
            provider,
            NullLogger<HistorySeedingBackgroundService>.Instance,
            monitor,
            serviceMonitor);

        await service.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));
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

        await Task.Delay(TimeSpan.FromSeconds(6));

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

        Mock<IPrintJobManagementService> jobService = new();
        jobService.Setup(s => s.SyncActiveExternalJobsFromPrintersAsync(It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using ServiceProvider provider = BuildServiceProvider(jobService.Object);
        BackgroundServiceMonitor serviceMonitor = new();
        using ActiveExternalJobSyncBackgroundService service = new(
            provider,
            NullLogger<ActiveExternalJobSyncBackgroundService>.Instance,
            monitor,
            serviceMonitor);

        await service.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));
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

        await Task.Delay(TimeSpan.FromSeconds(6));

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
