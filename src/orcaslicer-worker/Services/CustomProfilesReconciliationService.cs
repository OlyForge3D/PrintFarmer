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

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? observedFingerprint = null;
        bool firstAttempt = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string fingerprint =
                    _bundleStore.CalculateCustomProfilesFingerprint();
                if (string.Equals(
                    fingerprint,
                    _state.AppliedFingerprint,
                    StringComparison.Ordinal))
                {
                    observedFingerprint = null;
                    _state.MarkReady(fingerprint);
                }
                else if (firstAttempt
                    || string.Equals(
                        fingerprint,
                        observedFingerprint,
                        StringComparison.Ordinal))
                {
                    await ReconcileAsync(fingerprint, stoppingToken);
                    observedFingerprint = null;
                }
                else
                {
                    observedFingerprint = fingerprint;
                }
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

            firstAttempt = false;
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    internal async Task ReconcileAsync(
        string fingerprint,
        CancellationToken ct)
    {
        (_, ProfileReloadResult reload) =
            await _profilesService.MutateAndReloadProfilesAsync(
                _bundleStore.ReconcileOverlayAsync,
                ct);
        if (reload.Failures.Count > 0)
        {
            CustomProfileLoadFailure failure = reload.Failures[0];
            throw new InvalidOperationException(
                $"Custom profile '{failure.ProfileName}' in bundle " +
                $"'{failure.BundleName}' cannot resolve parent " +
                $"'{failure.MissingParent}'.");
        }

        _state.MarkReady(fingerprint);
        _logger.LogInformation(
            "Custom profile overlay and caches synchronized at {Fingerprint}",
            fingerprint);
    }
}
