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
/// Expected service impact. <c>MaxExpectedSeconds</c> is an upper bound derived from the configured
/// engine timeouts, not a measurement; <c>null</c> means no automatic path exists.
/// </summary>
internal sealed record HostUpdateDowntimePreview(
    string Impact,
    string[] AffectedServices,
    string[] RestoredTargets,
    int? MaxExpectedSeconds,
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
    public const string UpperBoundBasis = "upper_bound_from_configured_timeouts";

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
        int applyAndVerify = options.ApplyTimeoutSeconds + options.VerifyTimeoutSeconds;
        return plan.Kind switch
        {
            HostUpdateRecoveryPlanKind.ImageOnlyRollback =>
                new("service_restart", priorServices, [], applyAndVerify, UpperBoundBasis),
            HostUpdateRecoveryPlanKind.CoordinatedRestore =>
                new(
                    "restore_and_service_restart",
                    priorServices,
                    backup is null ? [] : [.. backup.TargetNames.Order(StringComparer.Ordinal)],
                    options.BackupTimeoutSeconds + (installed is null ? 0 : applyAndVerify),
                    UpperBoundBasis),
            HostUpdateRecoveryPlanKind.FenceReleaseOnly or HostUpdateRecoveryPlanKind.AlreadyRolledBack =>
                new("none", [], [], 0, UpperBoundBasis),
            _ => new("operator_required", [], [], null, UpperBoundBasis),
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
        new([.. report.Items], report.ConfigurationFingerprint, report.HasDrift, report.ReapprovalToken);

    private static bool IsPresentWithLength(string runDirectory, HostUpdateBackupManifestFile file)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDirectory));
            string path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
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
