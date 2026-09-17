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
    DateTimeOffset RecordedAt,
    IReadOnlyDictionary<string, string>? ServicePlatforms = null);

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

    /// <summary>
    /// A non-secret SHA256 fingerprint of this context's resolved connection string. Used only
    /// to detect -- never to reveal -- whether two in-process migration targets are pointed at
    /// different physical databases (Kane audit P0.6: a genuinely split AppDbContext/SlicerDbContext
    /// cannot be covered by the single shared "database" backup target). The default
    /// implementation returns empty, which preflight treats as "cannot compare" rather than
    /// "definitely equal".
    /// </summary>
    Task<string> GetConnectionStringFingerprintAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);
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
    IReadOnlySet<string> supportedProviderNames,
    IReadOnlySet<string>? mappedServiceIds = null) : IHostUpdatePreflightCheck
{
    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (mappedServiceIds is not null)
        {
            // A request target whose service ID has no configured ServiceMappings entry would
            // otherwise silently fall back to a guessed container name at verify/apply time
            // (Kane audit P0.5); fail closed here instead, before any mutating step runs.
            string[] unmapped = [.. request.Targets.Select(t => t.ServiceId).Where(id => !mappedServiceIds.Contains(id))];
            if (unmapped.Length > 0)
            {
                throw new HostUpdatePreflightFailedException($"unmapped_service_target:{string.Join(',', unmapped)}");
            }
        }

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

        var connectionStringFingerprintsByContext = new Dictionary<string, string>(StringComparer.Ordinal);
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

            string fingerprint = await target.GetConnectionStringFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fingerprint))
            {
                connectionStringFingerprintsByContext[target.ContextName] = fingerprint;
            }
        }

        // Defense-in-depth for Kane audit P0.6: the coordinated backup step covers a single
        // shared "database" target, which is architecturally correct only because every
        // documented deployment shape points AppDbContext and SlicerDbContext at the exact same
        // physical database (see SlicerModuleExtensions doc comments). If both contexts are
        // registered in this process and ever resolve to different connection strings, that
        // assumption is violated and the single-target backup would silently miss one of them --
        // fail closed rather than proceed with an incomplete backup.
        if (connectionStringFingerprintsByContext.TryGetValue("AppDbContext", out string? appFingerprint) &&
            connectionStringFingerprintsByContext.TryGetValue("SlicerDbContext", out string? slicerFingerprint) &&
            !string.Equals(appFingerprint, slicerFingerprint, StringComparison.Ordinal))
        {
            throw new HostUpdatePreflightFailedException("split_database_not_supported");
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
