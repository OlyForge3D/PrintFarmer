using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Entity Framework implementation of <see cref="IMaintenanceAlertResolutionService"/>. Wraps the
/// gate re-check, log insertion, and alert mutation in a single transaction so a gate that flips
/// after the controller's pre-check cannot leave an orphaned completion log (issue #711, round-7
/// Finding 5).
/// </summary>
/// <remarks>
/// Round-10 hardening (issue #711):
/// <list type="bullet">
///   <item><description>Finding H6: <see cref="ResolveAlertWithCompletionLogAsync"/> builds an
///     authoritative completion log (task, toolhead, and hour baselines) so callers such as the
///     unified attention feed produce a real maintenance completion instead of a status-only
///     transition the alert engine would immediately re-derive as "still due".</description></item>
///   <item><description>Finding H7: resolution is idempotent. An already-resolved alert returns its
///     existing linked completion log without inserting a duplicate, a filtered-unique index on
///     <see cref="MaintenanceLog.ResolvedAlertId"/> makes concurrent duplicates impossible, and
///     attention broadcasting runs after the commit so a broadcast failure cannot fail an already
///     durable resolution.</description></item>
/// </list>
/// </remarks>
public sealed class MaintenanceAlertResolutionService(
    AppDbContext dbContext,
    IOperatorFeatureGate? operatorFeatureGate = null,
    IAttentionBroadcaster? attentionBroadcaster = null,
    IPrinterStatisticsRepository? printerStatisticsRepository = null,
    IToolheadStatisticsRepository? toolheadStatisticsRepository = null,
    ILogger<MaintenanceAlertResolutionService>? logger = null,
    IMaintenanceResolutionNotifier? resolutionNotifier = null) : IMaintenanceAlertResolutionService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    // Optional to mirror MaintenanceAlertEngine: when null the gate is treated as enabled
    // (legacy behavior) so existing constructors and non-gated deployments are unaffected.
    private readonly IOperatorFeatureGate? _operatorFeatureGate = operatorFeatureGate;

    // Optional attention-feed invalidation (issue #707); preserves the retire-on-resolve
    // notification that MaintenanceAlertEngine.ResolveAlertAsync emitted before this service
    // owned the resolve path.
    private readonly IAttentionBroadcaster? _attentionBroadcaster = attentionBroadcaster;

    // Optional stat sources used only by ResolveAlertWithCompletionLogAsync to populate the hour
    // baselines the alert engine needs; null in legacy/unit contexts that pre-build the log.
    private readonly IPrinterStatisticsRepository? _printerStatisticsRepository = printerStatisticsRepository;
    private readonly IToolheadStatisticsRepository? _toolheadStatisticsRepository = toolheadStatisticsRepository;

    private readonly ILogger<MaintenanceAlertResolutionService>? _logger = logger;
    private readonly IMaintenanceResolutionNotifier? _resolutionNotifier = resolutionNotifier;

    /// <inheritdoc />
    public async Task<MaintenanceAlertResolutionResult?> ResolveWithLogAsync(
        Guid alertId,
        MaintenanceLog log,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        MaintenanceAlert? alert = await _dbContext.MaintenanceAlerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert is null)
        {
            return null;
        }

        MaintenanceAlertResolutionResult? terminalResult =
            await EvaluateTerminalStatusAsync(alert, cancellationToken);
        if (terminalResult is not null)
        {
            return terminalResult;
        }

        log.ResolvedAlertId = alertId;

        bool useTransaction = _dbContext.Database.IsRelational();
        IDbContextTransaction? transaction = useTransaction
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        DateTime resolvedAt = DateTime.UtcNow;

        try
        {
            EnsureAlertMutationEnabled(alert);

            if (useTransaction)
            {
                int transitioned = await _dbContext.MaintenanceAlerts
                    .Where(candidate =>
                        candidate.Id == alertId
                        && (candidate.Status == MaintenanceAlertStatus.Active
                            || candidate.Status == MaintenanceAlertStatus.Acknowledged))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(
                                candidate => candidate.Status,
                                MaintenanceAlertStatus.Resolved)
                            .SetProperty(candidate => candidate.ResolvedAt, resolvedAt)
                            .SetProperty(candidate => candidate.ResolvedBy, resolvedBy)
                            .SetProperty(candidate => candidate.UpdatedAt, resolvedAt),
                        cancellationToken);

                if (transitioned == 0)
                {
                    await transaction!.RollbackAsync(cancellationToken);
                    _dbContext.ChangeTracker.Clear();
                    return await ReloadAfterConcurrentTransitionAsync(
                        alertId,
                        cancellationToken);
                }
            }
            else
            {
                MaintenanceAlert? trackedAlert = await _dbContext.MaintenanceAlerts
                    .FirstOrDefaultAsync(candidate => candidate.Id == alertId, cancellationToken);
                if (trackedAlert is null)
                {
                    return null;
                }

                terminalResult = await EvaluateTerminalStatusAsync(
                    trackedAlert,
                    cancellationToken);
                if (terminalResult is not null)
                {
                    return terminalResult;
                }

                EnsureAlertMutationEnabled(trackedAlert);
                trackedAlert.Status = MaintenanceAlertStatus.Resolved;
                trackedAlert.ResolvedAt = resolvedAt;
                trackedAlert.ResolvedBy = resolvedBy;
                trackedAlert.UpdatedAt = resolvedAt;
                alert = trackedAlert;
            }

            _dbContext.MaintenanceLogs.Add(log);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException)
        {
            // Finding H7: the filtered-unique index on ResolvedAlertId caught a concurrent duplicate
            // completion. Roll back, discard the losing tracked entities, and return the winner so
            // the racing caller still observes an idempotent success rather than an error.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            _dbContext.ChangeTracker.Clear();

            MaintenanceLog? winner = await FindLatestLinkedLogAsync(alertId, cancellationToken);
            if (winner is not null)
            {
                MaintenanceAlert? committedAlert = await _dbContext.MaintenanceAlerts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
                if (committedAlert is not null)
                {
                    return new MaintenanceAlertResolutionResult(
                        committedAlert,
                        winner,
                        Created: false);
                }
            }

            throw;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        alert.Status = MaintenanceAlertStatus.Resolved;
        alert.ResolvedAt = resolvedAt;
        alert.ResolvedBy = resolvedBy;
        alert.UpdatedAt = resolvedAt;

        await PublishResolvedAttentionAsync(alert);
        await PublishResolutionCreatedAsync(alert, log, cancellationToken);

        return new MaintenanceAlertResolutionResult(alert, log, Created: true);
    }

    /// <inheritdoc />
    public async Task<MaintenanceAlertResolutionResult?> ResolveAlertWithCompletionLogAsync(
        Guid alertId,
        string resolvedBy,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        MaintenanceAlert? alert = await _dbContext.MaintenanceAlerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert == null)
        {
            return null;
        }

        MaintenanceAlertResolutionResult? terminalResult =
            await EvaluateTerminalStatusAsync(alert, cancellationToken);
        if (terminalResult is not null)
        {
            return terminalResult;
        }

        // Baselines are mandatory: the alert engine derives "due" from the latest completion log's
        // hours (MaintenanceAlertEngine.ComputeHoursSinceLastMaintenance). Omitting them would make
        // the engine treat the alert as still due and recreate it on the next evaluation.
        double? printerHours = null;
        if (_printerStatisticsRepository != null)
        {
            PrinterStatistics? stats = await _printerStatisticsRepository
                .GetByPrinterIdAsync(alert.PrinterId, cancellationToken);
            printerHours = stats?.TotalPrintHours;
        }

        double? toolheadHours = null;
        if (alert.ToolheadId.HasValue && _toolheadStatisticsRepository != null)
        {
            toolheadHours = await _toolheadStatisticsRepository
                .GetCumulativeHoursAsync(alert.ToolheadId.Value, cancellationToken);
        }

        var log = new MaintenanceLog
        {
            Id = Guid.NewGuid(),
            PrinterId = alert.PrinterId,
            PrinterMaintenanceScheduleId = alert.PrinterMaintenanceScheduleId,
            MaintenanceTaskId = alert.MaintenanceTaskId,
            ToolheadId = alert.ToolheadId,
            TaskName = alert.Title ?? "Scheduled Maintenance",
            PerformedAt = DateTime.UtcNow,
            PerformedBy = resolvedBy,
            Notes = notes,
            PrinterHoursAtMaintenance = printerHours,
            ToolheadHoursAtMaintenance = toolheadHours,
        };

        return await ResolveWithLogAsync(alertId, log, resolvedBy, cancellationToken);
    }

    private async Task<MaintenanceAlertResolutionResult?> EvaluateTerminalStatusAsync(
        MaintenanceAlert alert,
        CancellationToken cancellationToken)
    {
        if (alert.Status == MaintenanceAlertStatus.Dismissed)
        {
            throw new MaintenanceAlertNotResolvableException(alert.Id, alert.Status);
        }

        if (alert.Status != MaintenanceAlertStatus.Resolved)
        {
            return null;
        }

        MaintenanceLog? existing = await FindLatestLinkedLogAsync(
            alert.Id,
            cancellationToken);
        return new MaintenanceAlertResolutionResult(
            alert,
            existing,
            Created: false);
    }

    private async Task<MaintenanceAlertResolutionResult?> ReloadAfterConcurrentTransitionAsync(
        Guid alertId,
        CancellationToken cancellationToken)
    {
        MaintenanceAlert? current = await _dbContext.MaintenanceAlerts
            .AsNoTracking()
            .FirstOrDefaultAsync(alert => alert.Id == alertId, cancellationToken);
        if (current is null)
        {
            return null;
        }

        MaintenanceAlertResolutionResult? terminalResult =
            await EvaluateTerminalStatusAsync(current, cancellationToken);
        if (terminalResult is not null)
        {
            return terminalResult;
        }

        throw new DbUpdateConcurrencyException(
            $"Maintenance alert {alertId} changed while it was being resolved.");
    }

    private Task<MaintenanceLog?> FindLatestLinkedLogAsync(Guid alertId, CancellationToken cancellationToken) =>
        _dbContext.MaintenanceLogs
            .AsNoTracking()
            .Where(l => l.ResolvedAlertId == alertId)
            .OrderByDescending(l => l.PerformedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private void EnsureAlertMutationEnabled(MaintenanceAlert alert)
    {
        bool perToolMaintenanceEnabled =
            _operatorFeatureGate?.IsEnabled(OperatorFeature.MultiSlotFallback) ?? true;
        if (alert.ToolheadId.HasValue && !perToolMaintenanceEnabled)
        {
            throw new PerToolMaintenanceDisabledException();
        }
    }

    private async Task PublishResolvedAttentionAsync(MaintenanceAlert alert)
    {
        if (_attentionBroadcaster is null)
        {
            return;
        }

        // Finding H7: the resolution is already durably committed. A broadcast failure is an
        // observability concern, not a correctness one, so swallow and log it rather than letting
        // it surface as an HTTP 500 that would encourage a duplicate retry.
        try
        {
            DateTime occurredAt = alert.ResolvedAt ?? DateTime.UtcNow;
            await _attentionBroadcaster.NotifyChangedAsync(new AttentionChangedPayload(
                AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alert.Id),
                AttentionChangeKind.Resolved,
                occurredAt));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "[MaintenanceAlertResolutionService] Resolved alert {AlertId} but attention broadcast failed; resolution is committed.",
                alert.Id);
        }
    }

    private async Task PublishResolutionCreatedAsync(
        MaintenanceAlert alert,
        MaintenanceLog log,
        CancellationToken cancellationToken)
    {
        if (_resolutionNotifier is null)
        {
            return;
        }

        try
        {
            await _resolutionNotifier.NotifyCreatedAsync(
                alert,
                log,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "[MaintenanceAlertResolutionService] Resolved alert {AlertId} but completion notification failed; resolution is committed.",
                alert.Id);
        }
    }
}
