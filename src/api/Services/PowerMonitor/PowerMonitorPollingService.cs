using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cost;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Aliases to resolve ambiguity between Farm.Infrastructure.Domain and Farm.Web.Api.Services.SmartPlug
using DomainPowerMonitor = Farm.Infrastructure.Domain.PowerMonitor;
using DomainPowerReading = Farm.Infrastructure.Domain.PowerReading;

namespace Farm.Web.Api.Services.PowerMonitor;

/// <summary>
/// Background service that polls enabled <see cref="DomainPowerMonitor"/> records on a
/// configurable interval, persists <see cref="DomainPowerReading"/> rows, and
/// aggregates measured energy into <see cref="PrintJob.KwhUsed"/> for completed jobs.
/// </summary>
/// <remarks>
/// Poll interval is configured via <c>PFARM__PowerMonitor__PollIntervalSeconds</c> (default: 30).
/// Provider errors are logged and skipped — they never crash the service.
/// On job completion, energy measured during the active print window is summed and stored on
/// the job, then <see cref="IJobCostCalculationService.CalculateAndStoreCostsAsync"/> is called to
/// refresh the cost breakdown using the measured kWh.
/// </remarks>
public class PowerMonitorPollingService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<ISmartPlugProvider> providers,
    IConfiguration configuration,
    ILogger<PowerMonitorPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private TimeSpan PollInterval
    {
        get
        {
            string? raw = configuration["PowerMonitor:PollIntervalSeconds"];
            return int.TryParse(raw, out int seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultPollInterval;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PowerMonitorPollingService started (interval: {Interval}s)", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval = PollInterval;

            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                IJobCostCalculationService costService =
                    scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();

                await PollMonitorsAsync(db, stoppingToken);
                await AggregateCompletedJobsAsync(db, costService, interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PowerMonitorPollingService: unhandled error during poll cycle");
            }

            await Task.Delay(interval, stoppingToken);
        }

        logger.LogInformation("PowerMonitorPollingService stopped");
    }

    /// <summary>
    /// Polls every enabled <see cref="DomainPowerMonitor"/> and persists a
    /// <see cref="DomainPowerReading"/> row for each successful reading.
    /// Provider errors are logged and the monitor is skipped.
    /// </summary>
    private async Task PollMonitorsAsync(AppDbContext db, CancellationToken ct)
    {
        List<DomainPowerMonitor> monitors = await db.PowerMonitors
            .Where(m => m.IsEnabled)
            .ToListAsync(ct);

        if (monitors.Count == 0)
        {
            return;
        }

        DateTime recordedAt = DateTime.UtcNow;

        foreach (DomainPowerMonitor monitor in monitors)
        {
            ct.ThrowIfCancellationRequested();

            ISmartPlugProvider? provider = providers
                .FirstOrDefault(p => string.Equals(p.ProviderType, monitor.ProviderType, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                logger.LogWarning(
                    "PowerMonitorPollingService: no provider registered for type '{ProviderType}' (monitor {MonitorId})",
                    monitor.ProviderType,
                    monitor.Id);
                continue;
            }

            try
            {
                SmartPlug.PowerReading? reading = await provider.GetCurrentReadingAsync(monitor.DeviceAddress, ct);

                if (reading is null)
                {
                    logger.LogDebug(
                        "PowerMonitorPollingService: null reading from {ProviderType} at {Address} (monitor {MonitorId})",
                        monitor.ProviderType,
                        monitor.DeviceAddress,
                        monitor.Id);
                    continue;
                }

                db.PowerReadings.Add(new DomainPowerReading
                {
                    PowerMonitorId = monitor.Id,
                    WattsNow = (decimal)reading.WattsNow,
                    KwhTotal = reading.TotalKwh.HasValue ? (decimal)reading.TotalKwh.Value : null,
                    RecordedAt = recordedAt,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "PowerMonitorPollingService: error reading from {ProviderType} at {Address} (monitor {MonitorId}) — skipping",
                    monitor.ProviderType,
                    monitor.DeviceAddress,
                    monitor.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Finds completed <see cref="PrintJob"/> records whose assigned printer has a
    /// <see cref="DomainPowerMonitor"/> and that do not yet have
    /// <see cref="PrintJob.KwhUsed"/> set. For each, sums the <see cref="DomainPowerReading"/>
    /// rows that fall within the job's active print window, stores the result, then
    /// triggers a cost recalculation.
    /// </summary>
    private async Task AggregateCompletedJobsAsync(
        AppDbContext db,
        IJobCostCalculationService costService,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        List<PrintJob> jobs = await db.PrintJobs
            .Where(j =>
                j.Status == PrintJobStatus.Completed &&
                j.KwhUsed == null &&
                j.ActualStartTime != null &&
                j.ActualEndTime != null &&
                j.AssignedPrinterId != null &&
                db.PowerMonitors.Any(m => m.PrinterId == j.AssignedPrinterId && m.IsEnabled))
            .ToListAsync(ct);

        if (jobs.Count == 0)
        {
            return;
        }

        foreach (PrintJob job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await SetKwhUsedAsync(db, costService, job, pollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "PowerMonitorPollingService: failed to aggregate energy for job {JobId} — skipping",
                    job.Id);
            }
        }
    }

    private async Task SetKwhUsedAsync(
        AppDbContext db,
        IJobCostCalculationService costService,
        PrintJob job,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        List<int> monitorIds = await db.PowerMonitors
            .Where(m => m.PrinterId == job.AssignedPrinterId && m.IsEnabled)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (monitorIds.Count == 0)
        {
            return;
        }

        DateTime start = job.ActualStartTime!.Value;
        DateTime end = job.ActualEndTime!.Value;

        List<decimal> watts = await db.PowerReadings
            .Where(r =>
                monitorIds.Contains(r.PowerMonitorId) &&
                r.RecordedAt >= start &&
                r.RecordedAt <= end)
            .Select(r => r.WattsNow)
            .ToListAsync(ct);

        if (watts.Count == 0)
        {
            logger.LogDebug(
                "PowerMonitorPollingService: no readings in window [{Start:u}, {End:u}] for job {JobId}",
                start,
                end,
                job.Id);
            return;
        }

        // kWh = sum(watts × intervalSeconds) / 3_600_000  (W·s → kWh)
        double intervalSeconds = pollInterval.TotalSeconds;
        decimal kwh = watts.Sum(w => w * (decimal)intervalSeconds / 3_600_000m);

        job.KwhUsed = Math.Round(kwh, 4);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "PowerMonitorPollingService: set KwhUsed={Kwh:F4} for job {JobId} ({Count} readings)",
            job.KwhUsed,
            job.Id,
            watts.Count);

        try
        {
            await costService.CalculateAndStoreCostsAsync(job.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "PowerMonitorPollingService: cost recalculation failed for job {JobId}",
                job.Id);
        }
    }
}
