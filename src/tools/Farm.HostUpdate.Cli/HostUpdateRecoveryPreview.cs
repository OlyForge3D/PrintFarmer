using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.HostUpdate.Cli;

internal sealed record HostUpdatePriorIdentity(
    string ReleaseId,
    string ManifestDigest,
    string Topology,
    DateTimeOffset RecordedAt,
    IReadOnlyDictionary<string, string> ServiceDigests,
    IReadOnlyDictionary<string, string>? ServicePlatforms);

internal sealed record HostUpdateTargetImage(string ServiceId, string Platform, string ChildDigest);

internal sealed record HostUpdateTargetIdentity(
    string ReleaseId,
    string RequestId,
    string ManifestDigest,
    string SourceCommit,
    long AuthenticatedSequence,
    string Channel,
    string HostPlatform,
    string TrustRoot,
    HostUpdateAuthorizationKind AuthorizationKind,
    long PolicyRevision,
    string PolicyFingerprint,
    HostUpdateTargetImage[] Targets);

internal sealed record HostUpdateRecoveryIdentity(HostUpdatePriorIdentity? Prior, HostUpdateTargetIdentity Target, string? CurrentPlatform);

/// <summary>
/// Expected service impact. <c>TimeoutBudgetSeconds</c> is the sum of the configured engine
/// timeouts that bound the recovery's external processes and health polling. It is an estimate,
/// not an upper bound: the steps in <c>UnboundedSteps</c> have no configured timeout. <c>null</c>
/// means no automatic path exists.
/// </summary>
internal sealed record HostUpdateDowntimePreview(
    string Impact,
    string[] AffectedServices,
    string[] RestoredTargets,
    int? TimeoutBudgetSeconds,
    string[] UnboundedSteps,
    string Basis);

internal sealed record HostUpdateBackupEvidence(
    bool Found,
    string? RunDirectory,
    DateTimeOffset? CompletedAt,
    bool ReleaseMatches,
    string[] TargetNames,
    int FileCount,
    long TotalBytes,
    int FilesPresentWithRecordedLength);

internal sealed record HostUpdateRecoveryEvidence(
    HostUpdateRecoveryOutcome? ExistingOutcome,
    string? ExistingOutcomeDetail,
    DateTimeOffset? ExistingOutcomeRecordedAt,
    string[] UncertainPhases,
    bool MigrationStarted,
    bool ApplyStarted);

internal sealed record HostUpdateWriterFencePreview(bool AdmissionClosed, string[] FencedWriters, string AfterRecovery);

internal sealed record HostUpdateDriftPreview(
    HostUpdateDriftItem[] Items,
    string ConfigurationFingerprint,
    bool ReapprovalRequired,
    string? ReapprovalToken);

/// <summary>Builds the side-effect-free <c>recover --preview</c> details (issue #2998).</summary>
internal static class HostUpdateRecoveryPreview
{
    public const string TimeoutBudgetBasis = "sum_of_configured_timeouts_not_an_upper_bound";
    public const string HealthCheckFinalPass = "health_check_final_pass";
    public const string BackupChecksumVerification = "backup_checksum_verification";
    public const string OwnedDirectoryCopy = "owned_directory_copy";

    public static HostUpdateRecoveryIdentity Identity(HostUpdateExecutionRequest request, InstalledHostState? installed, string? currentPlatform) =>
        new(
            installed is null
                ? null
                : new HostUpdatePriorIdentity(installed.ReleaseId, installed.ManifestDigest, installed.Topology, installed.RecordedAt, installed.ServiceDigests, installed.ServicePlatforms),
            new HostUpdateTargetIdentity(
                request.ReleaseId,
                request.RequestId,
                request.ManifestDigest,
                request.SourceCommit,
                request.AuthenticatedSequence,
                HostUpdateRecoveryDrift.ChannelName(request.Channel),
                request.HostPlatform,
                request.TrustRoot,
                request.AuthorizationKind,
                request.PolicyRevision,
                request.PolicyFingerprint,
                [.. request.Targets.OrderBy(t => t.ServiceId, StringComparer.Ordinal).Select(t => new HostUpdateTargetImage(t.ServiceId, t.Platform, t.ChildDigest))]),
            currentPlatform);

    public static HostUpdateDowntimePreview Downtime(
        HostUpdateRecoveryPlan plan,
        InstalledHostState? installed,
        HostUpdateBackupManifest? backup,
        HostUpdateExecutionOptions options)
    {
        string[] priorServices = installed is null ? [] : [.. installed.ServiceDigests.Keys.Order(StringComparer.Ordinal)];

        // Apply runs one bounded "docker image pull" per service plus one "compose up"; verify
        // polls until its deadline, then may finish one more pass plus a poll interval.
        int applyAndVerify = ((priorServices.Length + 1) * options.ApplyTimeoutSeconds)
            + options.VerifyTimeoutSeconds + options.VerifyPollIntervalSeconds;
        string[] restored = backup is null ? [] : [.. (backup.TargetNames ?? []).Order(StringComparer.Ordinal)];

        // Each non-directory target is one restore process bounded by the backup timeout; owned
        // directories are copied back without a timeout.
        int restoreProcesses = restored.Count(name => !options.OwnedDirectories.ContainsKey(name));
        List<string> restoreUnbounded = [BackupChecksumVerification];
        if (restored.Any(options.OwnedDirectories.ContainsKey))
        {
            restoreUnbounded.Add(OwnedDirectoryCopy);
        }

        if (installed is not null)
        {
            restoreUnbounded.Add(HealthCheckFinalPass);
        }

        return plan.Kind switch
        {
            HostUpdateRecoveryPlanKind.ImageOnlyRollback =>
                new("service_restart", priorServices, [], applyAndVerify, [HealthCheckFinalPass], TimeoutBudgetBasis),
            HostUpdateRecoveryPlanKind.CoordinatedRestore =>
                new(
                    "restore_and_service_restart",
                    priorServices,
                    restored,
                    (restoreProcesses * options.BackupTimeoutSeconds) + (installed is null ? 0 : applyAndVerify),
                    [.. restoreUnbounded],
                    TimeoutBudgetBasis),
            HostUpdateRecoveryPlanKind.FenceReleaseOnly or HostUpdateRecoveryPlanKind.AlreadyRolledBack =>
                new("none", [], [], 0, [], TimeoutBudgetBasis),
            _ => new("operator_required", [], [], null, [], TimeoutBudgetBasis),
        };
    }

    public static HostUpdateBackupEvidence Backup(string releaseId, (HostUpdateBackupManifest Manifest, string RunDirectory)? located)
    {
        if (located is null)
        {
            return new(false, null, null, false, [], 0, 0, 0);
        }

        (HostUpdateBackupManifest manifest, string runDirectory) = located.Value;
        IReadOnlyList<HostUpdateBackupManifestFile> files = manifest.Files ?? [];
        return new(
            true,
            runDirectory,
            manifest.CompletedAt,
            string.Equals(manifest.ReleaseId, releaseId, StringComparison.Ordinal),
            [.. (manifest.TargetNames ?? []).Order(StringComparer.Ordinal)],
            files.Count,
            files.Sum(f => f.Length),
            files.Count(f => IsPresentWithLength(runDirectory, f)));
    }

    public static HostUpdateRecoveryEvidence Recovery(
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        HostUpdateRecoveryOutcomeRecord? outcome) =>
        new(
            outcome?.Outcome,
            outcome?.Detail,
            outcome?.RecordedAt,
            HostUpdateCli.FindUncertainPhases(activities),
            activities.Any(a => a.State == HostUpdateExecutionState.Migrating && a.Phase.StartsWith("migration:before", StringComparison.Ordinal)),
            activities.Any(a => a.State == HostUpdateExecutionState.Applying && a.Phase.StartsWith("apply:before", StringComparison.Ordinal)));

    public static HostUpdateWriterFencePreview WriterFence(HostUpdateRecoveryPlan plan, bool admissionClosed, IEnumerable<string> writerNames)
    {
        string afterRecovery = plan.Kind switch
        {
            HostUpdateRecoveryPlanKind.ImageOnlyRollback or HostUpdateRecoveryPlanKind.CoordinatedRestore or HostUpdateRecoveryPlanKind.FenceReleaseOnly
                => "writers_fenced_until_rollback_then_released",
            HostUpdateRecoveryPlanKind.AlreadyRolledBack => "unchanged",
            _ => admissionClosed ? "remains_fenced" : "unchanged",
        };
        return new(admissionClosed, [.. writerNames.Order(StringComparer.Ordinal)], afterRecovery);
    }

    public static HostUpdateDriftPreview Drift(HostUpdateDriftReport report) =>
        new([.. report.Items], report.ConfigurationFingerprint, report.ReapprovalToken is not null, report.ReapprovalToken);

    private static bool IsPresentWithLength(string runDirectory, HostUpdateBackupManifestFile file)
    {
        try
        {
            if (string.IsNullOrEmpty(file.RelativePath) || Path.IsPathRooted(file.RelativePath))
            {
                return false;
            }

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDirectory));
            string path = Path.GetFullPath(Path.Join(root, file.RelativePath));
            string relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            var info = new FileInfo(path);
            return info.Exists && info.Length == file.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
