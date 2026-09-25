using System.Data.Common;
using Farm.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Reads the database-side manifest binding (the signed manifest digest persisted by
/// <see cref="VerifiedReleaseManifestBindingStore"/>) for a release, read-only (issue #3050).
/// Shared by the executor, which journals it in the authorization baseline, and the host-local
/// recovery CLI, which compares it at recovery time.
/// </summary>
public interface IHostUpdateManifestBindingReader
{
    /// <summary>
    /// Returns the bound manifest digest, or <see cref="ReadOnlyHostUpdateManifestBindingReader.NoBinding"/>
    /// when the release has none. Throws when the binding cannot be read or is invalid, so a caller
    /// can never mistake an unreadable binding for an absent one.
    /// </summary>
    Task<string> ReadAsync(string releaseId, CancellationToken cancellationToken);
}

/// <summary>
/// Issues a single parameterised <c>SELECT</c> over a connection that the provider itself holds
/// read-only: SQLite opens with <c>Mode=ReadOnly</c>, PostgreSQL runs inside a
/// <c>READ ONLY</c> transaction, and SQL Server declares <c>ApplicationIntent=ReadOnly</c> and
/// rolls its transaction back. It uses raw ADO.NET, so no EF model, migration or schema check
/// runs. Connection strings are never logged, fingerprinted or surfaced in exceptions.
/// </summary>
public sealed class ReadOnlyHostUpdateManifestBindingReader(DatabaseProviderConfiguration database) : IHostUpdateManifestBindingReader
{
    public const string NoBinding = "none";

    private const int CommandTimeoutSeconds = 15;

    private readonly DatabaseProviderConfiguration _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<string> ReadAsync(string releaseId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        await using DbConnection connection = CreateReadOnlyConnection(_database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (_database.IsPostgres)
        {
            await using DbCommand readOnly = Command(connection, transaction, "SET TRANSACTION READ ONLY");
            _ = await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string sql = _database.IsSqlServer
            ? "SELECT [SettingsJson] FROM [AppSettingsEntities] WHERE [Key] = @key"
            : "SELECT \"SettingsJson\" FROM \"AppSettingsEntities\" WHERE \"Key\" = @key";
        object? value;
        await using (DbCommand command = Command(connection, transaction, sql))
        {
            DbParameter key = command.CreateParameter();
            key.ParameterName = "@key";
            key.Value = VerifiedReleaseManifestBindingStore.KeyFor(releaseId);
            _ = command.Parameters.Add(key);
            value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? NoBinding
            : VerifiedReleaseManifestBindingStore.ParseDigest(releaseId, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    internal static DbConnection CreateReadOnlyConnection(DatabaseProviderConfiguration database)
    {
        if (database.IsSqlite)
        {
            // Pooling is disabled so the CLI never keeps the host database file open after it exits.
            return new SqliteConnection(new SqliteConnectionStringBuilder(database.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        }

        if (database.IsPostgres)
        {
            return new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Pooling = false,
            }.ToString());
        }

        if (database.IsSqlServer)
        {
            return new SqlConnection(new SqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationIntent = ApplicationIntent.ReadOnly,
                Pooling = false,
            }.ToString());
        }

        throw new NotSupportedException("database_provider_unsupported");
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql;
        return command;
    }
}
