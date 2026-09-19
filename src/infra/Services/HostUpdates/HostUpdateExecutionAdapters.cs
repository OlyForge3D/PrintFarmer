using System.Collections.Frozen;
using System.Diagnostics;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Result of running an external process without a shell, capturing exit code and output for
/// diagnostics. Never includes secrets in <see cref="StandardOutput"/>/<see cref="StandardError"/>
/// callers are expected to redact sensitive arguments before logging them.
/// </summary>
public sealed record HostUpdateProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a host-local executable using an explicit argument list (never a shell command line),
/// so adapters cannot be subverted by argument/shell interpolation.
/// </summary>
public interface IHostUpdateProcessRunner
{
    Task<HostUpdateProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);
}

public interface IHostUpdateExecutableResolver
{
    string Resolve(string toolName);
}

public sealed class ConfiguredHostUpdateExecutableResolver(IReadOnlyDictionary<string, string> executablePaths)
    : IHostUpdateExecutableResolver
{
    public string Resolve(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!executablePaths.TryGetValue(toolName, out string? path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"host_update_executable_not_configured:{toolName}");
        }

        if (!Path.IsPathRooted(path))
        {
            throw new InvalidOperationException($"host_update_executable_path_not_configured:{toolName}");
        }

        return Path.GetFullPath(path);
    }
}

/// <summary>
/// Default <see cref="IHostUpdateProcessRunner"/> backed by <see cref="Process"/>. Arguments are
/// passed via <see cref="ProcessStartInfo.ArgumentList"/> so no shell parses them.
/// </summary>
public sealed class DefaultHostUpdateProcessRunner : IHostUpdateProcessRunner
{
    public async Task<HostUpdateProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Process '{fileName}' timed out after {timeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);
        return new HostUpdateProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill attempt.
        }
    }
}

/// <summary>
/// Restricts host-update execution to the audited tools used by the concrete adapters. This is a
/// defense-in-depth boundary: callers still provide explicit arguments, but cannot turn the
/// process runner into a general-purpose host command executor. Bare names are rejected;
/// rooted executable paths must be explicitly configured or reside in fixed system tool
/// directories, never an ambient PATH directory.
/// </summary>
public sealed class ConstrainedHostUpdateProcessRunner(
    IHostUpdateProcessRunner inner,
    IReadOnlySet<string>? configuredExecutablePaths = null) : IHostUpdateProcessRunner
{
    private static readonly FrozenSet<string> AllowedExecutables =
        new[] { "docker", "sqlite3", "pg_dump", "pg_restore", "sqlcmd" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] TrustedExecutableDirectories = BuildTrustedExecutableDirectories();

    public Task<HostUpdateProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        string executableName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        if (!AllowedExecutables.Contains(executableName)
            || fileName.EndsWith('.')
            || (extension.Length > 0 && !string.Equals(extension, ".exe", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"host_update_executable_not_allowed:{fileName}");
        }

        if (!Path.IsPathRooted(fileName))
        {
            throw new InvalidOperationException($"host_update_executable_path_not_configured:{fileName}");
        }

        if (!IsTrustedExecutablePath(fileName))
        {
            throw new InvalidOperationException($"host_update_executable_path_not_trusted:{fileName}");
        }

        return inner.RunAsync(fileName, arguments, timeout, cancellationToken, environment);
    }

    private bool IsTrustedExecutablePath(string fileName)
    {
        string fullPath = Path.GetFullPath(fileName);
        if (configuredExecutablePaths?.Contains(fullPath) == true)
        {
            return true;
        }

        string? directory = Path.GetDirectoryName(fullPath);
        return directory is not null
            && TrustedExecutableDirectories.Any(trustedDirectory =>
                string.Equals(directory, trustedDirectory, StringComparison.Ordinal));
    }

    private static string[] BuildTrustedExecutableDirectories() =>
    [
        .. GetKnownSystemDirectories(),
    ];

    private static IEnumerable<string> GetKnownSystemDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            string systemDirectory = Environment.SystemDirectory;
            if (!string.IsNullOrWhiteSpace(systemDirectory))
            {
                yield return Path.GetFullPath(systemDirectory);
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.GetFullPath(programFiles);
            }

            yield break;
        }

        yield return "/bin";
        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/sbin";
        yield return "/usr/sbin";
    }
}

/// <summary>Default-off installed-state store: reads empty and rejects mutations until a host-controlled root is configured.</summary>
public sealed class UnconfiguredInstalledHostStateStore : IInstalledHostStateStore
{
    public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<InstalledHostState?>(null);

    public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("root_directory_not_configured");
}

/// <summary>Default-off execution journal: read-only empty surface so startup/status remain inert while execution is unavailable.</summary>
public sealed class UnconfiguredHostUpdateExecutionJournal : IHostUpdateExecutionJournal
{
    public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => [];

    public IReadOnlyList<string> ListReleaseIds() => [];

    public void Append(HostUpdateExecutionActivity activity) => throw new InvalidOperationException("root_directory_not_configured");
}

/// <summary>Default-off execution lock: any attempted mutation fails closed until a host-controlled root is configured.</summary>
public sealed class UnconfiguredHostUpdateExecutionLock : IHostUpdateExecutionLock
{
    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("root_directory_not_configured");
}

/// <summary>Default-off backup locator: no backup history exists when the executor root is unconfigured.</summary>
public sealed class UnconfiguredHostUpdateBackupManifestLocator : IHostUpdateBackupManifestLocator
{
    public Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken) =>
        Task.FromResult<(HostUpdateBackupManifest Manifest, string RunDirectory)?>(null);
}

/// <summary>Default-off recovery outcome store: reads empty and rejects mutation until durable state is configured.</summary>
public sealed class UnconfiguredHostUpdateRecoveryOutcomeStore : IHostUpdateRecoveryOutcomeStore
{
    public Task<HostUpdateRecoveryOutcomeRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken) =>
        Task.FromResult<HostUpdateRecoveryOutcomeRecord?>(null);

    public Task WriteAsync(HostUpdateRecoveryOutcomeRecord record, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("root_directory_not_configured");
}

/// <summary>Default-off foundation journal: no trusted staged request exists until durable host state is configured.</summary>
public sealed class UnconfiguredHostUpdateJournal : IHostUpdateJournal
{
    public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) =>
        throw new InvalidOperationException("root_directory_not_configured");

    public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>([]);
}

/// <summary>Default-off backup coordinator: any direct execution attempt fails closed before mutation.</summary>
public sealed class UnconfiguredHostUpdateBackupCoordinator : IHostUpdateBackupCoordinator
{
    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("root_directory_not_configured");
}
