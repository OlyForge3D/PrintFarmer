using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Monitors the discovery microservice heartbeat and reports its status
/// to the background service monitor so it appears in the dashboard widget.
/// </summary>
public class DiscoveryHeartbeatMonitorService(
    IBackgroundServiceMonitor serviceMonitor,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<DiscoveryHeartbeatMonitorService> logger) : BackgroundService
{
    public const string ServiceId = "DiscoveryService";
    private const int CheckIntervalSeconds = 30;
    private const int HeartbeatStaleSeconds = 90; // Consider stale if no heartbeat in 90s

    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly ILogger<DiscoveryHeartbeatMonitorService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceMonitor.Register(
            ServiceId,
            "Printer Discovery",
            "External microservice that scans the network for 3D printers",
            "Discovery",
            "pf-icon-network",
            CheckIntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool enabled = IsDiscoveryEnabled();
                _serviceMonitor.ReportEnabled(ServiceId, enabled);

                if (enabled)
                {
                    CheckHeartbeat();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking discovery service heartbeat");
            }

            await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    /// <summary>
    /// Called by the settings controller when a heartbeat is received.
    /// </summary>
    public void OnHeartbeatReceived()
    {
        _serviceMonitor.ReportSuccess(ServiceId, CheckIntervalSeconds);
    }

    private void CheckHeartbeat()
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (settingsService.GetByKey(NetworkDiscoverySettings.SectionName) is not NetworkDiscoverySettings settings)
        {
            return;
        }

        if (settings.LastHeartbeat is null)
        {
            _serviceMonitor.ReportError(ServiceId, "No heartbeat received — discovery service may not be running");
            return;
        }

        TimeSpan age = DateTime.UtcNow - settings.LastHeartbeat.Value;
        if (age.TotalSeconds > HeartbeatStaleSeconds)
        {
            _serviceMonitor.ReportError(ServiceId, $"Last heartbeat {age.TotalSeconds:F0}s ago — discovery service may be down");
        }
    }

    private bool IsDiscoveryEnabled()
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        return settingsService.GetByKey(NetworkDiscoverySettings.SectionName) is NetworkDiscoverySettings settings
            && settings.EnableDiscovery;
    }
}
