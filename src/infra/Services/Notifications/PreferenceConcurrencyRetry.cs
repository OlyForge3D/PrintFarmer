using Farm.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Executes preference writes as bounded, whole serializable transactions with a fresh
/// <see cref="AppDbContext"/> on every attempt.
/// </summary>
public static class PreferenceConcurrencyRetry
{
    private const string UserIdIndexName = "IX_NotificationPreferences_UserId";
    private const string SqliteUserIdConstraint = "NotificationPreferences.UserId";

    /// <summary>Classification of an exception observed at the operation boundary.</summary>
    public enum ClassifierDecision
    {
        /// <summary>Not a supported concurrency signal; preserve and rethrow the original exception.</summary>
        Rethrow,

        /// <summary>A supported provider serialization, deadlock, lock, or EF concurrency conflict.</summary>
        TransientProviderConflict,

        /// <summary>The exact preferences UserId unique key lost a concurrent first-create race.</summary>
        UserIdUniqueConflict,
    }

    /// <summary>Maximum whole-operation attempts, including the first attempt.</summary>
    public const int MaxAttempts = 4;

    /// <summary>
    /// Executes <paramref name="operation"/> with a fresh factory context per attempt.
    /// A non-factory fallback is deliberately single-shot because its tracker cannot be refreshed safely.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        IDbContextFactory<AppDbContext>? factory,
        AppDbContext? fallbackContext,
        Func<AppDbContext, CancellationToken, Task<T>> operation,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(logger);

        if (factory is null)
        {
            if (fallbackContext is null)
            {
                throw new InvalidOperationException(
                    "PreferenceConcurrencyRetry requires either a DbContext factory or a fallback context.");
            }

            return await operation(fallbackContext, cancellationToken).ConfigureAwait(false);
        }

        Exception? lastConflict = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AppDbContext context = await factory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                try
                {
                    return await operation(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ClassifierDecision decision = Classify(exception);
                    if (decision == ClassifierDecision.Rethrow)
                    {
                        throw;
                    }

                    lastConflict = exception;
                    string reason = decision == ClassifierDecision.UserIdUniqueConflict
                        ? "userid-unique"
                        : "provider-conflict";
                    logger.LogWarning(
                        exception,
                        "[Notifications/Preferences] Transient {Reason} on attempt {Attempt}/{Max}; retrying with a fresh DbContext.",
                        reason,
                        attempt,
                        MaxAttempts);

                    if (attempt == MaxAttempts)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw lastConflict!;
    }

    /// <summary>
    /// Accepts only an exact EF concurrency exception, or an exact supported provider exception
    /// either directly or immediately inside an exact <see cref="DbUpdateException"/>. Arbitrary
    /// wrappers and deeper nesting are rejected so unrelated failures cannot consume retry budget.
    /// </summary>
    internal static ClassifierDecision Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.GetType() == typeof(DbUpdateConcurrencyException))
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        Exception providerException = exception;
        if (exception.GetType() == typeof(DbUpdateException))
        {
            if (exception.InnerException is null)
            {
                return ClassifierDecision.Rethrow;
            }

            providerException = exception.InnerException;
        }

        if (providerException.GetType() == typeof(SqliteException))
        {
            return ClassifySqlite((SqliteException)providerException);
        }

        if (providerException.GetType() == typeof(PostgresException))
        {
            return ClassifyPostgres((PostgresException)providerException);
        }

        if (providerException.GetType() == typeof(SqlException))
        {
            return ClassifySqlServer((SqlException)providerException);
        }

        return ClassifierDecision.Rethrow;
    }

    private static ClassifierDecision ClassifySqlite(SqliteException exception)
    {
        if (exception.SqliteErrorCode is 5 or 6)
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if (exception.SqliteExtendedErrorCode == 2067
            && IsExactSqliteUserIdConflict(exception.Message))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return ClassifierDecision.Rethrow;
    }

    private static ClassifierDecision ClassifyPostgres(PostgresException exception)
    {
        if (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, UserIdIndexName, StringComparison.Ordinal))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return ClassifierDecision.Rethrow;
    }

    private static ClassifierDecision ClassifySqlServer(SqlException exception)
    {
        if (exception.Number is 1205 or 3960 or 1222)
        {
            return ClassifierDecision.TransientProviderConflict;
        }

        if ((exception.Number is 2601 or 2627)
            && NamesExactSqlServerIndex(exception.Message, UserIdIndexName))
        {
            return ClassifierDecision.UserIdUniqueConflict;
        }

        return ClassifierDecision.Rethrow;
    }

    private static bool IsExactSqliteUserIdConflict(string message)
    {
        const string marker = "UNIQUE constraint failed:";
        int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        string columns = message[(markerIndex + marker.Length)..]
            .Trim()
            .Trim('\'', '"', '.');
        return string.Equals(columns, SqliteUserIdConstraint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesExactSqlServerIndex(string message, string indexName)
    {
        return message.Contains($"'{indexName}'", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"\"{indexName}\"", StringComparison.OrdinalIgnoreCase)
            || message.Contains($"[{indexName}]", StringComparison.OrdinalIgnoreCase);
    }
}
