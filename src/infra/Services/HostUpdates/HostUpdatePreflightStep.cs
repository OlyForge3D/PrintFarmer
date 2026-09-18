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

/// <summary>File-backed <see cref="IInstalledHostStateStore"/> using a durable atomic write.</summary>
public sealed class FileInstalledHostStateStore(string path) : IInstalledHostStateStore
{
    public async Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        InstalledHostState? state;
        try
        {
            state = JsonSerializer.Deserialize<InstalledHostState>(json);
        }
        catch (JsonException exception)
        {
            throw new HostUpdateInstalledStateCorruptException("json_invalid", exception);
        }

        // Bishop/Hicks review (issue #2663): a present-but-unusable file must never be reported as
        // "no installed state" (which preflight reads as a first install) and must never surface
        // later as an incidental NullReferenceException. JSON `null`, `{}`, and records missing any
        // required member all deserialize without throwing, so the record is validated here and a
        // specific corruption exception is raised, letting execute/recovery fail closed
        // deterministically to NeedsOperator.
        return Validate(state);
    }

    /// <summary>
    /// Validates that a deserialized record is semantically canonical, not merely non-blank. This
    /// state is the sole evidence recovery uses to decide what the host is allowed to be rolled
    /// back to, so a syntactically-parseable but semantically wrong record (a truncated digest, a
    /// bogus platform, a service set that disagrees with the recorded topology) must fail closed
    /// rather than drive a restore toward a state that was never actually installed.
    /// </summary>
    internal static InstalledHostState Validate(InstalledHostState? state)
    {
        if (state is null)
        {
            throw new HostUpdateInstalledStateCorruptException("record_null");
        }

        if (!HostUpdateValidation.IsReleaseId(state.ReleaseId))
        {
            throw new HostUpdateInstalledStateCorruptException("release_id_invalid");
        }

        if (!HostUpdateValidation.IsCanonicalDigest(state.ManifestDigest))
        {
            throw new HostUpdateInstalledStateCorruptException("manifest_digest_invalid");
        }

        if (state.ServiceDigests is null || state.ServiceDigests.Count == 0)
        {
            throw new HostUpdateInstalledStateCorruptException("service_digests_missing");
        }

        foreach (KeyValuePair<string, string> digest in state.ServiceDigests)
        {
            if (!HostUpdateValidation.IsIdentifier(digest.Key))
            {
                throw new HostUpdateInstalledStateCorruptException("service_id_invalid");
            }

            if (!HostUpdateValidation.IsCanonicalDigest(digest.Value))
            {
                throw new HostUpdateInstalledStateCorruptException("service_digest_invalid");
            }
        }

        // The platform map is what recovery replays digests with, so a missing, partial, or
        // over-broad map is unusable evidence rather than an optional extra: it must describe
        // exactly the same service set as the digests it accompanies.
        if (state.ServicePlatforms is null)
        {
            throw new HostUpdateInstalledStateCorruptException("service_platforms_missing");
        }

        if (state.ServicePlatforms.Count != state.ServiceDigests.Count ||
            !state.ServicePlatforms.Keys.All(state.ServiceDigests.ContainsKey))
        {
            throw new HostUpdateInstalledStateCorruptException("service_platforms_mismatch");
        }

        foreach (KeyValuePair<string, string> platform in state.ServicePlatforms)
        {
            if (!SignedUpdateManifestValidator.IsPlatform(platform.Value))
            {
                throw new HostUpdateInstalledStateCorruptException("service_platform_invalid");
            }
        }

        // Topology is derived data, so it is only trustworthy when it still agrees with the service
        // set it was derived from. Comparing against the canonical projection rejects duplicated,
        // unknown, missing, and mis-ordered entries in one check.
        if (!string.Equals(state.Topology, CanonicalTopology(state.ServiceDigests.Keys), StringComparison.Ordinal))
        {
            throw new HostUpdateInstalledStateCorruptException("topology_invalid");
        }

        return state;
    }

    /// <summary>
    /// The canonical topology projection. Must stay identical to the one the executor records in
    /// <c>HostUpdateExecutionStepsAdapter</c>; any divergence would make every written record fail
    /// its own validation on read back.
    /// </summary>
    internal static string CanonicalTopology(IEnumerable<string> serviceIds) =>
        string.Join('+', serviceIds.OrderBy(id => id, StringComparer.Ordinal));

    public async Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Validated on the way out as well as the way in: persisting non-canonical evidence would
        // only surface as an unreadable state file at the next preflight/recovery, long after the
        // context that produced it is gone.
        Validate(state);
        Directory.CreateDirectory(Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : ".");
        string json = JsonSerializer.Serialize(state);
        await Task.Run(() => HostUpdateDurableFile.WriteAllTextAtomic(path, json), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Thrown when the durable installed-state file exists but does not contain a usable record.
/// Distinct from "no installed state": a corrupt or truncated file must fail closed rather than be
/// mistaken for a first install.
/// </summary>
#pragma warning disable CA1032 // Only ever constructed with a code; standard constructors are not used.
public sealed class HostUpdateInstalledStateCorruptException : InvalidOperationException
{
    public HostUpdateInstalledStateCorruptException(string code)
        : base("installed_state_corrupt:" + code) => Code = code;

    public HostUpdateInstalledStateCorruptException(string code, Exception innerException)
        : base("installed_state_corrupt:" + code, innerException) => Code = code;

    public string Code { get; }
}
#pragma warning restore CA1032

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

    Task<bool> HasPendingMigrationsAsync(CancellationToken cancellationToken);

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
