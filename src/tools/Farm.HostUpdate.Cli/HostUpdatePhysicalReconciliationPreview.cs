using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.HostUpdate.Cli;

internal sealed record HostUpdatePhysicalReconciliationPreview(
    string State,
    string? Detail,
    int? PrinterCount,
    int? UncertainOutcomeCount,
    HostUpdatePrinterReconciliationItem[]? Printers,
    string? ReconciliationToken,
    DateTimeOffset? RecordedAt,
    string ReplayPolicy);

/// <summary>
/// Reads the printer command inventory from the configured application database without
/// tracking, saving, or reaching any printer backend. SQLite is opened read-only.
/// </summary>
internal sealed class HostUpdateCliPrinterCommandInventoryReader(DatabaseProviderConfiguration database) : IHostUpdatePrinterCommandInventoryReader
{
    public async Task<HostUpdatePrinterCommandInventory> ReadAsync(CancellationToken cancellationToken)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        if (database.IsSqlServer)
        {
            _ = builder.UseSqlServer(database.ConnectionString);
        }
        else if (database.IsPostgres)
        {
            _ = builder.UseNpgsql(database.ConnectionString);
        }
        else
        {
            var connection = new SqliteConnectionStringBuilder(database.ConnectionString) { Mode = SqliteOpenMode.ReadOnly };
            _ = builder.UseSqlite(connection.ToString());
        }

        await using var db = new AppDbContext(builder.Options);
        return await new DbHostUpdatePrinterCommandInventoryReader(db).ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Builds the <c>physicalReconciliation</c> section of <c>recover --preview</c> (issue #2999).</summary>
internal static class HostUpdatePhysicalReconciliationPreviewBuilder
{
    public const string Complete = "complete";
    public const string Recorded = "recorded";
    public const string ReadyToRecord = "ready_to_record";
    public const string AfterRollback = "after_rollback";
    public const string OperatorRequired = "operator_required";
    public const string InventoryUnavailable = "inventory_unavailable";
    public const string RecordUnreadable = "record_unreadable";

    public static async Task<HostUpdatePhysicalReconciliationPreview> BuildAsync(
        HostUpdateRecoveryPlan plan,
        HostUpdateExecutionRequest request,
        IHostUpdatePhysicalReconciliationStore store,
        IHostUpdatePrinterCommandInventoryReader inventoryReader,
        CancellationToken cancellationToken)
    {
        if (plan.Kind == HostUpdateRecoveryPlanKind.AlreadyRolledBack)
        {
            return new(Complete, null, null, null, null, null, null, HostUpdatePhysicalReconciliationCodes.ReplayPolicy);
        }

        HostUpdatePhysicalReconciliationRecord? record;
        try
        {
            record = await store.ReadAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(RecordUnreadable, exception.GetType().Name, null, null, null, null, null, HostUpdatePhysicalReconciliationCodes.ReplayPolicy);
        }

        if (record is not null && string.Equals(record.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            return new(
                Recorded,
                null,
                record.Printers.Count,
                record.Printers.Sum(printer => printer.UncertainOutcomes.Count),
                [.. record.Printers],
                null,
                record.RecordedAt,
                HostUpdatePhysicalReconciliationCodes.ReplayPolicy);
        }

        string state = plan.Kind switch
        {
            HostUpdateRecoveryPlanKind.FenceReleaseOnly => ReadyToRecord,
            HostUpdateRecoveryPlanKind.NeedsOperator => OperatorRequired,
            _ => AfterRollback,
        };

        HostUpdatePrinterCommandInventory inventory;
        try
        {
            inventory = await inventoryReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type is reported: provider messages can carry connection details.
            return new(
                state == ReadyToRecord ? InventoryUnavailable : state,
                "physical_inventory_unavailable:" + exception.GetType().Name,
                null,
                null,
                null,
                null,
                null,
                HostUpdatePhysicalReconciliationCodes.ReplayPolicy);
        }

        // A token is offered only once the rollback is durable; before that the inventory is
        // evidence only, and it is re-read (and re-bound) before anything is recorded.
        return new(
            state,
            null,
            inventory.Printers.Count,
            inventory.UncertainOutcomeCount,
            [.. inventory.Printers],
            state == ReadyToRecord ? inventory.Token(request.ReleaseId, request.RequestId) : null,
            null,
            HostUpdatePhysicalReconciliationCodes.ReplayPolicy);
    }
}
