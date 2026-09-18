namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Outcome of an attempted recovery after the executor left the release in <see cref="HostUpdateExecutionState.RecoveryRequired"/>.</summary>
public enum HostUpdateRecoveryOutcome
{
    /// <summary>The host was restored to a verified prior working state.</summary>
    RolledBack,

    /// <summary>No safe rollback/restore path exists; an operator must resolve this manually.</summary>
    NeedsOperator,
}

/// <summary>Durable, immutable record of one recovery attempt.</summary>
public sealed record HostUpdateRecoveryResult(HostUpdateRecoveryOutcome Outcome, string Detail);

/// <summary>Durable record of one recovery attempt, persisted independently of the caller so a
/// <see cref="HostUpdateRecoveryOutcome.NeedsOperator"/> outcome survives a process crash even if
/// whatever invoked <see cref="IHostUpdateRecoveryCoordinator.RecoverAsync"/> never got to persist
/// it itself (Kane audit P1.7).</summary>
public sealed record HostUpdateRecoveryOutcomeRecord(
    string ReleaseId,
    HostUpdateRecoveryOutcome Outcome,
    string Detail,
    DateTimeOffset RecordedAt);

/// <summary>Durably persists (and retrieves) the most recent recovery outcome for a release.</summary>
public interface IHostUpdateRecoveryOutcomeStore
{
    Task<HostUpdateRecoveryOutcomeRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken);

    Task WriteAsync(HostUpdateRecoveryOutcomeRecord record, CancellationToken cancellationToken);
}

/// <summary>File-backed <see cref="IHostUpdateRecoveryOutcomeStore"/> using an atomic write-then-rename,
/// one JSON file per release under <paramref name="rootDirectory"/> so recovery outcomes for distinct
/// releases never overwrite each other and a restart can discover the last outcome for any release.</summary>
public sealed class FileHostUpdateRecoveryOutcomeStore(string rootDirectory) : IHostUpdateRecoveryOutcomeStore
{
    public async Task<HostUpdateRecoveryOutcomeRecord?> ReadAsync(string releaseId, CancellationToken cancellationToken)
    {
        string path = PathFor(releaseId);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<HostUpdateRecoveryOutcomeRecord>(json);
    }

    public async Task WriteAsync(HostUpdateRecoveryOutcomeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(rootDirectory);
        string path = PathFor(record.ReleaseId);
        string json = System.Text.Json.JsonSerializer.Serialize(record);
        await Task.Run(() => HostUpdateDurableFile.WriteAllTextAtomic(path, json), cancellationToken).ConfigureAwait(false);
    }

    private string PathFor(string releaseId)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeReleaseId = new([.. releaseId.Select(c => invalid.Contains(c) ? '_' : c)]);
        return Path.Combine(rootDirectory, $"{safeReleaseId}.recovery.json");
    }
}

/// <summary>
/// Decides whether a failed update can be recovered by re-applying the prior pinned images
/// alone (image-only rollback), which is only safe when the prior release''s schema/storage
/// contract is still compatible with what is currently on disk (i.e. no migration successfully
/// committed past the prior release during the failed attempt).
/// </summary>
public interface IHostUpdateRecoveryCompatibilityEvaluator
{
    bool SupportsImageOnlyRollback(InstalledHostState priorState, IReadOnlyList<HostUpdateExecutionActivity> activities);
}

/// <summary>
/// Image-only rollback is safe only when the failed attempt''s own journal shows the migration
/// step never completed (no <c>migration:after</c> activity was recorded) -- i.e. the schema
/// on disk is still the one the prior release expects. Any recorded migration completion means
/// the schema may have moved forward and only a coordinated restore (or fix-forward) is safe.
/// </summary>
public sealed class DefaultHostUpdateRecoveryCompatibilityEvaluator : IHostUpdateRecoveryCompatibilityEvaluator
{
    public bool SupportsImageOnlyRollback(InstalledHostState priorState, IReadOnlyList<HostUpdateExecutionActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        ArgumentNullException.ThrowIfNull(activities);
        bool migrationStarted = activities.Any(a =>
            a.State == HostUpdateExecutionState.Migrating && a.Phase.StartsWith("migration:before", StringComparison.Ordinal));
        bool migrationCommitted = activities.Any(a =>
            a.State == HostUpdateExecutionState.Migrating && a.Phase.EndsWith(":after", StringComparison.Ordinal));
        return !migrationStarted && !migrationCommitted;
    }
}

/// <summary>Restores both provider-native database contexts plus application-owned blobs/config/keyrings from a specific completed backup.</summary>
public interface IHostUpdateRestoreExecutor
{
    Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken);
}

/// <summary>Locates the most recent completed, checksum-verified backup manifest for a release.</summary>
public interface IHostUpdateBackupManifestLocator
{
    Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken);
}

/// <summary>
/// Recovers a release left in <see cref="HostUpdateExecutionState.RecoveryRequired"/>: prefers a
/// verified image-only rollback when schema-compatible, otherwise performs a coordinated
/// restore of both databases and owned storage/config/key material from the matching backup.
/// Never uses EF down-migrations and never guesses; any uncertainty is persisted as
/// <see cref="HostUpdateRecoveryOutcome.NeedsOperator"/> rather than reported as recovered.
/// </summary>
public sealed class HostUpdateRecoveryCoordinator(
    IInstalledHostStateStore installedStateStore,
    IHostUpdateRecoveryCompatibilityEvaluator compatibilityEvaluator,
    IHostUpdateDigestApplier digestApplier,
    IHostUpdateRestoreExecutor restoreExecutor,
    IHostUpdateBackupManifestLocator manifestLocator,
    IHostUpdateDigestVerifier digestVerifier,
    IHostUpdateRecoveryOutcomeStore outcomeStore,
    IHostUpdateFenceCoordinator? fenceCoordinator = null,
    IHostUpdateExecutionLock? executionLock = null) : IHostUpdateRecoveryCoordinator
{
    public async Task<HostUpdateRecoveryResult> RecoverAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedRequest);
        ArgumentNullException.ThrowIfNull(activities);

        if (!HostUpdateRequestFingerprint.Matches(activities, failedRequest, out string fingerprintError))
        {
            var fingerprintResult = new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, fingerprintError);
            await outcomeStore.WriteAsync(
                new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, fingerprintResult.Outcome, fingerprintResult.Detail, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            return fingerprintResult;
        }

        using IHostUpdateExecutionLease lease = (executionLock ?? NoopHostUpdateExecutionLock.Instance).Acquire(TimeSpan.FromSeconds(30), cancellationToken);

        HostUpdateRecoveryOutcomeRecord? existingOutcome = await outcomeStore.ReadAsync(failedRequest.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (existingOutcome is { Outcome: HostUpdateRecoveryOutcome.RolledBack })
        {
            return new HostUpdateRecoveryResult(existingOutcome.Outcome, existingOutcome.Detail);
        }

        HostUpdateRecoveryResult result;
        try
        {
            result = await RecoverCoreAsync(failedRequest, activities, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Recovery itself was cancelled/killed mid-flight: this is exactly the uncertain case
            // NeedsOperator exists for, so it must be durably recorded (not silently dropped)
            // before the cancellation propagates to the caller.
            await outcomeStore.WriteAsync(
                new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, HostUpdateRecoveryOutcome.NeedsOperator, "recovery_canceled", DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Persisted unconditionally on every completed outcome -- including NeedsOperator --
        // so it survives a process crash/kill even if whatever invoked RecoverAsync never gets a
        // chance to persist it itself (Kane audit P1.7). Uses CancellationToken.None for the
        // persistence write itself: recording the outcome must not be skipped just because the
        // caller's token happens to already be in a cancellation-requested state.
        await outcomeStore.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, result.Outcome, result.Detail, DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        if (result.Outcome == HostUpdateRecoveryOutcome.RolledBack && fenceCoordinator is not null)
        {
            try
            {
                await fenceCoordinator.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "fence_release_failed:" + exception.GetType().Name);
                await outcomeStore.WriteAsync(
                    new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, result.Outcome, result.Detail, DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async Task<HostUpdateRecoveryResult> RecoverCoreAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        InstalledHostState? priorState = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (priorState is null && ApplyMayHaveStarted(activities))
            {
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "prior_image_state_missing_after_apply_started");
            }

            if (priorState is not null && compatibilityEvaluator.SupportsImageOnlyRollback(priorState, activities))
            {
                await digestApplier.ApplyByDigestsAsync(priorState.ServiceDigests, cancellationToken, priorState.ServicePlatforms).ConfigureAwait(false);
                await digestVerifier.VerifyDigestsAsync(priorState.ServiceDigests, cancellationToken).ConfigureAwait(false);
                await installedStateStore.WriteAsync(priorState with { RecordedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.RolledBack, "image_only_rollback");
            }

            (HostUpdateBackupManifest Manifest, string RunDirectory)? located =
                await manifestLocator.FindLatestAsync(failedRequest.ReleaseId, cancellationToken).ConfigureAwait(false);
            if (located is null)
            {
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, "no_backup_available");
            }

            await restoreExecutor.RestoreAsync(located.Value.Manifest, located.Value.RunDirectory, cancellationToken).ConfigureAwait(false);

            if (priorState is not null)
            {
                await digestApplier.ApplyByDigestsAsync(priorState.ServiceDigests, cancellationToken, priorState.ServicePlatforms).ConfigureAwait(false);
                await digestVerifier.VerifyDigestsAsync(priorState.ServiceDigests, cancellationToken).ConfigureAwait(false);
                await installedStateStore.WriteAsync(priorState with { RecordedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            }

            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.RolledBack, "coordinated_restore");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed: any exception during recovery itself is an uncertain outcome, never
            // reported as a successful rollback.
            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, exception.GetType().Name);
        }
    }

    private static bool ApplyMayHaveStarted(IReadOnlyList<HostUpdateExecutionActivity> activities) =>
        activities.Any(a => a.State == HostUpdateExecutionState.Applying && a.Phase.StartsWith("apply:before", StringComparison.Ordinal));
}

/// <summary>Attempts recovery for a release left in <see cref="HostUpdateExecutionState.RecoveryRequired"/>.</summary>
public interface IHostUpdateRecoveryCoordinator
{
    Task<HostUpdateRecoveryResult> RecoverAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken);
}

/// <summary>
/// Restores database contexts and owned storage from a backup manifest by invoking the
/// existing provider-specific restore tooling (e.g. <c>pg_restore</c>/<c>sqlcmd RESTORE
/// DATABASE</c>/<c>sqlite3 .restore</c>) via an explicit argument list, and by copying owned
/// directories back from the backup run. Verified per-file against the manifest''s recorded
/// checksums before considering the restore trustworthy.
/// </summary>
public sealed class ProcessHostUpdateRestoreExecutor(
    IHostUpdateProcessRunner processRunner,
    IReadOnlyDictionary<string, Func<string, HostUpdateRestoreCommand>> restoreCommandsByTarget,
    IReadOnlyDictionary<string, string> directoryRestoreTargetsByName,
    TimeSpan timeout) : IHostUpdateRestoreExecutor
{
    public async Task RestoreAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await VerifyChecksumsAsync(manifest, backupRunDirectory, cancellationToken).ConfigureAwait(false);

        foreach (string targetName in manifest.TargetNames)
        {
            string targetDirectory = Path.Combine(backupRunDirectory, SanitizeForPath(targetName));
            if (restoreCommandsByTarget.TryGetValue(targetName, out Func<string, HostUpdateRestoreCommand>? buildCommand))
            {
                HostUpdateRestoreCommand command = buildCommand(targetDirectory);
                HostUpdateProcessResult result = await processRunner.RunAsync(
                    command.FileName,
                    command.Arguments,
                    timeout,
                    cancellationToken,
                    command.Environment).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"restore_failed:{targetName}");
                }

                continue;
            }

            if (directoryRestoreTargetsByName.TryGetValue(targetName, out string? destinationDirectory))
            {
                if (File.Exists(Path.Combine(targetDirectory, ".empty")))
                {
                    if (Directory.Exists(destinationDirectory))
                    {
                        Directory.Delete(destinationDirectory, recursive: true);
                    }

                    continue;
                }

                if (Directory.Exists(destinationDirectory))
                {
                    Directory.Delete(destinationDirectory, recursive: true);
                }

                Directory.CreateDirectory(destinationDirectory);
                RestoreRecordedDirectories(targetDirectory, destinationDirectory);
                foreach (string sourcePath in Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(targetDirectory, sourcePath);
                    if (string.Equals(relative, ".printfarmer-directories.json", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(destinationDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
            }
        }
    }

    private static void RestoreRecordedDirectories(string targetDirectory, string destinationDirectory)
    {
        string manifestPath = Path.Combine(targetDirectory, ".printfarmer-directories.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        string[]? directories = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath));
        foreach (string relative in directories ?? [])
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }
    }

    private static async Task VerifyChecksumsAsync(HostUpdateBackupManifest manifest, string backupRunDirectory, CancellationToken cancellationToken)
    {
        foreach (HostUpdateBackupManifestFile file in manifest.Files)
        {
            string path = Path.Combine(backupRunDirectory, file.RelativePath);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"restore_source_missing:{file.RelativePath}");
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal) || bytes.LongLength != file.Length)
            {
                throw new InvalidOperationException($"restore_checksum_mismatch:{file.RelativePath}");
            }
        }
    }

    private static string SanitizeForPath(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}

/// <summary>Finds the most recent backup run directory (and its manifest) for a release under the backup root.</summary>
public sealed class FileHostUpdateBackupManifestLocator(string backupRootDirectory) : IHostUpdateBackupManifestLocator
{
    public async Task<(HostUpdateBackupManifest Manifest, string RunDirectory)?> FindLatestAsync(string releaseId, CancellationToken cancellationToken)
    {
        string releaseDirectory = Path.Combine(backupRootDirectory, SanitizeForPath(releaseId));
        if (!Directory.Exists(releaseDirectory))
        {
            return null;
        }

        string? latestRun = Directory.GetDirectories(releaseDirectory)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
        if (latestRun is null)
        {
            return null;
        }

        string manifestJson = await File.ReadAllTextAsync(Path.Combine(latestRun, "manifest.json"), cancellationToken).ConfigureAwait(false);
        HostUpdateBackupManifest? manifest = System.Text.Json.JsonSerializer.Deserialize<HostUpdateBackupManifest>(manifestJson);
        return manifest is null ? null : (manifest, latestRun);
    }

    private static string SanitizeForPath(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}

internal sealed class NoopHostUpdateExecutionLock : IHostUpdateExecutionLock
{
    public static readonly NoopHostUpdateExecutionLock Instance = new();

    private NoopHostUpdateExecutionLock()
    {
    }

    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken) => NoopHostUpdateExecutionLease.LeaseInstance;

    private sealed class NoopHostUpdateExecutionLease : IHostUpdateExecutionLease
    {
        public static readonly NoopHostUpdateExecutionLease LeaseInstance = new();

        private NoopHostUpdateExecutionLease()
        {
        }

        public void Dispose()
        {
        }
    }
}
