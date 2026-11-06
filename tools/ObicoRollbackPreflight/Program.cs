namespace ObicoRollbackPreflight;

using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

/// <summary>Checks restored Obico settings without starting printer services.</summary>
internal static class Program
{
    /// <summary>Returns a blocking exit code unless the restored row is compatible.</summary>
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("sqlite" or "postgres" or "sqlserver"))
        {
            await Console.Error.WriteLineAsync("Usage: ObicoRollbackPreflight <sqlite|postgres|sqlserver> <restored-db-path|-> <old-infrastructure-dll> <trusted-dll-sha256>");
            await Console.Error.WriteLineAsync("Server providers read PFARM_ROLLBACK_CONNECTION_STRING. Use an isolated restored database, never production.");
            return 2;
        }

        try
        {
            string assemblyPath = Path.GetFullPath(args[2]);
            VerifyAssemblyHash(assemblyPath, args[3]);
            await using DbConnection connection = CreateConnection(args[0], args[1]);
            await connection.OpenAsync();
            await using DbTransaction transaction = await connection.BeginTransactionAsync();
            if (args[0] == "postgres")
            {
                await using DbCommand readOnly = connection.CreateCommand();
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync();
            }

            string settingsJson = await ReadRestoredSettingsAsync(connection, transaction, args[0]);
            ValidateWithOldAssembly(assemblyPath, settingsJson);
            await transaction.RollbackAsync();
            await Console.Out.WriteLineAsync("PASS: restored Obico settings validated by the hash-verified rollback assembly. No settings were changed; no API or printer services were started.");
            await Console.Out.WriteLineAsync("This checks Obico settings only, not backup matching, other settings, schema compatibility, or authorization to start the old API.");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Provider/JSON/validation messages can contain deployment secrets.
            await Console.Error.WriteLineAsync($"BLOCKED: restored-settings compatibility could not be proven ({error.GetType().Name}). Do not start the old API. No monitoring settings were repaired or disabled.");
            return 1;
        }
    }

    /// <summary>Verifies the selected validator artifact against a separately trusted hash.</summary>
    internal static void VerifyAssemblyHash(string path, string expectedHash)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("A trusted rollback assembly SHA-256 is required.");
        }

        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rollback assembly hash mismatch.");
        }
    }

    /// <summary>Opens SQLite read-only or uses the explicitly supplied isolated server connection.</summary>
    internal static DbConnection CreateConnection(string provider, string database)
    {
        if (provider == "sqlite")
        {
            return new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(database),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        }

        if (database != "-")
        {
            throw new InvalidOperationException("Server connection strings must not be supplied on the command line.");
        }

        string connectionString = Environment.GetEnvironmentVariable("PFARM_ROLLBACK_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Missing isolated restore connection.");
        return provider == "postgres"
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);
    }

    /// <summary>Requires exactly one nonempty Obico settings row without changing it.</summary>
    internal static async Task<string> ReadRestoredSettingsAsync(DbConnection connection, DbTransaction transaction, string provider)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = provider == "sqlserver"
            ? "SELECT [SettingsJson] FROM [AppSettingsEntities] WHERE [Key] = @key"
            : "SELECT \"SettingsJson\" FROM \"AppSettingsEntities\" WHERE \"Key\" = @key";
        DbParameter key = command.CreateParameter();
        key.ParameterName = "@key";
        key.Value = "Obico";
        command.Parameters.Add(key);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || await reader.IsDBNullAsync(0))
        {
            throw new InvalidOperationException("Restored Obico row is missing; configuration fallback must be separately verified.");
        }

        string json = reader.GetString(0);
        if (string.IsNullOrWhiteSpace(json) || await reader.ReadAsync())
        {
            throw new InvalidOperationException("Exactly one nonempty restored Obico row is required.");
        }

        return json;
    }

    /// <summary>Deserializes with the rollback settings type and invokes its validator.</summary>
    internal static void ValidateWithOldAssembly(string assemblyPath, string json)
    {
        var context = new RollbackAssemblyContext(assemblyPath);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
            Type type = assembly.GetType("Farm.Infrastructure.Settings.ObicoSettings", throwOnError: true)!;
            object settings = JsonSerializer.Deserialize(json, type)
                ?? throw new InvalidOperationException("Restored Obico settings are null.");
            MethodInfo validate = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
                ?? throw new InvalidOperationException("Rollback validator is absent.");
            validate.Invoke(settings, null);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Loads the selected rollback assembly and its packaged dependencies.</summary>
    private sealed class RollbackAssemblyContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? path = this.resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : this.LoadFromAssemblyPath(path);
        }
    }
}
