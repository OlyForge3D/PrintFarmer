using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Configuration settings for the history seeding background service.
/// This setting is persisted to the database and can be managed from the Settings UI.
/// </summary>
[AppSetting(HistorySeedingSettings.SectionName)]
[SettingDisplay(Name = "History Seeding", Description = "Automatically import print job history from connected printers.", Icon = "pf-icon-history", Group = "Job Queue", Order = 3)]
public class HistorySeedingSettings : IAppSetting
{
    public const string SectionName = "HistorySeeding";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Whether history seeding is enabled. Default: true
    /// </summary>
    [JsonPropertyName("enabled")]
    [SettingDisplay(Name = "Enabled", Description = "Enable automatic history seeding from printers.", InputType = SettingInputType.Boolean, Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval between seeding runs in minutes. Default: 15
    /// </summary>
    [JsonPropertyName("intervalMinutes")]
    [SettingDisplay(Name = "Interval (Minutes)", Description = "Time between history seeding runs.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 1440, Order = 2)]
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Initial delay before first seeding run in seconds. Default: 60
    /// </summary>
    [JsonPropertyName("initialDelaySeconds")]
    [SettingDisplay(Name = "Initial Delay (Seconds)", Description = "Delay before the first seeding run after startup.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 3600, Order = 3)]
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Whether active external job sync is enabled. Default: true
    /// </summary>
    [JsonPropertyName("activeSyncEnabled")]
    [SettingDisplay(Name = "Active Sync Enabled", Description = "Enable faster sync for active external jobs.", InputType = SettingInputType.Boolean, Order = 4)]
    public bool ActiveSyncEnabled { get; set; } = true;

    /// <summary>
    /// Interval between active external sync runs in seconds. Default: 60
    /// </summary>
    [JsonPropertyName("activeSyncIntervalSeconds")]
    [SettingDisplay(Name = "Active Sync Interval (Seconds)", Description = "Time between active external job sync runs.", InputType = SettingInputType.Number, MinValue = 15, MaxValue = 3600, Order = 5)]
    public int ActiveSyncIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Initial delay before first active sync run in seconds. Default: 30
    /// </summary>
    [JsonPropertyName("activeSyncInitialDelaySeconds")]
    [SettingDisplay(Name = "Active Sync Initial Delay (Seconds)", Description = "Delay before the first active external sync run after startup.", InputType = SettingInputType.Number, MinValue = 0, MaxValue = 3600, Order = 6)]
    public int ActiveSyncInitialDelaySeconds { get; set; } = 30;
}

/// <summary>
/// Background service that periodically seeds print job history from connected printers.
/// This ensures jobs dispatched outside of PrintFarmer (e.g., via Mainsail/Fluidd/PrusaSlicer)
/// are captured in the job queue history for analytics and reporting.
///
/// Jobs are identified by (ExternalJobId, SourcePrinterId) composite key to prevent duplicates.
/// Existing jobs are updated with the latest data from the printer.
/// </summary>
public class HistorySeedingBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<HistorySeedingSettings> logger,
    IOptionsMonitor<HistorySeedingSettings> settingsMonitor,
    IBackgroundServiceMonitor serviceMonitor) : BackgroundService
{
    private const string ServiceId = "HistorySeedingService";
    private static readonly TimeSpan DisabledSettingsPollInterval = TimeSpan.FromSeconds(5);
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<HistorySeedingSettings> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<HistorySeedingSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HistorySeedingSettings settings = _settingsMonitor.CurrentValue;

        // Register with the service monitor for dashboard visibility
        _serviceMonitor.Register(
            ServiceId,
            "History Seeding",
            "Imports print job history from connected printers",
            "Job Queue",
            "pf-icon-history",
            settings.IntervalMinutes * 60);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!settings.Enabled)
        {
            _logger.LogInformation("[HistorySeedingService] Disabled via configuration");
            _serviceMonitor.ReportEnabled(ServiceId, false);
        }
        else
        {
            _serviceMonitor.ReportEnabled(ServiceId, true);
            _logger.LogInformation(
                "History seeding service started. Interval: {SettingsIntervalMinutes}m, Initial delay: {SettingsInitialDelaySeconds}s (fetches all available history)", settings.IntervalMinutes, settings.InitialDelaySeconds);

            // Initial delay to let the system stabilize on enabled startup.
            await Task.Delay(TimeSpan.FromSeconds(settings.InitialDelaySeconds), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("History seeding disabled, pausing service");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    await Task.Delay(DisabledSettingsPollInterval, stoppingToken);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await SeedHistoryAsync(stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, settings.IntervalMinutes * 60);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HistorySeedingService] Error during history seeding");
                _serviceMonitor.ReportError(ServiceId, ex.Message);
            }

            // Wait for next interval
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.IntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
        _logger.LogInformation("[HistorySeedingService] Stopped");
    }

    private async Task SeedHistoryAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("[HistorySeedingService] Starting history seeding run");

        using IServiceScope scope = _serviceProvider.CreateScope();
        IPrintJobManagementService jobService = scope.ServiceProvider.GetRequiredService<IPrintJobManagementService>();

        await jobService.SeedHistoryFromPrintersAsync(
            printerIds: null, // Seed from all enabled printers
            cancellationToken: cancellationToken);

        _logger.LogDebug("[HistorySeedingService] History seeding run completed");
    }
}

/// <summary>
/// Background service that periodically syncs active non-terminal external jobs from connected printers.
/// This runs on a shorter cadence than history seeding to discover externally-started active work sooner.
/// </summary>
public class ActiveExternalJobSyncBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<HistorySeedingSettings> logger,
    IOptionsMonitor<HistorySeedingSettings> settingsMonitor,
    IBackgroundServiceMonitor serviceMonitor) : BackgroundService
{
    private const string ServiceId = "ActiveExternalJobSyncService";
    private static readonly TimeSpan DisabledSettingsPollInterval = TimeSpan.FromSeconds(5);
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<HistorySeedingSettings> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<HistorySeedingSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HistorySeedingSettings settings = _settingsMonitor.CurrentValue;

        _serviceMonitor.Register(
            ServiceId,
            "Active External Job Sync",
            "Syncs active external jobs from connected printers",
            "Job Queue",
            "pf-icon-history",
            settings.ActiveSyncIntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!settings.ActiveSyncEnabled)
        {
            _logger.LogInformation("[ActiveExternalJobSyncService] Disabled via configuration");
            _serviceMonitor.ReportEnabled(ServiceId, false);
        }
        else
        {
            _serviceMonitor.ReportEnabled(ServiceId, true);
            _logger.LogInformation(
                "Active external job sync service started. Interval: {SettingsActiveSyncIntervalSeconds}s, Initial delay: {SettingsActiveSyncInitialDelaySeconds}s", settings.ActiveSyncIntervalSeconds, settings.ActiveSyncInitialDelaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(settings.ActiveSyncInitialDelaySeconds), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                settings = _settingsMonitor.CurrentValue;
                if (!settings.ActiveSyncEnabled)
                {
                    _logger.LogInformation("Active external job sync disabled, pausing service");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    await Task.Delay(DisabledSettingsPollInterval, stoppingToken);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await SyncActiveExternalJobsAsync(stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, settings.ActiveSyncIntervalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ActiveExternalJobSyncService] Error during active external job sync");
                _serviceMonitor.ReportError(ServiceId, ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.ActiveSyncIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
        _logger.LogInformation("[ActiveExternalJobSyncService] Stopped");
    }

    private async Task SyncActiveExternalJobsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("[ActiveExternalJobSyncService] Starting active external job sync run");

        using IServiceScope scope = _serviceProvider.CreateScope();
        IPrintJobManagementService jobService = scope.ServiceProvider.GetRequiredService<IPrintJobManagementService>();

        await jobService.SyncActiveExternalJobsFromPrintersAsync(
            printerIds: null,
            cancellationToken: cancellationToken);

        _logger.LogDebug("[ActiveExternalJobSyncService] Active external job sync run completed");
    }
}
