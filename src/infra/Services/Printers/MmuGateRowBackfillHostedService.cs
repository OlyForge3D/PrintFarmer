using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// One-shot startup backfill that repairs missing persisted MMU gate rows (issue #3323).
/// Multi-material printers created before gate gap-fill existed (e.g. an AD5X persisted with
/// only Gate 1-3) never had <see cref="IPrintersService.EnsureMmuToolheadsAsync(Guid, CancellationToken)"/> invoked, so
/// their fourth gate row was never created. The repair is idempotent and race-safe, so running
/// it on every startup is harmless once all printers are repaired.
/// </summary>
public class MmuGateRowBackfillHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MmuGateRowBackfillHostedService> _logger;

    public MmuGateRowBackfillHostedService(
        IServiceProvider serviceProvider,
        ILogger<MmuGateRowBackfillHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let migrations/seeding finish before touching printer rows.
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _ = await RunBackfillAsync(stoppingToken);
    }

    /// <summary>
    /// Runs the gate-row repair once in a fresh DI scope. Never throws: cancellation returns 0
    /// and any other failure is logged and returns -1. Otherwise returns the number of
    /// multi-material printers repaired successfully.
    /// </summary>
    public async Task<int> RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            IPrintersService printers = scope.ServiceProvider.GetRequiredService<IPrintersService>();

            int repaired = await printers.RepairMissingMmuGatesAsync(ct);
            _logger.LogInformation("[MmuGateBackfill] Checked MMU gate rows; {Count} multi-material printer(s) verified", repaired);
            return repaired;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MmuGateBackfill] MMU gate row backfill failed");
            return -1;
        }
    }
}
