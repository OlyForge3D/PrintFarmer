using System.Diagnostics.CodeAnalysis;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Reconciles process-local overlay links and caches when a sibling worker
/// changes the shared custom profile volume.
/// </summary>
public sealed class CustomProfilesReconciliationService(
    CustomProfileBundleStore bundleStore,
    CachedOrcaProfilesService profilesService,
    CustomProfilesReconciliationState state,
    IConfiguration configuration,
    ILogger<CustomProfilesReconciliationService> logger) : BackgroundService
{
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The dependency-injection container owns and disposes this singleton.")]
    private readonly CustomProfileBundleStore _bundleStore =
        bundleStore ?? throw new ArgumentNullException(nameof(bundleStore));

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The dependency-injection container owns and disposes this singleton.")]
    private readonly CachedOrcaProfilesService _profilesService =
        profilesService ?? throw new ArgumentNullException(nameof(profilesService));

    private readonly CustomProfilesReconciliationState _state =
        state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILogger<CustomProfilesReconciliationService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(
        Math.Max(
            1,
            (configuration
                ?? throw new ArgumentNullException(nameof(configuration)))
            .GetValue("CustomProfiles:ReconciliationIntervalSeconds", 1)));

    private string? _observedFingerprint;

    private bool _firstAttempt = true;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                const string failure =
                    "Custom profile reconciliation failed; inspect worker logs.";
                _state.MarkUnavailable(failure);
                _logger.LogError(ex, "{Failure}", failure);
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    internal async Task CheckForChangesAsync(CancellationToken ct)
    {
        string fingerprint =
            _bundleStore.CalculateCustomProfilesFingerprint();
        if (string.Equals(
            fingerprint,
            _state.AppliedFingerprint,
            StringComparison.Ordinal))
        {
            _firstAttempt = false;
            _observedFingerprint = null;
            _state.MarkReady(fingerprint);
            return;
        }

        if (_firstAttempt
            || string.Equals(
                fingerprint,
                _observedFingerprint,
                StringComparison.Ordinal))
        {
            _firstAttempt = false;
            _observedFingerprint = fingerprint;
            await ReconcileAsync(fingerprint, ct);
            _observedFingerprint = null;
            return;
        }

        _firstAttempt = false;
        _observedFingerprint = fingerprint;
        _state.MarkUnavailable(
            "Shared custom profiles changed; local reconciliation is pending.");
    }

    internal async Task ReconcileAsync(
        string fingerprint,
        CancellationToken ct)
    {
        (_, ProfileReloadResult reload) =
            await _profilesService.MutateAndReloadProfilesAsync(
                _bundleStore.ReconcileOverlayAsync,
                ct);
        _state.MarkReady(fingerprint);
        foreach (CustomProfileLoadFailure failure in reload.Failures)
        {
            _logger.LogWarning(
                "Custom profile {ProfileName} in bundle {BundleName} remains quarantined because parent {MissingParent} is unavailable",
                failure.ProfileName,
                failure.BundleName,
                failure.MissingParent);
        }

        _logger.LogInformation(
            "Custom profile overlay and caches synchronized at {Fingerprint}; {FailureCount} invalid custom profiles remain quarantined",
            fingerprint,
            reload.Failures.Count);
    }
}
