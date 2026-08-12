using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Data;

/// <summary>
/// Starts serializable transactions without reserving the SQLite write lock for read-only work.
/// </summary>
internal static class ProviderSafeSerializableTransaction
{
    public static async Task<ProviderSafeSerializableTransactionScope> BeginAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Database.IsRelational())
        {
            throw new InvalidOperationException(
                "Provider-safe serializable transactions require a relational database.");
        }

        if (context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A transaction is already active on this database context.");
        }

        if (string.Equals(
                context.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
        {
            bool closeConnection =
                context.Database.GetDbConnection().State != ConnectionState.Open;
            if (closeConnection)
            {
                await context.Database.OpenConnectionAsync(cancellationToken);
            }

            SqliteTransaction? nativeTransaction = null;
            try
            {
                var connection = (SqliteConnection)context.Database.GetDbConnection();
#pragma warning disable CA1849 // Microsoft.Data.Sqlite has no asynchronous deferred-transaction overload.
                nativeTransaction = connection.BeginTransaction(
                    IsolationLevel.Serializable,
                    deferred: true);
#pragma warning restore CA1849
                IDbContextTransaction transaction = await context.Database
                    .UseTransactionAsync(nativeTransaction, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Unable to enlist the deferred SQLite transaction.");
                return new ProviderSafeSerializableTransactionScope(
                    context,
                    transaction,
                    nativeTransaction,
                    closeConnection);
            }
            catch
            {
                if (nativeTransaction is not null)
                {
                    await nativeTransaction.DisposeAsync();
                }

                if (closeConnection)
                {
                    await context.Database.CloseConnectionAsync();
                }

                throw;
            }
        }

        IDbContextTransaction relationalTransaction =
            await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        return new ProviderSafeSerializableTransactionScope(
            context,
            relationalTransaction,
            nativeTransaction: null,
            closeConnection: false);
    }
}

#pragma warning disable IDISP007 // This scope receives and owns transactions created by BeginAsync.
internal sealed class ProviderSafeSerializableTransactionScope(
    AppDbContext context,
    IDbContextTransaction transaction,
    SqliteTransaction? nativeTransaction,
    bool closeConnection) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken cancellationToken) =>
        transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken) =>
        transaction.RollbackAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        if (nativeTransaction is not null)
        {
            await nativeTransaction.DisposeAsync();
        }

        if (closeConnection)
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
#pragma warning restore IDISP007
