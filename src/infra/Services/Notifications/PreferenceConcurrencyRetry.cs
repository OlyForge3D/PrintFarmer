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
/// concurrency exceptions, and the unique-index violation raised when two
/// concurrent first-creations both try to insert the same UserId row (Bishop
/// minor #1). Cancellation propagates unconditionally.
///
/// The helper takes a delegate that opens a FRESH <see cref="AppDbContext"/> via
/// <see cref="IDbContextFactory{TContext}"/> per attempt (Hicks #2). This
/// guarantees that a stale change-tracker snapshot from the losing attempt
/// never leaks into the retried transaction.
/// </summary>
public static class PreferenceConcurrencyRetry
{
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
                catch (DbUpdateConcurrencyException ex)
                {
                    // Hicks #1 parity: OperationCanceledException surfaces on
                    // its own catch (see filter below); this catch handles
                    // ONLY the concurrency conflict and never masks cancels.
                    lastTransient = ex;
                    LogRetry(logger, attempt, "concurrency", ex);
                    if (attempt == MaxAttempts)
                    {
                        break;
                    }

                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (DbUpdateException ex) when (IsTransientRelationalConflict(ex))
                {
                    lastTransient = ex;
                    LogRetry(logger, attempt, "provider-conflict", ex);
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
    /// Recognises the narrow set of relational provider transient failures we retry:
    /// SQLite BUSY/LOCKED, PostgreSQL serialization/deadlock, SQL Server deadlock/
    /// snapshot conflicts, MySQL deadlock, and — critical for Bishop minor #1 — the
    /// unique-index violation surfaced when two concurrent first-creation writers
    /// both insert the same UserId row. Any other DbUpdateException (schema error,
    /// non-transient FK violation, etc.) is a genuine fault and is NOT retried.
    /// </summary>
    private static bool IsTransientRelationalConflict(DbUpdateException exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            string typeName = current.GetType().Name;
            string message = current.Message ?? string.Empty;

            // SQLite: SQLITE_BUSY (5), SQLITE_LOCKED (6). Also detect the
            // constraint failure (SQLITE_CONSTRAINT_UNIQUE = 2067, code 19) that
            // fires when two concurrent inserts both target the same UserId.
            if (typeName == "SqliteException")
            {
                if (TryGetSqliteErrorCode(current, out int sqliteCode))
                {
                    if (sqliteCode == 5 || sqliteCode == 6)
                    {
                        return true;
                    }

                    if (sqliteCode == 19)
                    {
                        // Bishop minor #1: concurrent first-creation collision.
                        // The retry path re-reads and merges into the existing row.
                        return true;
                    }
                }

                if (message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // PostgreSQL / Npgsql: 40001 serialization_failure, 40P01 deadlock_detected,
            // 23505 unique_violation.
            if (typeName == "PostgresException")
            {
                if (TryGetNpgsqlSqlState(current, out string? state))
                {
                    if (state is "40001" or "40P01" or "23505")
                    {
                        return true;
                    }
                }
            }

            // SQL Server: 1205 deadlock victim, 3960 snapshot conflict, 2601/2627 unique.
            if (typeName == "SqlException")
            {
                if (TryGetSqlServerErrorNumber(current, out int number))
                {
                    if (number is 1205 or 3960 or 2601 or 2627)
                    {
                        return true;
                    }
                }
            }

            // MySQL / MariaDB: 1213 deadlock, 1062 duplicate entry.
            if (typeName == "MySqlException")
            {
                if (TryGetMySqlErrorNumber(current, out int mysqlNumber))
                {
                    if (mysqlNumber is 1213 or 1062)
                    {
                        return true;
                    }
                }
            }

            current = current.InnerException;
        }

        return false;
    }

    private static bool TryGetSqliteErrorCode(Exception ex, out int code)
    {
        System.Reflection.PropertyInfo? prop = ex.GetType().GetProperty("SqliteErrorCode");
        if (prop is not null && prop.GetValue(ex) is int extracted)
        {
            code = extracted;
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

    private static bool TryGetSqlServerErrorNumber(Exception ex, out int number)
        => TryGetIntProperty(ex, "Number", out number);

    private static bool TryGetMySqlErrorNumber(Exception ex, out int number)
        => TryGetIntProperty(ex, "Number", out number);

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
}
