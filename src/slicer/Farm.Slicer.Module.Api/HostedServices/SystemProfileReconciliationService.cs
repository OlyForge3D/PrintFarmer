using Farm.Infrastructure.Domain;
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
/// </remarks>
public sealed class SystemProfileReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemProfileReconciliationService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _workerWaitTimeout;
    private readonly TimeSpan _workerPollInterval;

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
        if (!await WaitForOrcaSlicerWorkerAsync(ct))
        {
            _logger.LogInformation("[SystemProfileReconciliation] No OrcaSlicer worker became available; nothing to reconcile against");
            return;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IProfilesService? profilesService = scope.ServiceProvider.GetService<IProfilesService>();
        if (profilesService is null)
        {
            _logger.LogInformation("[SystemProfileReconciliation] Profiles service not registered; skipping");
            return;
        }

        IMachineProfileRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineProfileRepository>();
        int before = (await machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, ct)).Count;

        using HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
        _ = await profilesService.SeedSystemProfilesFromWorkerAsync(httpClient, ct);

        int after = (await machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, ct)).Count;

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
    }

    /// <summary>
    /// Polls until a usable OrcaSlicer worker is registered, since a co-deployed worker container
    /// commonly registers after the API is up. Returns false if none appears within the timeout.
    /// </summary>
    private async Task<bool> WaitForOrcaSlicerWorkerAsync(CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.Add(_workerWaitTimeout);

        while (!ct.IsCancellationRequested)
        {
            using (IServiceScope scope = _scopeFactory.CreateScope())
            {
                ISlicersService? slicersService = scope.ServiceProvider.GetService<ISlicersService>();
                if (slicersService is null)
                {
                    return false;
                }

                IReadOnlyList<SlicerService> workers = await slicersService.ListAsync(ct);
                if (workers.Any(s => s.SlicerType == 1 && !string.IsNullOrWhiteSpace(s.Host)))
                {
                    return true;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(_workerPollInterval, ct);
        }

        return false;
    }
}
