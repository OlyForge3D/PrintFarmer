using System.Data;
using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Database-backed cross-process monotonic sequence allocator.
///
/// Relational providers increment the counter with one provider-native atomic statement:
/// SQL Server uses <c>UPDATE ... OUTPUT</c>; PostgreSQL and SQLite use
/// <c>UPDATE ... RETURNING</c>. The same statement advances the row's revision. No producer
/// tracks or competes on the singleton row's EF concurrency token, so simultaneous terminal
/// transitions cannot fail merely because they need adjacent event sequence numbers.
/// </summary>
public sealed class DbOutboxSequenceAllocator : IDbOutboxSequenceAllocator
{
    /// <inheritdoc />
    public async Task<long> AllocateAsync(AppDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!db.Database.IsRelational())
        {
            OutboxSequenceState state = await db.OutboxSequenceStates.SingleAsync(ct);
            state.NextSequence++;
            return state.NextSequence;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Relational outbox sequence allocation must run inside the same transaction " +
                "that inserts the event.");
        }

        DbConnection connection = db.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = db.Database.ProviderName switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" =>
                    "UPDATE [OutboxSequenceStates] " +
                    "SET [NextSequence] = [NextSequence] + 1, [Revision] = [Revision] + 1 " +
                    "OUTPUT INSERTED.[NextSequence] WHERE [Id] = 1;",
                "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                    "UPDATE \"OutboxSequenceStates\" " +
                    "SET \"NextSequence\" = \"NextSequence\" + 1, \"Revision\" = \"Revision\" + 1 " +
                    "WHERE \"Id\" = 1 RETURNING \"NextSequence\";",
                "Microsoft.EntityFrameworkCore.Sqlite" =>
                    "UPDATE \"OutboxSequenceStates\" " +
                    "SET \"NextSequence\" = \"NextSequence\" + 1, \"Revision\" = \"Revision\" + 1 " +
                    "WHERE \"Id\" = 1 RETURNING \"NextSequence\";",
                _ => throw new NotSupportedException(
                    $"Outbox sequence allocation is not configured for provider '{db.Database.ProviderName}'."),
            };

            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();

            object? value = await command.ExecuteScalarAsync(ct);
            if (value is null || value is DBNull)
            {
                throw new InvalidOperationException(
                    "The outbox sequence seed row is missing; migrations must create Id=1.");
            }

            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
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
