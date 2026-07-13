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
            bool expired = IsExpired(existing.CreatedAt, now);
            bool staleProcessing = !expired
                && existing.Status == IdempotencyRecordStatus.Processing
                && existing.CreatedAt < now - _options.ProcessingStaleness;

            if (expired || staleProcessing)
            {
                // Expired (past the retention window) OR a Processing row whose owning
                // request appears to have died before completing (older than
                // ProcessingStaleness): purge it before attempting a fresh insert so a
                // crashed request cannot block the key until it ages out. If a concurrent
                // caller beat us to the delete or the row is already gone, ExecuteDeleteAsync
                // simply returns 0 — no error.
                _ = await db.IdempotencyRecords
                    .Where(r => r.Id == existing.Id)
                    .ExecuteDeleteAsync(ct);
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
                "Idempotency-Key race resolved by unique index for route={RouteKey}; reloading winner.",
                routeKey);

            await using AppDbContext readDb = await _dbFactory.CreateDbContextAsync(ct);
            IdempotencyRecord? winner = await readDb.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.UserId == userId
                        && r.RouteKey == routeKey
                        && r.IdempotencyKey == idempotencyKey,
                    ct);
            if (winner is null || IsExpired(winner.CreatedAt, now))
            {
                // The winning row vanished or is itself expired. Rather than looping
                // (which risks livelock under sustained contention) we fall back to
                // Bypassed so the caller executes the mutation normally. A subsequent
                // retry with the same key will find a stable state.
                return new IdempotencyLookupResult(IdempotencyLookupOutcome.Bypassed, null);
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
