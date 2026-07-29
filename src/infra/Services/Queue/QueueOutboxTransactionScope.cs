// <copyright file="QueueOutboxTransactionScope.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Owns an outbox write transaction only when the caller does not already have one.
/// </summary>
public sealed class QueueOutboxTransactionScope : IAsyncDisposable
{
    private readonly IDbContextTransaction? _ownedTransaction;

    private QueueOutboxTransactionScope(IDbContextTransaction? ownedTransaction)
    {
        _ownedTransaction = ownedTransaction;
    }

    /// <summary>Begins a relational transaction when required for an atomic outbox write.</summary>
    public static async Task<QueueOutboxTransactionScope> BeginAsync(
        AppDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IDbContextTransaction? transaction =
            db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
        return new QueueOutboxTransactionScope(transaction);
    }

    /// <summary>Commits the transaction owned by this scope.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_ownedTransaction is not null)
        {
            await _ownedTransaction.CommitAsync(ct);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownedTransaction is not null)
        {
            await _ownedTransaction.DisposeAsync();
        }
    }
}
