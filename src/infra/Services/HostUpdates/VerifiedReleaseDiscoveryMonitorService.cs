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
/// registers with <see cref="IBackgroundServiceMonitor"/>, delays before its first iteration (so
/// short-lived hosts/tests never trigger a real discovery round), re-reads its enable/interval
/// configuration every iteration, and exposes an <c>internal</c> method
/// (<see cref="DiscoverAndCacheAsync"/>) so tests can exercise discovery without running the
/// polling loop.
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

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        VerifiedReleaseDiscoveryOptions options = _optionsMonitor.CurrentValue;

        _serviceMonitor.Register(
            ServiceId,
            "Verified Release Discovery",
            "Discovers and caches independently verified (signed, Cosign-checked) release evidence for the selected update channel",
            "HostUpdates",
            "pf-icon-shield",
            options.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!options.Enabled)
        {
            _logger.LogInformation("[VerifiedReleaseDiscovery] Disabled via configuration");
            _serviceMonitor.ReportEnabled(ServiceId, false);
            return;
        }

        _serviceMonitor.ReportEnabled(ServiceId, true);
        _logger.LogInformation(
            "[VerifiedReleaseDiscovery] Started. Interval: {Interval}s",
            options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                options = _optionsMonitor.CurrentValue;
                if (!options.Enabled)
                {
                    _logger.LogInformation("[VerifiedReleaseDiscovery] Disabled, pausing");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await DiscoverAndCacheAsync(stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, options.IntervalSeconds);
            }
            catch (OperationCanceledException)
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
                _cache.SetError(ex.Message);
                _serviceMonitor.ReportError(ServiceId, ex.Message);
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
    internal async Task DiscoverAndCacheAsync(CancellationToken ct)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();

        // The channel is the one genuinely user-editable, persisted setting for this feature
        // (issue #2757 item 2) — read live via ISettingsService, NOT via IOptionsMonitor, which
        // only reflects static configuration and never DB-persisted settings writes.
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        UpdateChannelSettings channelSettings = settingsService.Get<UpdateChannelSettings>();
        string channel = channelSettings.Channel;

        IHostUpdateMetadataProvider metadataProvider = scope.ServiceProvider.GetRequiredService<IHostUpdateMetadataProvider>();

        try
        {
            SignedReleaseMetadata metadata = await metadataProvider.GetCurrentAsync(channel, ct);
            VerifiedReleaseEvidenceDto evidence = metadata.ToEvidenceDto();
            _cache.SetVerified(evidence, DateTimeOffset.UtcNow);
            _logger.LogInformation(
                "[VerifiedReleaseDiscovery] Cached verified release for channel '{Channel}' (releaseId={ReleaseId}, sequence={Sequence})",
                channel,
                metadata.Identity.ReleaseId,
                metadata.Sequence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Discovery/verification failure for THIS round only. Explicit failure log +
            // recorded error; the cache's previously verified evidence (if any) is retained.
            _logger.LogError(
                ex,
                "[VerifiedReleaseDiscovery] Failed to discover/verify a release for channel '{Channel}'",
                channel);
            _cache.SetError(ex.Message);
            throw;
        }
    }
}
