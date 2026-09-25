using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Data.Sqlite;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Proves, without executing anything, that this process sees the same filesystem and tool
/// namespace the recovery engine will act on: the configured root and its durable state, the
/// compose files, the owned directories, and the explicitly configured host executables. Any
/// failure stops <c>recover --confirm</c> before the lock, the journal, or a host tool is touched.
/// </summary>
internal static class HostUpdateNamespaceProof
{
    public static IReadOnlyList<string> Check(
        HostUpdateExecutionOptions options,
        DatabaseProviderConfiguration database,
        IHostUpdateExecutableResolver resolver)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Path.IsPathRooted(options.RootDirectory))
        {
            failures.Add("root_directory_not_configured");
            return failures;
        }

        if (!Directory.Exists(options.RootDirectory))
        {
            failures.Add("root_directory_missing");
            return failures;
        }

        if (!Directory.Exists(options.StateDirectory))
        {
            failures.Add("state_directory_missing");
        }
        else if (!File.Exists(Path.Combine(options.StateDirectory, "journal.ndjson")))
        {
            failures.Add("journal_missing");
        }

        if (options.ComposeFiles.Length == 0)
        {
            failures.Add("compose_files_not_configured");
        }

        foreach (string composeFile in options.ComposeFiles)
        {
            if (string.IsNullOrWhiteSpace(composeFile) || !File.Exists(Path.GetFullPath(composeFile)))
            {
                failures.Add($"compose_file_missing:{composeFile}");
            }
        }

        var optional = new HashSet<string>(options.OptionalOwnedDirectories, StringComparer.Ordinal);
        foreach ((string name, string path) in options.OwnedDirectories)
        {
            if (!optional.Contains(name) && (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !Directory.Exists(path)))
            {
                failures.Add($"owned_directory_missing:{name}");
            }
        }

        CheckExecutable(failures, () => resolver.Resolve("docker"), "docker");
        CheckExecutable(
            failures,
            () => HostUpdateDatabaseBackupTargetFactory.CreateRestoreCommand(database, resolver)(options.BackupRootDirectory).FileName,
            "database_restore_tool");

        if (database.IsSqlite)
        {
            string dataSource;
            try
            {
                dataSource = new SqliteConnectionStringBuilder(database.ConnectionString).DataSource;
            }
            catch (ArgumentException)
            {
                dataSource = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(dataSource) || !Path.IsPathRooted(dataSource) || !File.Exists(dataSource))
            {
                failures.Add("sqlite_database_not_visible");
            }
        }

        return failures;
    }

    private static void CheckExecutable(List<string> failures, Func<string> resolve, string name)
    {
        try
        {
            string path = resolve();
            if (!Path.IsPathRooted(path) || !File.Exists(path))
            {
                failures.Add($"executable_missing:{name}");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            failures.Add($"executable_not_configured:{name}");
        }
    }
}
