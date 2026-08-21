using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.HostedServices;

/// <summary>
/// Reconciles the system OrcaSlicer profile catalog in the database against what the worker
/// currently offers, so a deployment converges on its own instead of waiting for an administrator
/// to notice and press a button.
/// </summary>
/// <remarks>
/// <para>
/// Issue #1779 was closed twice while still reproducing in production. Both times the code fix was
/// genuinely correct and genuinely deployed, and both times nothing ever re-ran the seed, so the
/// database kept its incomplete profile set — in that case missing all eight Prusa CORE One /
/// CORE One L high-flow machine profiles, which left <c>/api/slicer/profiles/extended</c>
/// disagreeing with <c>/api/slicer/profiles/machine/for-model/{id}</c> and made high-flow printers
/// impossible to bind in Calibration Setup. Seeding previously reached the database only through
/// an admin-only POST, or through a registration hook that is off by default
/// (<c>SeedProfilesOnRegistration</c>), so "ship the fix" and "fix the data" were separate acts and
/// only the first ever happened.
/// </para>
/// <para>
/// This service closes that gap. It is safe to run on every start because
/// <see cref="Farm.Slicer.Module.Api.Services.ProfilesService.SeedSystemProfilesFromWorkerAsync"/>
/// is non-destructive and idempotent: every candidate is matched against the identity of the rows
/// already present — mirroring each table's UNIQUE index — so an already-complete catalog is a
/// no-op and an incomplete one gains exactly the rows it is missing. It never deletes, and it never
/// touches user-created profiles.
/// </para>
/// <para>
/// <b>Concurrency.</b> A rolling deploy can start several instances at once, and they reconcile
/// against the same tables. That is deliberately handled by convergence rather than by a lock,
/// because a lock here could only be advisory: the settings-backed lock is a non-atomic
/// read-then-write and would add a stranding failure mode without actually guaranteeing exclusion.
/// Instead the losing instance is harmless by construction — identity matching means it stages only
/// rows it believes are missing, and if the winner committed them in between, the database's UNIQUE
/// indexes reject the insert, the repositories detach the failed entity (so one rejection cannot
/// poison the rows behind it), and the batch falls back to per-row inserts. The outcome is no
/// duplicates and no escaping exception, which is exactly what the reconciler needs. Two dedicated
/// tests pin this: one drives the seed against a database where another instance already wrote
/// every row under hashes this instance cannot reproduce, and one proves idempotency holds even
/// when every content hash changes.
/// </para>
/// <para>
/// <b>Startup cost and failure semantics.</b> This is a <see cref="BackgroundService"/> whose first
/// action is an <c>await</c>, so it never blocks host startup or readiness — reconciliation happens
/// alongside a serving application. Every failure is caught and logged rather than rethrown, which
/// matters because .NET's default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>:
/// letting a seeding error escape would turn an incomplete profile catalog, a cosmetic gap, into an
/// outage. On failure the admin endpoint remains available and the next start retries.
/// </para>
/// </remarks>
public sealed class SystemProfileReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemProfileReconciliationService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _workerWaitTimeout;
    private readonly TimeSpan _workerPollInterval;
    private readonly TimeSpan _workerFreshness;

    public SystemProfileReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<SystemProfileReconciliationService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(configuration);
        _enabled = configuration.GetValue("SystemProfileReconciliation:Enabled", true);
        _startupDelay = TimeSpan.FromSeconds(configuration.GetValue("SystemProfileReconciliation:StartupDelaySeconds", 20));
        _workerWaitTimeout = TimeSpan.FromMinutes(configuration.GetValue("SystemProfileReconciliation:WorkerWaitMinutes", 10));
        _workerPollInterval = TimeSpan.FromSeconds(configuration.GetValue("SystemProfileReconciliation:WorkerPollSeconds", 30));
        _workerFreshness = TimeSpan.FromMinutes(configuration.GetValue("SystemProfileReconciliation:WorkerFreshnessMinutes", 15));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("[SystemProfileReconciliation] Disabled via configuration (SystemProfileReconciliation:Enabled=false)");
            return;
        }

        try
        {
            // Let the app finish starting before adding load, and give a co-deployed worker a chance
            // to register itself.
            await Task.Delay(_startupDelay, stoppingToken);
            await ReconcileAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Never take the host down over profile reconciliation; the admin endpoint remains as a
            // manual fallback and the next start will try again.
            _logger.LogError(ex, "[SystemProfileReconciliation] Reconciliation failed; profiles may be incomplete until the next start or a manual seed");
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.Add(_workerWaitTimeout);
        Exception? lastFailure = null;

        // Retry the whole attempt — worker discovery AND seeding — until the deadline. A worker that
        // is up but still preloading its profile catalog will reject or fail the first fetch, and a
        // single attempt would then leave the deployment unreconciled until the next restart, which
        // is the failure mode this service exists to eliminate (#1779).
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Discovery is inside the retry: a transient registry/database failure here must be
                // retried like any other, not allowed to escape and defer reconciliation until the
                // next restart — that is the very failure mode this service exists to remove.
                if (await TryFindEligibleWorkerAsync(ct))
                {
                    if (await TrySeedAsync(ct))
                    {
                        return;
                    }

                    lastFailure = null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                _logger.LogWarning(
                    "[SystemProfileReconciliation] Reconciliation attempt failed ({Message}); will retry until {Deadline:o}",
                    ex.Message,
                    deadline);
            }

            if (DateTime.UtcNow >= deadline)
            {
                if (lastFailure is not null)
                {
                    _logger.LogError(
                        lastFailure,
                        "[SystemProfileReconciliation] Gave up reconciling the system profile catalog; it may be incomplete until the next start or a manual seed");
                }
                else
                {
                    _logger.LogInformation("[SystemProfileReconciliation] No eligible OrcaSlicer worker became available; nothing to reconcile against");
                }

                return;
            }

            await Task.Delay(_workerPollInterval, ct);
        }
    }

    /// <summary>
    /// Runs one seed pass. Returns false when the seed reported failures, so the caller retries
    /// within its window instead of mistaking an incomplete catalog for a complete one.
    /// </summary>
    private async Task<bool> TrySeedAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IProfilesService? profilesService = scope.ServiceProvider.GetService<IProfilesService>();
        if (profilesService is null)
        {
            _logger.LogInformation("[SystemProfileReconciliation] Profiles service not registered; skipping");
            return true;
        }

        IMachineProfileRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
        int before = (await machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, ct)).Count;

        using HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
        object result = await profilesService.SeedSystemProfilesFromWorkerAsync(httpClient, ct);

        int after = (await machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, ct)).Count;

        if (!TryReadErrorCount(result, out int errors))
        {
            // Fail closed: an unrecognised result shape is not evidence of success, and treating it
            // as zero errors is exactly how an incomplete catalog would look complete.
            _logger.LogWarning("[SystemProfileReconciliation] Seed returned an unrecognised result shape; not treating the catalog as complete");
            return false;
        }

        if (errors > 0)
        {
            _logger.LogWarning(
                "[SystemProfileReconciliation] Seed reported {Errors} failed profile(s); machine profiles {Before} -> {After}. Not treating the catalog as complete",
                errors,
                before,
                after);
            return false;
        }

        if (after != before)
        {
            _logger.LogInformation(
                "[SystemProfileReconciliation] Backfilled system machine profiles: {Before} -> {After}",
                before,
                after);
        }
        else
        {
            _logger.LogInformation("[SystemProfileReconciliation] System profile catalog already complete ({Count} machine profiles)", after);
        }

        return true;
    }

    /// <summary>
    /// Reads the seed's <c>errors</c> count from its result. Returns false when the member is
    /// absent or not an <see cref="int"/>, so the caller can fail closed rather than infer success
    /// from a shape it does not recognise.
    /// </summary>
    private static bool TryReadErrorCount(object? result, out int errors)
    {
        errors = 0;
        object? value = result?.GetType().GetProperty("errors")?.GetValue(result);
        if (value is not int count)
        {
            return false;
        }

        errors = count;
        return true;
    }

    /// <summary>
    /// Reports whether a worker is registered that is actually usable for profile import.
    /// </summary>
    /// <remarks>
    /// The eligibility rules deliberately mirror the ones the import paths themselves apply — a
    /// supported OrcaSlicer version, an attested upstream-slicer capability, and a host — plus a
    /// freshness check. Accepting any row with a host would let a stale registration left behind by
    /// a removed worker satisfy the wait immediately, so the bounded retry window would be spent
    /// against a worker that is not there.
    /// </remarks>
    private async Task<bool> TryFindEligibleWorkerAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISlicersService? slicersService = scope.ServiceProvider.GetService<ISlicersService>();
        if (slicersService is null)
        {
            return false;
        }

        IReadOnlyList<SlicerService> workers = await slicersService.ListAsync(ct);
        DateTime staleBefore = DateTime.UtcNow.Subtract(_workerFreshness);

        return workers.Any(s =>
            s.SlicerType == 1 &&
            !string.IsNullOrWhiteSpace(s.Host) &&
            OrcaSlicerProfileCompatibility.IsSupportedVersion(s.Version) &&
            CalibrationContractConstants.AttestsUpstreamSlicer(s.CapabilitiesJson) &&
            !string.Equals(s.Status, "Offline", StringComparison.OrdinalIgnoreCase) &&
            s.LastSeen >= staleBefore);
    }
}
