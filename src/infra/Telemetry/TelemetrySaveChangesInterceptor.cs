using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// EF Core interceptor that automatically records database write operations
/// via <see cref="IPrintFarmerTelemetryService.RecordDatabaseOperation"/>.
/// Counts Added/Modified/Deleted entities per table after each SaveChanges call.
/// </summary>
public sealed class TelemetrySaveChangesInterceptor(IPrintFarmerTelemetryService telemetry) : SaveChangesInterceptor
{
    private readonly IPrintFarmerTelemetryService _telemetry = telemetry;
    private readonly ConcurrentDictionary<Guid, List<(string Table, string Operation, int Count)>> _pendingWrites = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CapturePendingChanges(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CapturePendingChanges(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        EmitCapturedChanges(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        EmitCapturedChanges(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ClearCapturedChanges(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ClearCapturedChanges(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void CapturePendingChanges(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var writes = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .GroupBy(e => new
            {
                Table = e.Metadata.GetTableName() ?? e.Metadata.ClrType.Name,
                Operation = e.State switch
                {
                    EntityState.Added => "insert",
                    EntityState.Modified => "update",
                    EntityState.Deleted => "delete",
                    _ => "write",
                },
            })
            .Select(g => (g.Key.Table, g.Key.Operation, g.Count()))
            .ToList();

        _pendingWrites[context.ContextId.InstanceId] = writes;
    }

    private void EmitCapturedChanges(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (!_pendingWrites.TryRemove(context.ContextId.InstanceId, out var writes))
        {
            return;
        }

        foreach (var (table, operation, count) in writes)
        {
            _telemetry.RecordDatabaseOperation(table, operation, count);
        }
    }

    private void ClearCapturedChanges(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        _ = _pendingWrites.TryRemove(context.ContextId.InstanceId, out _);
    }
}
