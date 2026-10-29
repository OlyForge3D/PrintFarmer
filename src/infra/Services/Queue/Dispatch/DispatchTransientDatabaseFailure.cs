using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Classifies the provider lock-contention failures that the auto-dispatch durable scan may
/// safely defer to its next interval (#3604). Only exact provider exception types are accepted,
/// so wrapped, unknown or schema/data failures are never mistaken for contention.
/// </summary>
internal static class DispatchTransientDatabaseFailure
{
    /// <summary>
    /// Returns <see langword="true"/> for SQLite <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c>,
    /// PostgreSQL serialization failure/deadlock/lock-not-available, and SQL Server
    /// deadlock-victim/lock-request-timeout errors.
    /// </summary>
    public static bool IsTransientContention(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.GetType() == typeof(SqliteException))
        {
            return ((SqliteException)exception).SqliteErrorCode is 5 or 6;
        }

        if (exception.GetType() == typeof(PostgresException))
        {
            return ((PostgresException)exception).SqlState is PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable;
        }

        if (exception.GetType() == typeof(SqlException))
        {
            return ((SqlException)exception).Number is 1205 or 1222;
        }

        return false;
    }
}
