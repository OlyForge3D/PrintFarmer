using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Entity Framework implementation of <see cref="IMaintenanceAlertResolutionService"/>. Wraps the
/// gate re-check, log insertion, and alert mutation in a single transaction so a gate that flips
/// after the controller's pre-check cannot leave an orphaned completion log (issue #711, round-7
/// Finding 5).
/// </summary>
public sealed class MaintenanceAlertResolutionService(
    AppDbContext dbContext,
    IOperatorFeatureGate? operatorFeatureGate = null,
    IAttentionBroadcaster? attentionBroadcaster = null) : IMaintenanceAlertResolutionService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    // Optional to mirror MaintenanceAlertEngine: when null the gate is treated as enabled
    // (legacy behavior) so existing constructors and non-gated deployments are unaffected.
    private readonly IOperatorFeatureGate? _operatorFeatureGate = operatorFeatureGate;

    // Optional attention-feed invalidation (issue #707); preserves the retire-on-resolve
    // notification that MaintenanceAlertEngine.ResolveAlertAsync emitted before this service
    // owned the resolve path.
    private readonly IAttentionBroadcaster? _attentionBroadcaster = attentionBroadcaster;

    /// <inheritdoc />
    public async Task<MaintenanceAlertResolutionResult?> ResolveWithLogAsync(
        Guid alertId,
        MaintenanceLog log,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        MaintenanceAlert? alert = await _dbContext.MaintenanceAlerts
            .FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken);
        if (alert == null)
        {
            return null;
        }

        // InMemory has no transaction support; a single SaveChanges is already atomic there. For
        // relational providers open an explicit transaction so the staged log rolls back with the
        // alert mutation if the gate re-check throws.
        bool useTransaction = _dbContext.Database.IsRelational();
        IDbContextTransaction? transaction = useTransaction
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // Stage the log WITHOUT saving so a gate rejection discards it atomically.
            _dbContext.MaintenanceLogs.Add(log);

            // Re-check the gate immediately before mutating — inside the transaction — so a gate
            // that flipped after the controller pre-check cannot persist the log with an
            // unresolved alert.
            EnsureAlertMutationEnabled(alert);

            alert.Status = MaintenanceAlertStatus.Resolved;
            alert.ResolvedAt = DateTime.UtcNow;
            alert.ResolvedBy = resolvedBy;

            // Single SaveChanges commits the log insert and the alert transition together.
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }

        await PublishResolvedAttentionAsync(alert);

        return new MaintenanceAlertResolutionResult(alert, log);
    }

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

        DateTime occurredAt = alert.ResolvedAt ?? DateTime.UtcNow;
        await _attentionBroadcaster.NotifyChangedAsync(new AttentionChangedPayload(
            AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alert.Id),
            AttentionChangeKind.Resolved,
            occurredAt));
    }
}
