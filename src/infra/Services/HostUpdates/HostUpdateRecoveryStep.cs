namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Outcome of an attempted recovery after the executor left the release in <see cref="HostUpdateExecutionState.RecoveryRequired"/>.</summary>
/// <remarks>
/// Values are pinned because they are persisted as numbers in the durable outcome store; inserting
/// a member in the middle would silently reinterpret already-written records.
/// </remarks>
public enum HostUpdateRecoveryOutcome
{
    /// <summary>The host was restored to a verified prior working state and admission was reopened.</summary>
    RolledBack = 0,

    /// <summary>No safe rollback/restore path exists; an operator must resolve this manually.</summary>
    NeedsOperator = 1,

    /// <summary>
    /// The rollback itself completed durably, but the admission fence still needs its idempotent
    /// release. This is deliberately not terminal: restart reconciliation keeps writers fenced and
    /// a retry re-drives the release alone, never restore/apply.
    /// </summary>
    FenceReleasePending = 2,
}

/// <summary>Durable, immutable record of one recovery attempt.</summary>
public sealed record HostUpdateRecoveryResult(HostUpdateRecoveryOutcome Outcome, string Detail);

/// <summary>The recovery path <see cref="IHostUpdateRecoveryCoordinator.RecoverAsync"/> would take.</summary>
public enum HostUpdateRecoveryPlanKind
{
    /// <summary>A terminal <see cref="HostUpdateRecoveryOutcome.RolledBack"/> is already recorded; recovery is a no-op.</summary>
    AlreadyRolledBack,

    /// <summary>The rollback is durable; only the idempotent admission-fence release remains.</summary>
    FenceReleaseOnly,

    /// <summary>Re-apply and verify the prior pinned images; no schema change was committed.</summary>
    ImageOnlyRollback,

    /// <summary>Restore both database contexts and owned storage from the verified backup, then re-apply the prior images.</summary>
    CoordinatedRestore,

    /// <summary>No supported automatic path; an operator must resolve it.</summary>
    NeedsOperator,
}

/// <summary>Side-effect-free recovery preview.</summary>
public sealed record HostUpdateRecoveryPlan(
    HostUpdateRecoveryPlanKind Kind,
    string Detail,
    IReadOnlyDictionary<string, string>? PriorServiceDigests,
    string? BackupRunDirectory);

/// <summary>Previews recovery using the coordinator's own decision logic without side effects.</summary>
public interface IHostUpdateRecoveryPlanner
{
    Task<HostUpdateRecoveryPlan> PlanAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken);
}

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
    IHostUpdateExecutionLock? executionLock = null,
    IHostUpdatePhysicalReconciliationGate? physicalReconciliationGate = null,
    HostUpdateExecutionOptions? executionOptions = null) : IHostUpdateRecoveryCoordinator, IHostUpdateRecoveryPlanner
{
    private const string FenceReleaseFailureSeparator = "|";

    /// <summary>The recorded prior services differ from the configured split/monolith topology.</summary>
    public const string PriorStateTopologyMismatch = "prior_state_topology_mismatch";

    /// <summary>The backup includes the database but this host does not own it, so it is never restored here.</summary>
    public const string DatabaseExternallyOwnedStop = "database_externally_owned";

    private const string HostUpdateDatabaseTargetName = "database";

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

        // Only a plain RolledBack record is fully terminal: it is written exclusively after the
        // admission fence has actually been released, so there is nothing left to re-drive.
        if (existingOutcome is { Outcome: HostUpdateRecoveryOutcome.RolledBack })
        {
            return new HostUpdateRecoveryResult(existingOutcome.Outcome, existingOutcome.Detail);
        }

        // The rollback itself already completed durably on an earlier attempt and only the
        // idempotent fence release remains. Re-entering RecoverCoreAsync here would re-run
        // restore/digest-apply against an already-restored host, so the retry is narrowed to the
        // release alone.
        if (existingOutcome is { Outcome: HostUpdateRecoveryOutcome.FenceReleasePending })
        {
            return await ReleaseFenceAfterRolledBackAsync(failedRequest, existingOutcome.Detail).ConfigureAwait(false);
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

        if (result.Outcome == HostUpdateRecoveryOutcome.RolledBack)
        {
            // Bishop/Hicks review (issue #2663): the FIRST durable write after a successful rollback
            // must record rollback-complete AND fence-release-pending together. Writing a plain
            // terminal RolledBack first would permanently strand admission.closed if the process
            // died in the window before ReleaseAsync ran, because both the re-entry branch above and
            // the restart availability reconciliation treat a plain RolledBack record as fully
            // resolved and would therefore never re-drive the release.
            await outcomeStore.WriteAsync(
                new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, HostUpdateRecoveryOutcome.FenceReleasePending, result.Detail, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            return await ReleaseFenceAfterRolledBackAsync(failedRequest, result.Detail).ConfigureAwait(false);
        }

        // Persisted unconditionally on every other completed outcome -- including NeedsOperator --
        // so it survives a process crash/kill even if whatever invoked RecoverAsync never gets a
        // chance to persist it itself (Kane audit P1.7). Uses CancellationToken.None for the
        // persistence write itself: recording the outcome must not be skipped just because the
        // caller's token happens to already be in a cancellation-requested state.
        await outcomeStore.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord(failedRequest.ReleaseId, result.Outcome, result.Detail, DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Drives the idempotent admission-fence release that must follow a completed rollback. Never
    /// performs (or re-performs) restore/apply work: by the time this runs the host payload is
    /// already durably restored, and the only remaining obligations are the physical printer
    /// command reconciliation gate (issue #2999) and reopening admission.
    /// </summary>
    private async Task<HostUpdateRecoveryResult> ReleaseFenceAfterRolledBackAsync(HostUpdateExecutionRequest failedRequest, string pendingDetail)
    {
        string releaseId = failedRequest.ReleaseId;

        // Retries carry any previous failure diagnostic in the detail suffix; the rollback detail
        // itself is everything before it, so repeated failures cannot accumulate suffixes.
        int separator = pendingDetail.IndexOf(FenceReleaseFailureSeparator, StringComparison.Ordinal);
        string rollbackDetail = separator < 0 ? pendingDetail : pendingDetail[..separator];

        // Writers stay fenced until an operator has recorded that every affected printer was
        // physically reconciled. Nothing here replays, cancels or issues a printer command; the
        // durable state stays fence-release-pending, so a retry re-checks this gate alone.
        string? reconciliationBlock = await PhysicalReconciliationBlockAsync(failedRequest).ConfigureAwait(false);
        if (reconciliationBlock is not null)
        {
            string blockedDetail = rollbackDetail + FenceReleaseFailureSeparator + reconciliationBlock;
            await outcomeStore.WriteAsync(
                new HostUpdateRecoveryOutcomeRecord(releaseId, HostUpdateRecoveryOutcome.FenceReleasePending, blockedDetail, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.FenceReleasePending, blockedDetail);
        }

        if (fenceCoordinator is not null)
        {
            try
            {
                await fenceCoordinator.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The rollback is still complete and the fence is still closed, so the durable state
                // stays fence-release-pending (never a generic NeedsOperator): a restart must retry
                // the idempotent release only, not RecoverCoreAsync/restore/apply. The failure is
                // carried as a diagnostic suffix for the operator.
                string failedDetail = rollbackDetail + FenceReleaseFailureSeparator + "fence_release_failed:" + exception.GetType().Name;
                await outcomeStore.WriteAsync(
                    new HostUpdateRecoveryOutcomeRecord(releaseId, HostUpdateRecoveryOutcome.FenceReleasePending, failedDetail, DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
                return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.FenceReleasePending, failedDetail);
            }
        }

        // Admission is open again (or was never fenced by this coordinator), so the release is now
        // genuinely terminal and the plain RolledBack record may finally be written.
        var released = new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.RolledBack, rollbackDetail);
        await outcomeStore.WriteAsync(
            new HostUpdateRecoveryOutcomeRecord(releaseId, released.Outcome, released.Detail, DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);
        return released;
    }

    private async Task<string?> PhysicalReconciliationBlockAsync(HostUpdateExecutionRequest failedRequest)
    {
        if (physicalReconciliationGate is null)
        {
            return null;
        }

        try
        {
            return await physicalReconciliationGate.IsRecordedAsync(failedRequest.ReleaseId, failedRequest.RequestId, CancellationToken.None).ConfigureAwait(false)
                ? null
                : HostUpdatePhysicalReconciliationCodes.Pending;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreadable or tampered record is never treated as reconciliation.
            return HostUpdatePhysicalReconciliationCodes.Unreadable + ":" + exception.GetType().Name;
        }
    }

    private async Task<HostUpdateRecoveryResult> RecoverCoreAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        try
        {
            RecoveryDecision decision = await DecideAsync(failedRequest, activities, cancellationToken).ConfigureAwait(false);
            InstalledHostState? priorState = decision.PriorState;
            switch (decision.Kind)
            {
                case HostUpdateRecoveryPlanKind.ImageOnlyRollback:
                    await digestApplier.ApplyByDigestsAsync(priorState!.ServiceDigests, cancellationToken, priorState.ServicePlatforms).ConfigureAwait(false);
                    await digestVerifier.VerifyDigestsAsync(priorState.ServiceDigests, cancellationToken).ConfigureAwait(false);
                    await installedStateStore.WriteAsync(priorState with { RecordedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                    return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.RolledBack, "image_only_rollback");

                case HostUpdateRecoveryPlanKind.CoordinatedRestore:
                    await restoreExecutor.RestoreAsync(decision.Backup!.Value.Manifest, decision.Backup.Value.RunDirectory, cancellationToken).ConfigureAwait(false);
                    if (priorState is not null)
                    {
                        await digestApplier.ApplyByDigestsAsync(priorState.ServiceDigests, cancellationToken, priorState.ServicePlatforms).ConfigureAwait(false);
                        await digestVerifier.VerifyDigestsAsync(priorState.ServiceDigests, cancellationToken).ConfigureAwait(false);
                        await installedStateStore.WriteAsync(priorState with { RecordedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                    }

                    return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.RolledBack, "coordinated_restore");

                default:
                    return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, decision.Detail);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed: any exception during recovery itself is an uncertain outcome, never
            // reported as a successful rollback.
            return new HostUpdateRecoveryResult(HostUpdateRecoveryOutcome.NeedsOperator, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Read-only preview of the path <see cref="RecoverAsync"/> would take. It uses the same
    /// decision logic but never acquires the lock, writes an outcome, restores, or applies.
    /// </summary>
    public async Task<HostUpdateRecoveryPlan> PlanAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failedRequest);
        ArgumentNullException.ThrowIfNull(activities);

        if (!HostUpdateRequestFingerprint.Matches(activities, failedRequest, out string fingerprintError))
        {
            return new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.NeedsOperator, fingerprintError, null, null);
        }

        HostUpdateRecoveryOutcomeRecord? existingOutcome = await outcomeStore.ReadAsync(failedRequest.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (existingOutcome is { Outcome: HostUpdateRecoveryOutcome.RolledBack })
        {
            return new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.AlreadyRolledBack, existingOutcome.Detail, null, null);
        }

        if (existingOutcome is { Outcome: HostUpdateRecoveryOutcome.FenceReleasePending })
        {
            return new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.FenceReleaseOnly, existingOutcome.Detail, null, null);
        }

        try
        {
            RecoveryDecision decision = await DecideAsync(failedRequest, activities, cancellationToken).ConfigureAwait(false);
            return new HostUpdateRecoveryPlan(
                decision.Kind,
                decision.Detail,
                decision.PriorState?.ServiceDigests,
                decision.Backup?.RunDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.NeedsOperator, exception.GetType().Name, null, null);
        }
    }

    private async Task<RecoveryDecision> DecideAsync(
        HostUpdateExecutionRequest failedRequest,
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        InstalledHostState? priorState = await installedStateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        bool completedInstallation = activities.Any(a =>
            a.State == HostUpdateExecutionState.Completed &&
            string.Equals(a.Phase, "installed-state:after", StringComparison.Ordinal));
        if (completedInstallation)
        {
            return new(HostUpdateRecoveryPlanKind.NeedsOperator, "completed_installation_requires_fence_release", priorState, null);
        }

        if (priorState is null && ApplyMayHaveStarted(activities))
        {
            return new(HostUpdateRecoveryPlanKind.NeedsOperator, "prior_image_state_missing_after_apply_started", null, null);
        }

        // The prior state is re-applied service-for-service and then verified against the
        // configured topology. A split/monolith mismatch would only surface after images were
        // pulled and containers recreated, so it stops here before any restore or apply.
        if (priorState is not null && executionOptions is { ActiveServiceIds.Length: > 0 } &&
            !priorState.ServiceDigests.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(executionOptions.ActiveServiceIds))
        {
            return new(HostUpdateRecoveryPlanKind.NeedsOperator, PriorStateTopologyMismatch, priorState, null);
        }

        if (priorState is not null && compatibilityEvaluator.SupportsImageOnlyRollback(priorState, activities))
        {
            return new(HostUpdateRecoveryPlanKind.ImageOnlyRollback, "image_only_rollback", priorState, null);
        }

        (HostUpdateBackupManifest Manifest, string RunDirectory)? located =
            await manifestLocator.FindLatestAsync(failedRequest.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (located is not null && executionOptions is { DatabaseExternallyOwned: true } &&
            located.Value.Manifest.TargetNames.Contains(HostUpdateDatabaseTargetName, StringComparer.Ordinal))
        {
            // This host never restores a database it does not own, so preview and confirm both
            // stop here rather than advertising a restore the executor would refuse.
            return new(HostUpdateRecoveryPlanKind.NeedsOperator, DatabaseExternallyOwnedStop, priorState, null);
        }

        return located is null
            ? new(HostUpdateRecoveryPlanKind.NeedsOperator, "no_backup_available", priorState, null)
            : new(HostUpdateRecoveryPlanKind.CoordinatedRestore, "coordinated_restore", priorState, located);
    }

    private sealed record RecoveryDecision(
        HostUpdateRecoveryPlanKind Kind,
        string Detail,
        InstalledHostState? PriorState,
        (HostUpdateBackupManifest Manifest, string RunDirectory)? Backup);

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
        Dictionary<string, string> targets = ValidateTargetMappings(manifest);
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

            if (targets.TryGetValue(targetName, out string? destinationDirectory))
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
                    await HostUpdateDurableFile.CopyFileDurablyAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
                }

                HostUpdateDurableFile.FlushDirectory(destinationDirectory);
                continue;
            }

            throw new InvalidOperationException($"restore_target_unmapped:{targetName}");
        }
    }

    private Dictionary<string, string> ValidateTargetMappings(HostUpdateBackupManifest manifest)
    {
        if (manifest.TargetNames.Count != manifest.TargetNames.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("restore_target_duplicate");
        }

        Dictionary<string, string> directoryTargets = new(StringComparer.Ordinal);
        foreach (string targetName in manifest.TargetNames)
        {
            bool hasCommand = restoreCommandsByTarget.ContainsKey(targetName);
            bool hasDirectory = directoryRestoreTargetsByName.TryGetValue(targetName, out string? destinationDirectory);
            if (!hasCommand && !hasDirectory)
            {
                throw new InvalidOperationException($"restore_target_unmapped:{targetName}");
            }

            if (hasCommand == hasDirectory)
            {
                throw new InvalidOperationException($"restore_target_mapping_invalid:{targetName}");
            }

            if (hasDirectory)
            {
                directoryTargets.Add(targetName, destinationDirectory!);
            }
        }

        return directoryTargets;
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
