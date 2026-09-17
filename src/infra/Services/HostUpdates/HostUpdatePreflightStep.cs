using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// The set of service digests and topology actually installed on this host as of the last
/// successfully completed and verified update, used to re-validate preflight prerequisites.
/// </summary>
public sealed record InstalledHostState(
    string ReleaseId,
    string ManifestDigest,
    IReadOnlyDictionary<string, string> ServiceDigests,
    string Topology,
    DateTimeOffset RecordedAt);

/// <summary>
/// Durable single-writer store for the host's last verified installed state. Used by preflight
/// (to detect drift) and by verify/recovery (to persist the newly confirmed state).
/// </summary>
public interface IInstalledHostStateStore
{
    Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken);
}

/// <summary>File-backed <see cref="IInstalledHostStateStore"/> using an atomic write-then-rename.</summary>
public sealed class FileInstalledHostStateStore(string path) : IInstalledHostStateStore
{
    public async Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<InstalledHostState>(json);
    }

    public async Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : ".");
        string json = JsonSerializer.Serialize(state);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Thrown when preflight determines the host is not safe to update.</summary>
#pragma warning disable CA1032 // Only ever constructed with a code; standard constructors are not used.
public sealed class HostUpdatePreflightFailedException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
#pragma warning restore CA1032

/// <summary>
/// Reports one migration-owning database context so preflight and migration coordination can
/// inspect its actual live provider and apply its migrations under the same execution owner.
/// </summary>
public interface IHostUpdateMigrationTarget
{
    string ContextName { get; }

    Task<string> GetProviderNameAsync(CancellationToken cancellationToken);

    Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Revalidates the immutable request against the actually installed host state, checks
/// provider/topology/updater compatibility, and confirms disk and runtime prerequisites
/// before any mutating step runs.
/// </summary>
public sealed class HostUpdatePreflightCheck(
    IInstalledHostStateStore installedStateStore,
    IReadOnlyList<IHostUpdateMigrationTarget> migrationTargets,
    IHostUpdateProcessRunner processRunner,
    string diskWatchPath,
    long minimumFreeBytes,
    IReadOnlySet<string> supportedProviderNames) : IHostUpdatePreflightCheck
{
    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        InstalledHostState? installed = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (installed is not null)
        {
            // The installed topology (service count/set) must match this request's target set
            // unless this is the very first update recorded for the host.
            var installedServiceIds = installed.ServiceDigests.Keys.ToHashSet(StringComparer.Ordinal);
            var requestedServiceIds = request.Targets.Select(t => t.ServiceId).ToHashSet(StringComparer.Ordinal);
            if (!installedServiceIds.SetEquals(requestedServiceIds))
            {
                throw new HostUpdatePreflightFailedException("topology_mismatch");
            }
        }

        foreach (IHostUpdateMigrationTarget target in migrationTargets)
        {
            string provider;
            try
            {
                provider = await target.GetProviderNameAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new HostUpdatePreflightFailedException("provider_inspection_failed");
            }

            if (!supportedProviderNames.Contains(provider))
            {
                throw new HostUpdatePreflightFailedException($"unsupported_provider:{provider}");
            }
        }

        if (!TryGetFreeBytes(diskWatchPath, out long freeBytes) || freeBytes < minimumFreeBytes)
        {
            throw new HostUpdatePreflightFailedException("insufficient_disk_space");
        }

        HostUpdateProcessResult dockerVersion;
        try
        {
            dockerVersion = await processRunner.RunAsync(
                "docker",
                ["version", "--format", "{{.Server.Version}}"],
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HostUpdatePreflightFailedException("runtime_unavailable");
        }

        if (!dockerVersion.Succeeded)
        {
            throw new HostUpdatePreflightFailedException("runtime_unavailable");
        }
    }

    private static bool TryGetFreeBytes(string path, out long freeBytes)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            var drive = new DriveInfo(root);
            freeBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            freeBytes = 0;
            return false;
        }
    }
}

/// <summary>Runs the preflight step of the host update executor.</summary>
public interface IHostUpdatePreflightCheck
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}
