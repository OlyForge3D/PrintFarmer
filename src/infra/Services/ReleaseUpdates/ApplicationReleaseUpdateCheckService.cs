using Farm.Infrastructure.Services.Background;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.ReleaseUpdates;

/// <summary>
/// Periodically checks GitHub for a newer PrintFarmer release on the installed version's
/// channel (issue #3281) and caches the result in <see cref="ApplicationReleaseUpdateState"/>.
/// Notification only: it never downloads, stages, or applies an update. Non-release builds
/// (no parseable installed version) never contact GitHub.
/// </summary>
public sealed class ApplicationReleaseUpdateCheckService(
    ILogger<ApplicationReleaseUpdateCheckService> logger,
    IOptionsMonitor<ApplicationReleaseUpdateOptions> optionsMonitor,
    IBackgroundServiceMonitor serviceMonitor,
    ApplicationReleaseUpdateState state,
    IApplicationReleaseSource releaseSource) : BackgroundService
{
    private const string ServiceId = "ApplicationReleaseUpdateCheckService";

    private readonly ILogger<ApplicationReleaseUpdateCheckService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<ApplicationReleaseUpdateOptions> _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));
    private readonly ApplicationReleaseUpdateState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly IApplicationReleaseSource _releaseSource = releaseSource ?? throw new ArgumentNullException(nameof(releaseSource));

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ApplicationReleaseUpdateOptions options = _optionsMonitor.CurrentValue;
        _serviceMonitor.Register(
            ServiceId,
            "Release Update Check",
            "Checks GitHub for a newer PrintFarmer release on the installed channel and alerts farm admins",
            "HostUpdates",
            "pf-icon-download",
            options.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            options = _optionsMonitor.CurrentValue;
            bool active = options.Enabled && _state.InstalledVersion is not null;
            _state.Configure(options.Enabled, options.IntervalSeconds);
            _serviceMonitor.ReportEnabled(ServiceId, active);

            try
            {
                if (active)
                {
                    await RunCheckAsync(options.IntervalSeconds, stoppingToken).ConfigureAwait(false);
                }

                await DelayAsync(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    /// <summary>Runs one check and records exactly one outcome. Returns true on success.</summary>
    internal async Task<bool> RunCheckAsync(int intervalSeconds, CancellationToken stoppingToken)
    {
        ApplicationReleaseVersion? installed = _state.InstalledVersion;
        if (installed is null)
        {
            return false;
        }

        try
        {
            ApplicationReleaseInfo? latest = await _releaseSource
                .GetLatestReleaseAsync(installed.Channel, stoppingToken)
                .ConfigureAwait(false);
            _state.RecordSuccess(latest);
            _serviceMonitor.ReportSuccess(ServiceId, intervalSeconds);
            if (latest is not null && latest.Version > installed)
            {
                _logger.LogInformation(
                    "[ReleaseUpdateCheck] PrintFarmer {Latest} is available ({Channel} channel; installed {Installed})",
                    latest.Version.Tag,
                    installed.Channel,
                    installed.Tag);
            }

            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure (HTTP error, timeout, oversize/unreadable payload) is reported
            // explicitly; the previously discovered release is retained by the state cache.
            string message = ex switch
            {
                OperationCanceledException => "GitHub release check timed out.",
                HttpRequestException or InvalidDataException => ex.Message,
                _ => "GitHub release check failed unexpectedly.",
            };
            _logger.LogWarning(ex, "[ReleaseUpdateCheck] Release check failed: {Error}", message);
            _state.RecordFailure(message);
            _serviceMonitor.ReportError(ServiceId, message);
            return false;
        }
    }
}
