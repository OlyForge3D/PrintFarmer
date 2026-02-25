using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

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
            return;
        }

        _serviceMonitor.ReportEnabled(ServiceId, true);
        _logger.LogInformation(
            "History seeding service started. Interval: {SettingsIntervalMinutes}m, Initial delay: {SettingsInitialDelaySeconds}s (fetches all available history)", settings.IntervalMinutes, settings.InitialDelaySeconds);

        // Initial delay to let the system stabilize
        await Task.Delay(TimeSpan.FromSeconds(settings.InitialDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                settings = _settingsMonitor.CurrentValue; // Reload settings each iteration
                if (!settings.Enabled)
                {
                    _logger.LogInformation("History seeding disabled, pausing service");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Check again in 1 minute
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
