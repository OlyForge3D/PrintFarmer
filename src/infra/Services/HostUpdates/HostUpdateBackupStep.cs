using System.Security.Cryptography;
using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when a required backup target is owned externally and cannot be safely backed up here.</summary>
public sealed class HostUpdateBackupUnsupportedOwnerException(IReadOnlyList<string> externallyOwnedTargetNames)
    : InvalidOperationException($"external_backup_owner_unsupported:{string.Join(',', externallyOwnedTargetNames)}")
{
    public IReadOnlyList<string> ExternallyOwnedTargetNames { get; } = externallyOwnedTargetNames;
}

/// <summary>Thrown when a backup target fails to produce any files or explicit directory coverage.</summary>
public sealed class HostUpdateBackupIncompleteException(string targetName) : InvalidOperationException($"backup_incomplete:{targetName}");
#pragma warning restore CA1032

/// <summary>
/// One coordinated, checksummed backup target: an application database context, or application
/// owned blobs/profiles/calibration/config/certificates/keyrings. Never covers a database or
/// store the operator has declared externally owned (<see cref="IsExternallyOwned"/>).
/// </summary>
public interface IHostUpdateBackupTarget
{
    string Name { get; }

    /// <summary>
    /// True when this target's underlying store (e.g. a customer-managed external PostgreSQL
    /// server) is not owned by this host and cannot be safely backed up by this coordinator.
    /// </summary>
    bool IsExternallyOwned { get; }

    /// <summary>Writes this target's backup content into <paramref name="destinationDirectory"/>.</summary>
    Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken);
}

/// <summary>One file recorded, checksummed, in a completed backup manifest.</summary>
public sealed record HostUpdateBackupManifestFile(string RelativePath, string Sha256, long Length);

/// <summary>
/// Durable, checksummed record of a completed coordinated backup. Never contains secret
/// values; only non-sensitive relative paths and content hashes.
/// </summary>
public sealed record HostUpdateBackupManifest(
    string ReleaseId,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> TargetNames,
    IReadOnlyList<HostUpdateBackupManifestFile> Files);

/// <summary>
/// Coordinates a single consistency-point backup across every registered target, fails closed
/// on any externally owned target, and writes a checksummed manifest before allowing migration.
/// </summary>
public sealed class HostUpdateBackupCoordinator(
    IReadOnlyList<IHostUpdateBackupTarget> targets,
    string backupRootDirectory) : IHostUpdateBackupCoordinator
{
    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<string> externallyOwned = [.. targets.Where(t => t.IsExternallyOwned).Select(t => t.Name)];
        if (externallyOwned.Count > 0)
        {
            throw new HostUpdateBackupUnsupportedOwnerException(externallyOwned);
        }

        string runDirectory = Path.Combine(
            backupRootDirectory,
            SanitizeForPath(request.ReleaseId),
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        string releaseDirectory = Path.GetDirectoryName(runDirectory)!;
        Directory.CreateDirectory(runDirectory);
        HostUpdateDurableFile.FlushDirectory(backupRootDirectory);
        HostUpdateDurableFile.FlushDirectory(releaseDirectory);
        HostUpdateDurableFile.FlushDirectory(runDirectory);

        foreach (IHostUpdateBackupTarget target in targets)
        {
            string targetDirectory = Path.Combine(runDirectory, SanitizeForPath(target.Name));
            Directory.CreateDirectory(targetDirectory);
            await target.BackupAsync(targetDirectory, cancellationToken).ConfigureAwait(false);
            if (!Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            {
                throw new HostUpdateBackupIncompleteException(target.Name);
            }
        }

        var files = new List<HostUpdateBackupManifestFile>();
        foreach (string filePath in Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            files.Add(new HostUpdateBackupManifestFile(Path.GetRelativePath(runDirectory, filePath), hash, bytes.LongLength));
        }

        if (files.Count == 0)
        {
            throw new HostUpdateBackupIncompleteException("manifest");
        }

        var manifest = new HostUpdateBackupManifest(
            request.ReleaseId,
            DateTimeOffset.UtcNow,
            [.. targets.Select(t => t.Name)],
            [.. files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)]);
        string manifestPath = Path.Combine(runDirectory, "manifest.json");
        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        HostUpdateDurableFile.WriteAllTextAtomic(manifestPath, manifestJson);
        HostUpdateDurableFile.FlushDirectory(runDirectory);
        HostUpdateDurableFile.FlushDirectory(releaseDirectory);
        HostUpdateDurableFile.FlushDirectory(backupRootDirectory);
    }

    private static string SanitizeForPath(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}

/// <summary>Runs the backup step of the host update executor.</summary>
public interface IHostUpdateBackupCoordinator
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Backs up one provider-native database context by invoking the host''s existing
/// provider-specific dump tooling (never a duplicate ad-hoc implementation) via an explicit
/// argument list, never shell string interpolation.
/// </summary>
public sealed class ProcessDatabaseBackupTarget(
    string name,
    bool isExternallyOwned,
    IHostUpdateProcessRunner processRunner,
    string fileName,
    Func<string, IReadOnlyList<string>> buildArguments,
    TimeSpan timeout,
    IReadOnlyDictionary<string, string>? environment = null) : IHostUpdateBackupTarget
{
    public string Name { get; } = name;

    public bool IsExternallyOwned { get; } = isExternallyOwned;

    public async Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        HostUpdateProcessResult result = await processRunner.RunAsync(
            fileName,
            buildArguments(destinationDirectory),
            timeout,
            cancellationToken,
            environment).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new HostUpdateBackupIncompleteException(Name);
        }
    }
}

/// <summary>Backs up an application-owned directory (blobs/profiles/calibration/config/keyrings) via a recursive copy.</summary>
public sealed class DirectoryCopyBackupTarget(string name, string sourceDirectory, bool isRequired = true) : IHostUpdateBackupTarget
{
    public string Name { get; } = name;

    public bool IsExternallyOwned => false;

    public async Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            if (isRequired)
            {
                // Every currently-configured owned directory is a real, always-mounted container
                // path (see the doc comment on HostUpdateExecutionOptions.OwnedDirectories); a
                // missing mount here means the deployment is misconfigured, not that there is
                // legitimately nothing to back up. Writing a ".empty" sentinel and reporting
                // success would silently drop this directory's data from every future restore, so
                // a required target fails the backup closed instead.
                throw new HostUpdateBackupIncompleteException(Name);
            }

            // Only a directory explicitly declared optional (e.g. certificates/keyrings not
            // configured in this deployment) may legitimately be absent. The coordinator's
            // non-empty-file check still applies to catch a target that produces nothing when it
            // was expected to.
            HostUpdateDurableFile.WriteAllTextAtomic(Path.Combine(destinationDirectory, ".empty"), "source_not_present");
            return;
        }

        string[] relativeDirectories =
        [
            ".",
            .. Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sourceDirectory, path))
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
        string[] sourceFiles = [.. Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)];
        if (relativeDirectories.Length > 1 || sourceFiles.Length == 0)
        {
            string directoryManifest = JsonSerializer.Serialize(relativeDirectories, new JsonSerializerOptions { WriteIndented = true });
            HostUpdateDurableFile.WriteAllTextAtomic(
                Path.Combine(destinationDirectory, ".printfarmer-directories.json"),
                directoryManifest);
        }

        foreach (string sourcePath in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
            await HostUpdateDurableFile.CopyFileDurablyAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
    }
}
