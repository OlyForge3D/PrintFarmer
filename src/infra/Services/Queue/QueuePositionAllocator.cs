// <copyright file="QueuePositionAllocator.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Allocates unique monotonic queue positions per assigned printer.</summary>
public interface IQueuePositionAllocator
{
    Task<int> AllocateAsync(Guid? printerId, CancellationToken ct = default);
}

/// <summary>
/// Uses a provider-native atomic upsert so concurrent producers never receive the same position.
/// </summary>
public sealed class QueuePositionAllocator(AppDbContext db) : IQueuePositionAllocator
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<int> AllocateAsync(Guid? printerId, CancellationToken ct = default)
    {
        Guid scopeId = printerId ?? Guid.Empty;
        if (!_db.Database.IsRelational())
        {
            QueuePositionState? state = await _db.QueuePositionStates.FindAsync([scopeId], ct);
            if (state is null)
            {
                int nextPosition = (await _db.PrintJobs
                    .Where(job => job.AssignedPrinterId == printerId)
                    .MaxAsync(job => (int?)job.QueuePosition, ct) ?? 0) + 1;
                state = new QueuePositionState
                {
                    ScopeId = scopeId,
                    NextPosition = nextPosition,
                };
                _db.QueuePositionStates.Add(state);
            }
            else
            {
                state.NextPosition++;
            }

            return state.NextPosition;
        }

        DbConnection connection = _db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = _db.Database.ProviderName switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" =>
                    "MERGE [QueuePositionStates] WITH (HOLDLOCK) AS target " +
                    "USING (SELECT @scopeId AS [ScopeId], COALESCE((" +
                    "SELECT MAX([QueuePosition]) + 1 FROM [PrintJobs] " +
                    "WHERE [AssignedPrinterId] = @scopeId OR " +
                    "([AssignedPrinterId] IS NULL AND " +
                    "@scopeId = '00000000-0000-0000-0000-000000000000')), 1) " +
                    "AS [InitialPosition]) AS source " +
                    "ON target.[ScopeId] = source.[ScopeId] " +
                    "WHEN MATCHED THEN UPDATE SET [NextPosition] = target.[NextPosition] + 1 " +
                    "WHEN NOT MATCHED THEN INSERT ([ScopeId], [NextPosition]) " +
                    "VALUES (source.[ScopeId], source.[InitialPosition]) " +
                    "OUTPUT INSERTED.[NextPosition];",
                "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                    "INSERT INTO \"QueuePositionStates\" (\"ScopeId\", \"NextPosition\") " +
                    "SELECT @scopeId, COALESCE(MAX(\"QueuePosition\"), 0) + 1 FROM \"PrintJobs\" " +
                    "WHERE \"AssignedPrinterId\" = @scopeId OR " +
                    "(\"AssignedPrinterId\" IS NULL AND " +
                    "@scopeId = '00000000-0000-0000-0000-000000000000') " +
                    "ON CONFLICT (\"ScopeId\") DO UPDATE SET \"NextPosition\" = " +
                    "\"QueuePositionStates\".\"NextPosition\" + 1 RETURNING \"NextPosition\";",
                "Microsoft.EntityFrameworkCore.Sqlite" =>
                    "INSERT INTO \"QueuePositionStates\" (\"ScopeId\", \"NextPosition\") " +
                    "SELECT @scopeId, COALESCE(MAX(\"QueuePosition\"), 0) + 1 FROM \"PrintJobs\" " +
                    "WHERE \"AssignedPrinterId\" = @scopeId OR " +
                    "(\"AssignedPrinterId\" IS NULL AND " +
                    "@scopeId = '00000000-0000-0000-0000-000000000000') " +
                    "ON CONFLICT (\"ScopeId\") DO UPDATE SET \"NextPosition\" = \"NextPosition\" + 1 " +
                    "RETURNING \"NextPosition\";",
                _ => throw new NotSupportedException(
                    $"Queue position allocation is not configured for provider '{_db.Database.ProviderName}'."),
            };

            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@scopeId";
            parameter.Value = scopeId;
            _ = command.Parameters.Add(parameter);

            if (_db.Database.CurrentTransaction is { } transaction)
            {
                command.Transaction = transaction.GetDbTransaction();
            }

            object? value = await command.ExecuteScalarAsync(ct);
            return value is null || value is DBNull
                ? throw new InvalidOperationException("Queue position allocation returned no value.")
                : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
