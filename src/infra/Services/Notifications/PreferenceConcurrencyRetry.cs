using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Bounded transient-retry helper for preference writes that lose a race against
/// a concurrent legacy or attention writer. Only known relational transient
/// failure signals trigger a retry — provider serialization/deadlock errors, EF
/// concurrency exceptions, and the unique-index violation raised on the
/// <c>NotificationPreferences.UserId</c> unique index when two concurrent
/// first-creations both try to insert the same UserId row (Bishop minor #1,
/// Hicks #2). Cancellation propagates unconditionally.
///
/// The helper takes a delegate that opens a FRESH <see cref="AppDbContext"/> via
/// <see cref="IDbContextFactory{TContext}"/> per attempt (Hicks #2). This
/// guarantees that a stale change-tracker snapshot from the losing attempt
/// never leaks into the retried transaction. Both wrapped (<see cref="DbUpdateException"/>-
/// nested) and RAW provider exceptions surfaced from a query / BeginTransaction /
/// Commit call are classified through the full <see cref="Exception.InnerException"/>
/// chain: EF only wraps provider exceptions raised by <see cref="DbContext.SaveChanges()"/>,
/// so a serialization failure raised while opening the serializable transaction
/// would otherwise escape unwrapped.
/// </summary>
public static class PreferenceConcurrencyRetry
{
    /// <summary>
    /// Diagnostic classification of a caught exception. Public so the tests can
    /// assert the classifier directly without staging a database.
    /// </summary>
    internal enum ClassifierDecision
    {
        /// <summary>Not a recognised transient — do not retry, rethrow verbatim.</summary>
        Rethrow,

        /// <summary>Provider serialization / deadlock / lock timeout — safe to retry.</summary>
        TransientProviderConflict,

        /// <summary>
        /// Unique-index violation on the <c>NotificationPreferences.UserId</c>
        /// index — retry so the losing writer re-reads and merges into the
        /// existing row (first-create convergence, Bishop minor #1).
        /// </summary>
        UserIdUniqueConflict,
    }

    /// <summary>Maximum number of whole-operation retry attempts before surfacing the last failure.</summary>
    /// <remarks>
    /// A modest fixed bound: the finalized #708 contract has ≤2 concurrent legacy vs
    /// modern writers per user in the wild, so a 4-attempt ceiling comfortably absorbs a
    /// realistic burst without hiding a real fault. Larger bounds mask problems by
    /// spinning under load; smaller bounds surface 400 for benign concurrency.
    /// </remarks>
    public const int MaxAttempts = 4;

    /// <summary>
    /// Executes <paramref name="operation"/> against a fresh DbContext per attempt.
    /// Retries only recognised transient failures; validation errors and cancellation
    /// propagate on the first surface. Returns whatever the delegate produces.
    /// </summary>
    /// <typeparam name="T">Return type of the wrapped operation.</typeparam>
    /// <param name="factory">
    /// DbContext factory used to open a fresh context per attempt. May be null on
    /// non-production paths (e.g., unit tests that pass their own context via the
    /// service constructor); in that case the caller-provided fallback runs once
    /// without retry.
    /// </param>
    /// <param name="fallbackContext">
    /// Optional context to use when <paramref name="factory"/> is null. Only invoked once
    /// (no retry) because a single tracked context has no way to shed stale state between
    /// attempts.
    /// </param>
    /// <param name="operation">
    /// The unit of work to execute. Receives the context to operate against and the
    /// caller's cancellation token. MUST NOT commit or dispose the context — the caller
    /// owns lifetime.
    /// </param>
    /// <param name="logger">Structured logger used to warn on retries.</param>
    /// <param name="cancellationToken">Caller cancellation token; propagates immediately.</param>
    /// <exception cref="OperationCanceledException">
    /// Rethrown unconditionally when the caller cancels or an inner OCE (linked/timeout)
    /// surfaces (Hicks #1 parity: no swallowed cancellation).
    /// </exception>
    public static async Task<T> ExecuteAsync<T>(
        IDbContextFactory<AppDbContext>? factory,
        AppDbContext? fallbackContext,
        Func<AppDbContext, CancellationToken, Task<T>> operation,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(logger);

        // No factory means we're on a test path where the caller wired a bespoke
        // in-memory DbContext directly. The single-attempt fallback preserves the
        // existing test surface — no retry — because InMemory has no concurrent
        // provider-level failures to guard against anyway.
        if (factory is null)
        {
            if (fallbackContext is null)
            {
                throw new InvalidOperationException(
                    "PreferenceConcurrencyRetry requires either a DbContext factory or a fallback context.");
            }

            return await operation(fallbackContext, cancellationToken).ConfigureAwait(false);
        }

        Exception? lastTransient = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AppDbContext freshContext = await factory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (freshContext.ConfigureAwait(false))
            {
                try
                {
                    return await operation(freshContext, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Hicks #1/#2 parity: OperationCanceledException MUST propagate
                    // BEFORE any generic-provider/EF catches. A cancelled call
                    // consumes no retry budget and never counts as a transient.
                    throw;
                }
                catch (Exception ex) when (Classify(ex) != ClassifierDecision.Rethrow)
                {
                    ClassifierDecision decision = Classify(ex);
                    string reason = decision == ClassifierDecision.UserIdUniqueConflict
                        ? "userid-unique"
                        : "provider-conflict";
                    lastTransient = ex;
                    LogRetry(logger, attempt, reason, ex);
                    if (attempt == MaxAttempts)
                    {
                        break;
                    }

                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
        }

        // Every recognised transient attempt failed — surface the last one.
        throw lastTransient!;
    }

    private static void LogRetry(ILogger logger, int attempt, string reason, Exception ex)
    {
        // Keep the log line free of PII (no user id, no payload) — this is a
        // cold-path retry warning and downstream telemetry already captures
        // the operation identity. Level Warning so a burst of retries fires
        // an alert without spamming Information sinks.
        logger.LogWarning(
            ex,
            "[Notifications/Preferences] Transient {Reason} on attempt {Attempt}/{Max}; retrying with a fresh DbContext.",
            reason,
            attempt,
            MaxAttempts);
    }

    private static async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        // Fixed short linear backoff. Under the finalized #708 contract the
        // race window is a single serializable transaction so real conflicts
        // resolve within tens of milliseconds. Exponential backoff would
        // exceed the request budget for a benign concurrent write.
        //
        // Task.Delay honours cancellation on its own — no wrapper catch is
        // needed. Hicks #1 requires OCE to bubble out; letting Task.Delay
        // propagate directly is the least-noisy way to satisfy that.
        TimeSpan delay = TimeSpan.FromMilliseconds(15 * attempt);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Classifies a caught exception into a retry decision. Traverses the entire
    /// <see cref="Exception.InnerException"/> chain because a raw provider
    /// exception can appear either at the outermost level (BeginTransaction /
    /// query / Commit) or wrapped inside <see cref="DbUpdateException"/> from a
    /// <see cref="DbContext.SaveChanges()"/> call. <see cref="DbUpdateConcurrencyException"/>
    /// is always transient (an EF optimistic concurrency conflict).
    ///
    /// Uniqueness is treated as transient ONLY when the unique-index name that
    /// the provider surfaces on the exception references
    /// <c>NotificationPreferences.UserId</c> — arbitrary FK/CHECK/NOT-NULL/other
    /// unique failures fall through to <see cref="ClassifierDecision.Rethrow"/>
    /// so a genuine schema fault is surfaced, not masked by a retry loop.
    /// </summary>
    internal static ClassifierDecision Classify(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is DbUpdateConcurrencyException)
            {
                return ClassifierDecision.TransientProviderConflict;
            }

            string typeName = current.GetType().Name;
            string message = current.Message ?? string.Empty;

            switch (typeName)
            {
                case "SqliteException":
                    {
                        ClassifierDecision? decision = ClassifySqlite(current, message);
                        if (decision.HasValue)
                        {
                            return decision.Value;
                        }

                        break;
                    }

                case "PostgresException":
                    {
                        ClassifierDecision? decision = ClassifyNpgsql(current);
                        if (decision.HasValue)
                        {
                            return decision.Value;
                        }

                        break;
                    }

                case "SqlException":
                    {
                        ClassifierDecision? decision = ClassifySqlServer(current, message);
                        if (decision.HasValue)
                        {
                            return decision.Value;
                        }

                        break;
                    }

                case "MySqlException":
                    {
                        ClassifierDecision? decision = ClassifyMySql(current, message);
                        if (decision.HasValue)
                        {
                            return decision.Value;
                        }

                        break;
                    }
            }

            current = current.InnerException;
        }

        return ClassifierDecision.Rethrow;
    }

    /// <summary>
    /// The unique index/constraint the classifier accepts as a first-create
    /// UserId conflict. Every relational provider we ship migrations for
    /// names this artefact identically (EF's default index-name convention
    /// is <c>IX_{Table}_{Column}</c>; the SQL Server column-uniqueness
    /// constraint uses <c>UQ_</c> prefixes but still carries the column
    /// name).
    /// </summary>
    private const string UserIdIndexNeedle = "NotificationPreferences_UserId";

    /// <summary>
    /// SQLite BUSY (5) / LOCKED (6) → transient. Extended constraint code
    /// 2067 (SQLITE_CONSTRAINT_UNIQUE) or primary code 19 with a message that
    /// names <c>NotificationPreferences.UserId</c> → UserId unique conflict.
    /// Anything else → not our conflict.
    /// </summary>
    private static ClassifierDecision? ClassifySqlite(Exception ex, string message)
    {
        if (TryGetSqliteErrorCode(ex, out int sqliteCode))
        {
            if (sqliteCode is 5 or 6)
            {
                return ClassifierDecision.TransientProviderConflict;
            }

            // 2067 = SQLITE_CONSTRAINT_UNIQUE (extended). 19 = SQLITE_CONSTRAINT
            // (primary) — we still need the message to tell UserId apart from
            // some other unique constraint the schema may add later.
            if (sqliteCode is 2067 or 19
                && (message.Contains(UserIdIndexNeedle, StringComparison.OrdinalIgnoreCase)
                    || (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                        && message.Contains("NotificationPreferences.UserId", StringComparison.OrdinalIgnoreCase))))
            {
                return ClassifierDecision.UserIdUniqueConflict;
            }
        }

        // No error code exposed. Fall back to the SQLite standard "UNIQUE
        // constraint failed: NotificationPreferences.UserId" message shape.
        if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            && message.Contains("NotificationPreferences.UserId", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return null;
    }

    /// <summary>
    /// PostgreSQL / Npgsql SQLSTATE classification:
    /// 40001 serialization_failure, 40P01 deadlock_detected → transient.
    /// 23505 unique_violation → UserId conflict only when the constraint name
    /// on the exception references the <c>NotificationPreferences.UserId</c>
    /// index, otherwise rethrow.
    /// </summary>
    private static ClassifierDecision? ClassifyNpgsql(Exception ex)
    {
        if (!TryGetNpgsqlSqlState(ex, out string? state))
        {
            return null;
        }

        if (state is "40001" or "40P01")
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if (state != "23505")
        {
            return null;
        }

        string constraint = TryGetStringProperty(ex, "ConstraintName") ?? string.Empty;
        string tableName = TryGetStringProperty(ex, "TableName") ?? string.Empty;
        string columnName = TryGetStringProperty(ex, "ColumnName") ?? string.Empty;
        if (constraint.Contains(UserIdIndexNeedle, StringComparison.OrdinalIgnoreCase)
            || (tableName.Equals("NotificationPreferences", StringComparison.OrdinalIgnoreCase)
                && columnName.Equals("UserId", StringComparison.OrdinalIgnoreCase)))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return null;
    }

    /// <summary>
    /// SQL Server: 1205 deadlock victim, 3960 snapshot conflict, 1222 lock
    /// timeout → transient. 2601 (duplicate key on unique index) and 2627
    /// (unique constraint violation) → UserId conflict only when the message
    /// references the <c>IX_NotificationPreferences_UserId</c> index.
    /// </summary>
    private static ClassifierDecision? ClassifySqlServer(Exception ex, string message)
    {
        if (!TryGetIntProperty(ex, "Number", out int number))
        {
            return null;
        }

        if (number is 1205 or 3960 or 1222)
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if (number is 2601 or 2627
            && message.Contains(UserIdIndexNeedle, StringComparison.OrdinalIgnoreCase))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return null;
    }

    /// <summary>
    /// MySQL / MariaDB: 1213 deadlock, 1205 lock wait timeout → transient.
    /// 1062 duplicate entry → UserId conflict only when the offending index
    /// message references the <c>NotificationPreferences.UserId</c> index.
    /// </summary>
    private static ClassifierDecision? ClassifyMySql(Exception ex, string message)
    {
        if (!TryGetIntProperty(ex, "Number", out int number))
        {
            return null;
        }

        if (number is 1213 or 1205)
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if (number == 1062
            && message.Contains(UserIdIndexNeedle, StringComparison.OrdinalIgnoreCase))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return null;
    }

    private static bool TryGetSqliteErrorCode(Exception ex, out int code)
    {
        // Try SqliteErrorCode first; fall back to SqliteExtendedErrorCode when
        // the primary code is the generic SQLITE_CONSTRAINT (19) so the
        // classifier can tell UNIQUE (2067) apart from CHECK/FK.
        System.Reflection.PropertyInfo? prop = ex.GetType().GetProperty("SqliteErrorCode");
        if (prop is not null && prop.GetValue(ex) is int extracted)
        {
            code = extracted;
            if (extracted == 19)
            {
                System.Reflection.PropertyInfo? extProp = ex.GetType().GetProperty("SqliteExtendedErrorCode");
                if (extProp is not null && extProp.GetValue(ex) is int extendedCode)
                {
                    code = extendedCode;
                }
            }

            return true;
        }

        code = 0;
        return false;
    }

    private static bool TryGetNpgsqlSqlState(Exception ex, out string? state)
    {
        System.Reflection.PropertyInfo? prop = ex.GetType().GetProperty("SqlState");
        if (prop is not null && prop.GetValue(ex) is string extracted)
        {
            state = extracted;
            return true;
        }

        state = null;
        return false;
    }

    /// <summary>
    /// Reads an integer property (like SqlException.Number / MySqlException.Number)
    /// via reflection so this helper avoids a hard dependency on any specific
    /// provider assembly at compile time. Both providers happen to expose the
    /// error code under the same property name; sharing the accessor keeps the
    /// analyzer happy (S4144) and centralises the null/type checks.
    /// </summary>
    private static bool TryGetIntProperty(Exception ex, string propertyName, out int number)
    {
        System.Reflection.PropertyInfo? prop = ex.GetType().GetProperty(propertyName);
        if (prop is not null && prop.GetValue(ex) is int extracted)
        {
            number = extracted;
            return true;
        }

        number = 0;
        return false;
    }

    private static string? TryGetStringProperty(Exception ex, string propertyName)
    {
        System.Reflection.PropertyInfo? prop = ex.GetType().GetProperty(propertyName);
        if (prop is not null && prop.GetValue(ex) is string extracted)
        {
            return extracted;
        }

        return null;
    }
}
