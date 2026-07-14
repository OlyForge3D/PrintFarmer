using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// EF Core implementation of <see cref="IIdempotencyStore"/>. Uses
/// <see cref="IDbContextFactory{TContext}"/> so it can be resolved outside a
/// request scope (hosted cleanup service, background workers).
///
/// <para>
/// Concurrency model: the composite unique index on
/// <c>(UserId, RouteKey, IdempotencyKey)</c> serializes racing first-requests
/// at the database. The "insert-then-catch-unique-violation-then-reload" pattern
/// is used because it lets the store handle two racing callers atomically without
/// requiring provider-specific upsert syntax.
/// </para>
///
/// <para>
/// Expired records: an existing row with <c>CreatedAt &lt; now - RetentionWindow</c>
/// is deleted before a fresh insert is attempted. Read-side interpretation always
/// ignores expired rows so a stale row never masquerades as a replay even before
/// the cleanup service prunes it.
/// </para>
/// </summary>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<IdempotencyStore> _logger;
    private readonly IdempotencyOptions _options;

    /// <summary>
    /// Constructs the store with the default tuning options
    /// (<see cref="IdempotencyOptions.Default"/>).
    /// </summary>
    public IdempotencyStore(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IdempotencyStore> logger)
        : this(dbFactory, logger, IdempotencyOptions.Default)
    {
    }

    /// <summary>Constructs the store with explicit tuning options.</summary>
    public IdempotencyStore(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IdempotencyStore> logger,
        IdempotencyOptions options)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Maximum number of insert attempts before a caller that keeps losing the race
    /// to a winner that then vanishes (or is stale) is told to back off with
    /// <see cref="IdempotencyLookupOutcome.InProgress"/>. Bounds the loop so sustained
    /// contention can never livelock, while still giving a genuine
    /// abandon-in-between race a chance to re-insert and win protection.
    /// </summary>
    private const int MaxBeginAttempts = 3;

    /// <inheritdoc />
    public async Task<IdempotencyLookupResult> TryBeginAsync(
        string userId,
        string routeKey,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);

        DateTime now = DateTime.UtcNow;

        // Bounded retry loop. The winner-reload path (after a unique-violation) can
        // legitimately observe the winning row vanish — a concurrent caller abandoned
        // its Processing row between our failed insert and our reload — or find it
        // stale. Returning Bypassed there would let a retry execute the mutation with
        // no replay protection (Hicks H-2), so instead we retry the insert. If every
        // attempt races into the same vanish/stale pattern we surface InProgress (409)
        // so the client backs off rather than executing unprotected.
        for (int attempt = 1; attempt <= MaxBeginAttempts; attempt++)
        {
            await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);

            IdempotencyRecord? existing = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.UserId == userId
                        && r.RouteKey == routeKey
                        && r.IdempotencyKey == idempotencyKey,
                    ct);

            if (existing is not null)
            {
                if (IsReclaimable(existing, now))
                {
                    // Expired (past the retention window) OR a Processing row whose owning
                    // request appears to have died before completing (older than
                    // ProcessingStaleness): purge it before attempting a fresh insert so a
                    // crashed request cannot block the key until it ages out. The delete is
                    // CONDITIONAL on the reclaim predicate (Hicks r2 blocker 3): if a
                    // concurrent CompleteAsync committed between our AsNoTracking read and
                    // this delete, zero rows match and we must NOT fall through to a fresh
                    // insert — that would erase the just-completed record and re-execute the
                    // mutation. Re-loop instead to re-interpret the row as a replay hit.
                    if (!await TryReclaimStaleRecordAsync(db, existing.Id, now, ct))
                    {
                        continue;
                    }

                    // Reclaimed — fall through to the insert below.
                }
                else if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new IdempotencyLookupResult(IdempotencyLookupOutcome.HashConflict, existing);
                }
                else if (existing.Status == IdempotencyRecordStatus.Completed)
                {
                    return new IdempotencyLookupResult(IdempotencyLookupOutcome.ReplayCompleted, existing);
                }
                else
                {
                    return new IdempotencyLookupResult(IdempotencyLookupOutcome.InProgress, existing);
                }
            }

            IdempotencyRecord record = new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RouteKey = routeKey,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                Status = IdempotencyRecordStatus.Processing,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ = db.IdempotencyRecords.Add(record);

            try
            {
                _ = await db.SaveChangesAsync(ct);
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.Inserted, record);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A concurrent first-request won the race. Reload the winning row and
                // interpret it exactly as we would in the initial-read path.
                _logger.LogDebug(
                    ex,
                    "Idempotency-Key race resolved by unique index for route={RouteKey}; reloading winner (attempt {Attempt}).",
                    routeKey,
                    attempt);

                await using AppDbContext readDb = await _dbFactory.CreateDbContextAsync(ct);
                IdempotencyRecord? winner = await readDb.IdempotencyRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        r => r.UserId == userId
                            && r.RouteKey == routeKey
                            && r.IdempotencyKey == idempotencyKey,
                        ct);

                if (winner is null)
                {
                    // The winning row vanished (a concurrent caller abandoned its
                    // Processing row between our insert and this reload). Retry the
                    // insert rather than Bypassing, so the mutation is never executed
                    // unprotected (Hicks H-2). If every bounded attempt races into the
                    // same vanish, the post-loop return surfaces InProgress (409).
                    continue;
                }

                if (IsReclaimable(winner, now))
                {
                    // The reloaded winner is itself expired or a stale Processing row.
                    // Apply the same conditional reclaim the initial-read path uses (Bishop
                    // NB3 + Hicks r2 blocker 3). Whether the conditional delete wins (stale
                    // row purged) or loses to a concurrent completion (zero rows matched),
                    // re-looping re-interprets the row correctly: a freshly-completed winner
                    // becomes a ReplayCompleted hit on the next pass instead of being erased,
                    // and a genuinely purged row becomes a fresh insert.
                    _ = await TryReclaimStaleRecordAsync(readDb, winner.Id, now, ct);

                    continue;
                }

                if (!string.Equals(winner.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new IdempotencyLookupResult(IdempotencyLookupOutcome.HashConflict, winner);
                }

                return winner.Status == IdempotencyRecordStatus.Completed
                    ? new IdempotencyLookupResult(IdempotencyLookupOutcome.ReplayCompleted, winner)
                    : new IdempotencyLookupResult(IdempotencyLookupOutcome.InProgress, winner);
            }
        }

        // Every bounded attempt raced into the same vanish/stale pattern: surface
        // InProgress (409) so the client backs off rather than executing unprotected.
        return new IdempotencyLookupResult(IdempotencyLookupOutcome.InProgress, null);
    }

    /// <summary>
    /// True when an existing record should be purged and replaced by a fresh insert:
    /// it is either past the retention window (<see cref="IsExpired"/>) or a
    /// <see cref="IdempotencyRecordStatus.Processing"/> row whose owning request
    /// appears to have died (older than <see cref="IdempotencyOptions.ProcessingStaleness"/>).
    /// Shared by the initial-read and winner-reload branches of
    /// <see cref="TryBeginAsync"/> so both agree on exactly what is reclaimable.
    /// </summary>
    private bool IsReclaimable(IdempotencyRecord record, DateTime now)
    {
        if (IsExpired(record.CreatedAt, now))
        {
            return true;
        }

        return record.Status == IdempotencyRecordStatus.Processing
            && record.CreatedAt < now - _options.ProcessingStaleness;
    }

    /// <summary>
    /// Conditionally deletes a record, but only while it still satisfies the reclaim
    /// predicate (<see cref="IsReclaimable"/>) — evaluated atomically inside the DELETE's
    /// WHERE clause against the same <paramref name="now"/> the caller used for its
    /// read-side decision, so completion cannot slip between the check and the delete.
    /// Returns <c>true</c> when a row was deleted (reclaim succeeded), <c>false</c> when
    /// zero rows matched — meaning the record stopped being reclaimable between the caller's
    /// snapshot read and this delete (a concurrent <see cref="CompleteAsync"/> committed, or
    /// a competing reclaim won the race).
    ///
    /// <para>
    /// This closes the TOCTOU window (Hicks r2 blocker 3): an unconditional delete-by-id
    /// would erase a freshly-completed record, causing the next replay attempt to miss it
    /// and re-execute the already-applied mutation. Both reclaim sites in
    /// <see cref="TryBeginAsync"/> route through here so they share identical semantics.
    /// </para>
    /// </summary>
    private async Task<bool> TryReclaimStaleRecordAsync(AppDbContext db, Guid recordId, DateTime now, CancellationToken ct)
    {
        DateTime retentionCutoff = now - IIdempotencyStore.RetentionWindow;
        DateTime stalenessCutoff = now - _options.ProcessingStaleness;

        // Predicate mirrors IsReclaimable exactly: expired (past retention) OR a
        // still-Processing row older than the staleness horizon. Because it runs in the
        // database as part of the DELETE, a row that was completed after the caller's read
        // no longer matches and survives.
        int deletedRows = await db.IdempotencyRecords
            .Where(r => r.Id == recordId
                && (r.CreatedAt < retentionCutoff
                    || (r.Status == IdempotencyRecordStatus.Processing && r.CreatedAt < stalenessCutoff)))
            .ExecuteDeleteAsync(ct);

        return deletedRows > 0;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        Guid recordId,
        int statusCode,
        string? contentType,
        byte[] responseBody,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        IdempotencyRecord? record = await db.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            // Record was pruned or abandoned between begin and complete — leave it
            // gone; the client will retry and go through the normal insert path.
            return;
        }

        if (record.Status == IdempotencyRecordStatus.Completed)
        {
            // Already completed by a racing observer; leave as-is to preserve
            // the first-writer response bytes.
            return;
        }

        record.Status = IdempotencyRecordStatus.Completed;
        record.ResponseStatusCode = statusCode;
        record.ResponseContentType = contentType;
        record.ResponseBody = responseBody;
        record.UpdatedAt = DateTime.UtcNow;
        _ = await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task AbandonProcessingAsync(Guid recordId, CancellationToken ct)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        _ = await db.IdempotencyRecords
            .Where(r => r.Id == recordId && r.Status == IdempotencyRecordStatus.Processing)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now - IIdempotencyStore.RetentionWindow;
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);

        // Bulk delete-by-predicate: concurrency-safe because it does not enumerate
        // and each row is deleted in a single statement using the same predicate.
        int removed = await db.IdempotencyRecords
            .Where(r => r.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} expired idempotency records older than {Cutoff:O}.", removed, cutoff);
        }

        return removed;
    }

    /// <summary>
    /// Threshold check for retention expiry. A record is expired iff its
    /// <see cref="IdempotencyRecord.CreatedAt"/> is strictly earlier than
    /// <paramref name="now"/> minus <see cref="IIdempotencyStore.RetentionWindow"/>.
    /// The boundary is <b>exclusive</b>: a record whose age is exactly the
    /// retention window is still considered valid. This single predicate is shared
    /// by <see cref="TryBeginAsync"/> (initial read and winner-reload) and mirrors
    /// the <c>CreatedAt &lt; cutoff</c> filter used by <see cref="PruneExpiredAsync"/>,
    /// so read, begin, and prune all agree on the exact-tick boundary.
    /// </summary>
    internal static bool IsExpired(DateTime createdAt, DateTime now)
        => createdAt < now - IIdempotencyStore.RetentionWindow;

    // --- Provider-specific unique-constraint error codes -----------------------
    // SQLite primary result code SQLITE_CONSTRAINT and its PK/unique extended codes.
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    // PostgreSQL SQLSTATE for unique_violation (surfaced via DbException.SqlState).
    private const string PostgresUniqueViolation = "23505";

    // SQL Server engine error numbers: 2601 duplicate key row in a unique index,
    // 2627 unique/primary-key constraint violation.
    private const int SqlServerDuplicateKeyRow = 2601;
    private const int SqlServerUniqueConstraint = 2627;

    /// <summary>
    /// True when <paramref name="ex"/> wraps a provider unique-constraint violation.
    /// Detection is <b>typed and code-based</b> — never message-string matching — so
    /// it is robust across locales and provider message wording changes:
    /// <list type="bullet">
    /// <item><description>SQLite: <see cref="Microsoft.Data.Sqlite.SqliteException"/> with
    /// primary code 19 (<c>SQLITE_CONSTRAINT</c>) or extended code 1555/2067. The
    /// <c>IdempotencyRecords</c> table carries only the composite unique index (no FKs
    /// or CHECK constraints), so a constraint failure here is unambiguously the unique
    /// violation.</description></item>
    /// <item><description>PostgreSQL: <see cref="System.Data.Common.DbException.SqlState"/>
    /// == <c>23505</c>. Npgsql surfaces the SQLSTATE on the base <c>DbException</c>, so no
    /// direct Npgsql dependency is required.</description></item>
    /// <item><description>SQL Server: <c>Microsoft.Data.SqlClient.SqlException.Number</c>
    /// in {2601, 2627}, read reflectively to avoid a hard SqlClient dependency in the
    /// infrastructure assembly.</description></item>
    /// </list>
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (IsUniqueViolationInner(inner))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUniqueViolationInner(Exception inner)
    {
        int? sqliteErrorCode = null;
        int? sqliteExtendedErrorCode = null;
        if (inner is Microsoft.Data.Sqlite.SqliteException sqlite)
        {
            sqliteErrorCode = sqlite.SqliteErrorCode;
            sqliteExtendedErrorCode = sqlite.SqliteExtendedErrorCode;
        }

        // PostgreSQL (and any ADO provider honouring the SQLSTATE contract) exposes
        // the SQLSTATE on the base DbException — no Npgsql type reference required.
        string? sqlState = (inner as System.Data.Common.DbException)?.SqlState;

        int? sqlServerNumber = null;
        string? typeName = inner.GetType().FullName;
        if (typeName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException")
        {
            sqlServerNumber = inner.GetType().GetProperty("Number")?.GetValue(inner) as int?;
        }

        return MatchesUniqueViolation(sqlState, sqlServerNumber, sqliteErrorCode, sqliteExtendedErrorCode);
    }

    /// <summary>
    /// Pure classifier over the coded signals extracted from a provider exception.
    /// Exposed to tests so each provider's unique-violation signature can be
    /// asserted without constructing a live provider exception.
    /// </summary>
    internal static bool MatchesUniqueViolation(
        string? sqlState,
        int? sqlServerErrorNumber,
        int? sqliteErrorCode,
        int? sqliteExtendedErrorCode)
    {
        if (sqliteErrorCode == SqliteConstraint
            || sqliteExtendedErrorCode is SqliteConstraintPrimaryKey or SqliteConstraintUnique)
        {
            return true;
        }

        if (string.Equals(sqlState, PostgresUniqueViolation, StringComparison.Ordinal))
        {
            return true;
        }

        return sqlServerErrorNumber is SqlServerDuplicateKeyRow or SqlServerUniqueConstraint;
    }
}
