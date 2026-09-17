using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Monitored background service (issue #2757) that periodically discovers the currently
/// selected release channel's latest signed GitHub release, verifies it with Cosign, and — only
/// on full success — caches the resulting <see cref="VerifiedReleaseEvidenceDto"/> in
/// <see cref="IVerifiedReleaseEvidenceCache"/> for <c>SystemInfoService</c>/
/// <c>ReleaseReadinessEvaluator</c> to consume.
/// <para>
/// Modeled directly on <see cref="Farm.Infrastructure.Services.Catalog.CatalogUpdateDetectionService"/>:
/// registers with <see cref="IBackgroundServiceMonitor"/>, re-reads its enable/interval
/// configuration on every iteration, and exposes an <c>internal</c> method
/// (<see cref="DiscoverAndCacheAsync"/>) so tests can exercise discovery without running the
/// polling loop. Unlike an earlier revision of this service, the loop no longer delays before
/// its first iteration: it evaluates <c>Enabled</c> and, when enabled, runs one discovery round
/// immediately on <see cref="ExecuteAsync"/> entry, then delays for the configured interval
/// before repeating. Tests still avoid triggering a real discovery round by calling
/// <see cref="DiscoverAndCacheAsync"/> directly rather than starting the polling loop.
/// </para>
/// <para>
/// <b>Scope boundary (issue #2757, production discovery slice):</b> this service ONLY
/// discovers and caches verified release metadata/readiness evidence. It never stages,
/// downloads, applies, or recovers an update, and never performs any unattended action —
/// those remain out of scope for this PR. A discovery failure is logged explicitly via
/// <c>ILogger.LogError</c> and reported to <see cref="IBackgroundServiceMonitor.ReportError"/>;
/// the previously cached evidence (if any) is deliberately retained rather than cleared — see
/// <see cref="IVerifiedReleaseEvidenceCache"/>'s remarks. There is no success-shaped fallback: a
/// failure is never silently treated as "nothing changed, still eligible".
/// </para>
/// </summary>
public class VerifiedReleaseDiscoveryMonitorService(
    IServiceProvider serviceProvider,
    ILogger<VerifiedReleaseDiscoveryMonitorService> logger,
    IOptionsMonitor<VerifiedReleaseDiscoveryOptions> optionsMonitor,
    IBackgroundServiceMonitor serviceMonitor,
    IVerifiedReleaseEvidenceCache cache) : BackgroundService
{
    private const string ServiceId = "VerifiedReleaseDiscoveryMonitorService";

    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<VerifiedReleaseDiscoveryMonitorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<VerifiedReleaseDiscoveryOptions> _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));
    private readonly IVerifiedReleaseEvidenceCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        VerifiedReleaseDiscoveryOptions options = new();

        _serviceMonitor.Register(
            ServiceId,
            "Verified Release Discovery",
            "Discovers and caches independently verified (signed, Cosign-checked) release evidence for the selected update channel",
            "HostUpdates",
            "pf-icon-shield",
            options.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalSeconds = options.IntervalSeconds;
            try
            {
                options = _optionsMonitor.CurrentValue;
                intervalSeconds = options.IntervalSeconds;
                if (options.Enabled)
                {
                    _serviceMonitor.ReportEnabled(ServiceId, true);
                    _logger.LogInformation(
                        "[VerifiedReleaseDiscovery] Running. Interval: {Interval}s",
                        options.IntervalSeconds);
                    await RunDiscoveryRoundAsync(options.IntervalSeconds, stoppingToken);
                }
                else
                {
                    _logger.LogInformation("[VerifiedReleaseDiscovery] Disabled, pausing");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("[VerifiedReleaseDiscovery] Stopping");
                break;
            }
            catch (Exception ex)
            {
                // Explicit, non-success-shaped failure reporting. The cache retains whatever
                // evidence it already had — see IVerifiedReleaseEvidenceCache remarks — so a
                // transient outage does not regress readiness evaluation to "no evidence".
                _logger.LogError(ex, "[VerifiedReleaseDiscovery] Unhandled error during discovery");
                RecordFailure(ex);
            }

            try
            {
                await DelayAsync(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("[VerifiedReleaseDiscovery] Stopping");
                break;
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    /// <summary>
    /// Discovers and verifies the latest release for the currently selected
    /// <see cref="UpdateChannelSettings.Channel"/>, and caches it on success. <c>internal</c> (not
    /// <c>private</c>) so that <c>Farm.Infrastructure.Tests</c> — granted access via
    /// <c>InternalsVisibleTo</c> — can exercise a single discovery round directly, exactly like
    /// <c>CatalogUpdateDetectionService.DetectAndHandleUpdatesAsync</c>.
    /// </summary>
    internal async Task<VerifiedReleaseDiscoveryOutcome> DiscoverAndCacheAsync(CancellationToken ct)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();

        // The channel is the one genuinely user-editable, persisted setting for this feature
        // (issue #2757 item 2) — read live via ISettingsService, NOT via IOptionsMonitor, which
        // only reflects static configuration and never DB-persisted settings writes.
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        UpdateChannelSettings channelSettings = settingsService.Get<UpdateChannelSettings>();
        string channel = channelSettings.Channel;

        IHostUpdateMetadataProvider metadataProvider = scope.ServiceProvider.GetRequiredService<IHostUpdateMetadataProvider>();
        IVerifiedReleaseManifestBindingStore bindingStore =
            scope.ServiceProvider.GetRequiredService<IVerifiedReleaseManifestBindingStore>();

        SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(channel, ct);
        VerifiedReleaseEvidenceDto evidence = metadata.ToEvidenceDto();
        await bindingStore.EnsureBoundAsync(
            metadata.Identity.ReleaseId,
            metadata.Identity.ManifestDigest,
            ct);
        if (!_cache.SetVerified(evidence, DateTimeOffset.UtcNow))
        {
            string message = $"Rejected rollback release for channel '{channel}' (sequence={metadata.Sequence}).";
            _logger.LogError("[VerifiedReleaseDiscovery] {Message}", message);
            _cache.SetError(message);
            return VerifiedReleaseDiscoveryOutcome.Failure(message);
        }

        _logger.LogInformation(
            "[VerifiedReleaseDiscovery] Cached verified release for channel '{Channel}' (releaseId={ReleaseId}, sequence={Sequence})",
            channel,
            metadata.Identity.ReleaseId,
            metadata.Sequence);
        return VerifiedReleaseDiscoveryOutcome.Success;
    }

    /// <summary>Runs one monitored discovery round and reports exactly one outcome.</summary>
    internal async Task<bool> RunDiscoveryRoundAsync(int intervalSeconds, CancellationToken stoppingToken)
    {
        try
        {
            VerifiedReleaseDiscoveryOutcome outcome = await DiscoverAndCacheAsync(stoppingToken);
            if (!outcome.Succeeded)
            {
                _serviceMonitor.ReportError(ServiceId, outcome.Error);
                return false;
            }

            _serviceMonitor.ReportSuccess(ServiceId, intervalSeconds);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return false;
        }
    }

    private void RecordFailure(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Verified release discovery failed without an error message."
            : exception.Message;
        _logger.LogError(exception, "[VerifiedReleaseDiscovery] Failed to discover/verify a release");
        _cache.SetError(message);
        _serviceMonitor.ReportError(ServiceId, message);
    }
}

/// <summary>Represents one cache attempt with a non-null failure message.</summary>
internal readonly record struct VerifiedReleaseDiscoveryOutcome(bool Succeeded, string Error)
{
    public static VerifiedReleaseDiscoveryOutcome Success { get; } = new(true, string.Empty);

    public static VerifiedReleaseDiscoveryOutcome Failure(string error) => new(false, error);
}
