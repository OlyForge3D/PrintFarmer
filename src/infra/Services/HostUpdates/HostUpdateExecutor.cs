#pragma warning disable SA1502
#pragma warning disable SA1516
#pragma warning disable SA1136
#pragma warning disable SA1408
#pragma warning disable SA1501
#pragma warning disable SA1513
#pragma warning disable SA1514
#pragma warning disable SA1519
#pragma warning disable SA1107
#pragma warning disable SA1503
#pragma warning disable S2681
#pragma warning disable IDISP007
#pragma warning disable SA1008
#pragma warning disable SA1009
#pragma warning disable SA1010
#pragma warning disable SA1011
#pragma warning disable SA1518
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateExecutionState { Accepted, Preflight, Draining, Fenced, BackedUp, Migrating, Applying, Verifying, Completed, RecoveryRequired }
public enum HostUpdateExecutionChannel { Stable, Insider }
public sealed record HostUpdateExecutionTarget(string ServiceId, string Platform, string ChildDigest);
public sealed record HostUpdateExecutionRequest(string ReleaseId, long AuthenticatedSequence, string ManifestDigest, string SourceCommit, HostUpdateExecutionChannel Channel, IReadOnlyList<HostUpdateExecutionTarget> Targets)
{
    public const int MinimumTargetCount = 1;
    public bool IsValid(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(ReleaseId) || AuthenticatedSequence < 1 || !Digest(ManifestDigest) || !Commit(SourceCommit) || Targets is null || Targets.Count < MinimumTargetCount)
        { error = "release_identity_invalid"; return false; }
        if (Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.ServiceId) || string.IsNullOrWhiteSpace(t.Platform) || !Digest(t.ChildDigest)))
        { error = "target_invalid"; return false; }
        if (Targets.Select(t => t.ServiceId).Distinct(StringComparer.Ordinal).Count() != Targets.Count)
        { error = "target_set_invalid"; return false; }
        return true;
    }
    private static bool Digest(string value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);
    private static bool Commit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);
}
public sealed record HostUpdateExecutionActivity(string ActivityId, string ReleaseId, HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt, string? RequestFingerprint = null);
public sealed record HostUpdateExecutionResult(string ReleaseId, HostUpdateExecutionState State, string? FailureCode, IReadOnlyList<HostUpdateExecutionActivity> Activities) { public bool Succeeded => State == HostUpdateExecutionState.Completed; }
public interface IHostUpdateExecutor { Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default); }
public interface IHostUpdateExecutionSteps
{
    Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task PersistInstalledStateAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task ReleaseFenceAsync(CancellationToken cancellationToken);
}
public interface IHostUpdateExecutionJournal { IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId); void Append(HostUpdateExecutionActivity activity); IReadOnlyList<string> ListReleaseIds(); }
public interface IHostUpdateExecutionLease : IDisposable { }
public interface IHostUpdateExecutionLock { IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken); }

/// <summary>
/// Canonical immutable request binding for one release journal. It covers every field that makes
/// a host-update request authoritative: release id, authenticated sequence, manifest digest,
/// source commit, channel, and the exact service/platform child digests. Resuming or recovering a
/// release with the same <c>ReleaseId</c> but any different identity bit is unsafe and fails closed.
/// </summary>
public static class HostUpdateRequestFingerprint
{
    private sealed record CanonicalTarget(string ServiceId, string Platform, string ChildDigest);
    private sealed record CanonicalRequest(string ReleaseId, long AuthenticatedSequence, string ManifestDigest, string SourceCommit, string Channel, IReadOnlyList<CanonicalTarget> Targets);

    public static string Compute(HostUpdateExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new CanonicalRequest(
            request.ReleaseId,
            request.AuthenticatedSequence,
            request.ManifestDigest,
            request.SourceCommit,
            request.Channel.ToString(),
            [.. request.Targets
                .OrderBy(t => t.ServiceId, StringComparer.Ordinal)
                .ThenBy(t => t.Platform, StringComparer.Ordinal)
                .Select(t => new CanonicalTarget(t.ServiceId, t.Platform, t.ChildDigest))]);
        string json = JsonSerializer.Serialize(canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static bool Matches(IReadOnlyList<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request, out string error)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(request);
        error = string.Empty;
        if (activities.Count == 0)
        {
            return true;
        }

        string expected = Compute(request);
        foreach (HostUpdateExecutionActivity activity in activities)
        {
            if (string.IsNullOrWhiteSpace(activity.RequestFingerprint))
            {
                error = "request_fingerprint_missing";
                return false;
            }

            if (!FixedTimeEquals(activity.RequestFingerprint, expected))
            {
                error = "request_fingerprint_mismatch";
                return false;
            }
        }

        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public sealed class HostUpdateExecutor(
    IHostUpdateExecutionSteps steps,
    IHostUpdateExecutionJournal journal,
    IHostUpdateExecutionLock updateLock,
    IHostUpdateSideEffectReconciler? sideEffectReconciler = null) : IHostUpdateExecutor
{
    private static readonly (HostUpdateExecutionState State, string Phase, bool SafeToCancelAndReplay)[] Plan =
    [(HostUpdateExecutionState.Preflight, "preflight", true), (HostUpdateExecutionState.Draining, "drain", true), (HostUpdateExecutionState.Fenced, "fence", true), (HostUpdateExecutionState.BackedUp, "backup", true), (HostUpdateExecutionState.Migrating, "migration", false), (HostUpdateExecutionState.Applying, "apply", false), (HostUpdateExecutionState.Verifying, "verify", true)];
    public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid(out string error))
        {
            return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, error, []);
        }

        using IHostUpdateExecutionLease lease = updateLock.Acquire(TimeSpan.FromSeconds(30), cancellationToken);
        List<HostUpdateExecutionActivity> activities = journal.Read(request.ReleaseId).ToList();
        HostUpdateExecutionState current = activities.LastOrDefault()?.State ?? HostUpdateExecutionState.Accepted;
        if (!HostUpdateRequestFingerprint.Matches(activities, request, out string fingerprintError))
        { Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + fingerprintError); return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, fingerprintError, activities); }
        if (current == HostUpdateExecutionState.Completed)
        {
            return await EnsureCompletedFenceReleasedAsync(request, activities, cancellationToken).ConfigureAwait(false);
        }

        if (current == HostUpdateExecutionState.RecoveryRequired)
        {
            return new(request.ReleaseId, current, "recovery_required", activities);
        }

        if (current == HostUpdateExecutionState.Accepted)
        {
            Append(activities, request, current, "accepted");
        }

        try
        {
            foreach ((HostUpdateExecutionState state, string phase, bool safeToCancelAndReplay) in Plan)
            {
                if (activities.Any(a => a.State == state && a.Phase.EndsWith(":after", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!safeToCancelAndReplay && activities.Any(a => a.State == state && string.Equals(a.Phase, phase + ":before", StringComparison.Ordinal)))
                {
                    HostUpdateSideEffectReconciliation reconciliation = sideEffectReconciler is null
                        ? HostUpdateSideEffectReconciliation.Uncertain("reconciler_unavailable")
                        : await sideEffectReconciler.ReconcileAsync(phase, request, cancellationToken).ConfigureAwait(false);
                    if (reconciliation.Reconciled)
                    {
                        Append(activities, request, state, phase + ":after");
                        current = state;
                        continue;
                    }

                    string failure = "uncertain_side_effect:" + phase + ":" + reconciliation.Detail;
                    Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + failure);
                    return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, failure, activities);
                }
                if (safeToCancelAndReplay && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }

                Append(activities, request, state, phase + ":before");
                try
                { await InvokeAsync(state, request, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!safeToCancelAndReplay) { string failure = "uncertain_side_effect:" + phase + ":canceled"; Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + failure); return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, failure, activities); }
                catch (OperationCanceledException) when (safeToCancelAndReplay) { return new(request.ReleaseId, current, "canceled", activities); }
                Append(activities, request, state, phase + ":after");
                current = state;
                if (safeToCancelAndReplay && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }
            }
            Append(activities, request, HostUpdateExecutionState.Completed, "completed");
            return await EnsureCompletedFenceReleasedAsync(request, activities, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + ex.GetType().Name); return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, ex.GetType().Name, activities); }
    }

    private async Task<HostUpdateExecutionResult> EnsureCompletedFenceReleasedAsync(
        HostUpdateExecutionRequest request,
        List<HostUpdateExecutionActivity> activities,
        CancellationToken cancellationToken)
    {
        if (!activities.Any(a => a.State == HostUpdateExecutionState.Completed && string.Equals(a.Phase, "installed-state:after", StringComparison.Ordinal)))
        {
            if (!activities.Any(a => a.State == HostUpdateExecutionState.Completed && string.Equals(a.Phase, "installed-state:before", StringComparison.Ordinal)))
            {
                Append(activities, request, HostUpdateExecutionState.Completed, "installed-state:before");
            }

            await steps.PersistInstalledStateAsync(request, cancellationToken).ConfigureAwait(false);
            Append(activities, request, HostUpdateExecutionState.Completed, "installed-state:after");
        }

        if (activities.Any(a => a.State == HostUpdateExecutionState.Completed && string.Equals(a.Phase, "fence-release:after", StringComparison.Ordinal)))
        {
            return new(request.ReleaseId, HostUpdateExecutionState.Completed, null, activities);
        }

        if (!activities.Any(a => a.State == HostUpdateExecutionState.Completed && string.Equals(a.Phase, "fence-release:before", StringComparison.Ordinal)))
        {
            Append(activities, request, HostUpdateExecutionState.Completed, "fence-release:before");
        }

        try
        {
            await steps.ReleaseFenceAsync(cancellationToken).ConfigureAwait(false);
            Append(activities, request, HostUpdateExecutionState.Completed, "fence-release:after");
            return new(request.ReleaseId, HostUpdateExecutionState.Completed, null, activities);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string failure = "fence_release_failed:" + exception.GetType().Name;
            Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + failure);
            return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, failure, activities);
        }
    }

    private Task InvokeAsync(HostUpdateExecutionState state, HostUpdateExecutionRequest request, CancellationToken cancellationToken) => state switch { HostUpdateExecutionState.Preflight => steps.PreflightAsync(request, cancellationToken), HostUpdateExecutionState.Draining => steps.DrainAsync(request, cancellationToken), HostUpdateExecutionState.Fenced => steps.FenceAsync(request, cancellationToken), HostUpdateExecutionState.BackedUp => steps.BackupAsync(request, cancellationToken), HostUpdateExecutionState.Migrating => steps.MigrateAsync(request, cancellationToken), HostUpdateExecutionState.Applying => steps.ApplyAsync(request, cancellationToken), HostUpdateExecutionState.Verifying => steps.VerifyAsync(request, cancellationToken), _ => Task.CompletedTask };
    private void Append(List<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) { var activity = new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow, HostUpdateRequestFingerprint.Compute(request)); journal.Append(activity); activities.Add(activity); }
}

/// <summary>Loads the authoritative execution request from the already verified/staged host-update journal.</summary>
public interface IHostUpdateExecutionRequestResolver
{
    Task<HostUpdateExecutionRequest?> ResolveAsync(string releaseId, CancellationToken cancellationToken);
}

/// <summary>
/// Converts a completed #2662 staging receipt into the #2663 executor request. The admin API
/// supplies only the release id; sequence, source commit, channel, manifest digest, active
/// service set, platform and digests all come from durable server-side verified evidence.
/// </summary>
public sealed class HostUpdateExecutionRequestResolver(IHostUpdateJournal foundationJournal) : IHostUpdateExecutionRequestResolver
{
    public async Task<HostUpdateExecutionRequest?> ResolveAsync(string releaseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return null;
        }

        IReadOnlyList<HostUpdateJournalEntry> entries = await foundationJournal.ReadAsync(cancellationToken).ConfigureAwait(false);
        HostUpdateJournalEntry? staged = entries.LastOrDefault(entry =>
            entry.State == HostUpdateLifecycle.Staged &&
            string.Equals(entry.Code, "staged", StringComparison.Ordinal) &&
            string.Equals(entry.Snapshot.Identity?.ReleaseId, releaseId, StringComparison.Ordinal) &&
            entry.Snapshot.Receipt is { IsComplete: true, Code: "staged" });
        if (staged?.Snapshot.Identity is not { } identity)
        {
            return null;
        }

        if (!Enum.TryParse(identity.Channel, ignoreCase: true, out HostUpdateExecutionChannel channel))
        {
            return null;
        }

        long sequence;
        try
        {
            sequence = SignedUpdateManifestValidator.DeriveSequence(identity.Version);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return null;
        }

        string platform = staged.Snapshot.Platform;
        HostUpdateExecutionTarget[] targets = [.. staged.Snapshot.RequiredComponents
            .OrderBy(component => component, StringComparer.Ordinal)
            .Select(component =>
            {
                string key = SignedUpdateManifestValidator.PlatformKey(component, platform);
                return staged.Snapshot.ComponentPlatformDigests.TryGetValue(key, out string? digest)
                    ? new HostUpdateExecutionTarget(component, platform, digest)
                    : null;
            })
            .OfType<HostUpdateExecutionTarget>()];

        var request = new HostUpdateExecutionRequest(
            identity.ReleaseId,
            sequence,
            identity.ManifestDigest,
            identity.SourceCommit,
            channel,
            targets);
        return request.IsValid(out _) ? request : null;
    }
}
public sealed class FileHostUpdateExecutionLock(string path) : IHostUpdateExecutionLock
{
    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            { FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); stream.SetLength(0); using (StreamWriter writer = new(stream, leaveOpen: true)) { writer.Write($"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}"); writer.Flush(); } stream.Flush(true); return new Lease(stream); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < timeout) { Thread.Sleep(25); }
            catch (IOException) { throw new TimeoutException("host_update_lock_timeout"); }
        }
    }
    private sealed class Lease(FileStream stream) : IHostUpdateExecutionLease { public void Dispose() => stream.Dispose(); }
}

/// <summary>Outcome of proving whether a prior side effect already reached its target state.</summary>
public sealed record HostUpdateSideEffectReconciliation(bool Reconciled, string Detail)
{
    public static HostUpdateSideEffectReconciliation Complete(string detail) => new(true, detail);

    public static HostUpdateSideEffectReconciliation Uncertain(string detail) => new(false, detail);
}

/// <summary>Proves migration/apply side-effect completion after a crash before the after-marker was journaled.</summary>
public interface IHostUpdateSideEffectReconciler
{
    Task<HostUpdateSideEffectReconciliation> ReconcileAsync(string phase, HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles unsafe side effects with operation-specific evidence: migrations are complete only
/// when every context reports no pending migrations; apply is complete only when exact running
/// digests match the immutable request. Anything else remains NeedsOperator.
/// </summary>
public sealed class HostUpdateSideEffectReconciler(
    IHostUpdateMigrationReconciler migrationReconciler,
    IHostUpdateDigestVerifier digestVerifier) : IHostUpdateSideEffectReconciler
{
    public async Task<HostUpdateSideEffectReconciliation> ReconcileAsync(string phase, HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.Equals(phase, "migration", StringComparison.Ordinal))
        {
            return await migrationReconciler.IsReconciledAsync(request, cancellationToken).ConfigureAwait(false)
                ? HostUpdateSideEffectReconciliation.Complete("migration_state_matches")
                : HostUpdateSideEffectReconciliation.Uncertain("migration_state_incomplete");
        }

        if (string.Equals(phase, "apply", StringComparison.Ordinal))
        {
            try
            {
                IReadOnlyDictionary<string, string> digests = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
                await digestVerifier.VerifyDigestsAsync(digests, cancellationToken).ConfigureAwait(false);
                return HostUpdateSideEffectReconciliation.Complete("running_digests_match");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HostUpdateSideEffectReconciliation.Uncertain("running_digests_unverified:" + exception.GetType().Name);
            }
        }

        return HostUpdateSideEffectReconciliation.Uncertain("unsupported_phase");
    }
}
public sealed class FileHostUpdateExecutionJournal(string path) : IHostUpdateExecutionJournal
{
    private sealed record JournalRecord(string PreviousHash, string Payload, string Hash, HostUpdateExecutionActivity Activity);
    public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        List<HostUpdateExecutionActivity> result = [];
        string previous = string.Empty;
        foreach (string line in File.ReadLines(path))
        {
            JournalRecord? record;
            try
            { record = JsonSerializer.Deserialize<JournalRecord>(line); }
            catch (JsonException ex) { throw new InvalidDataException("journal_corrupt", ex); }
            if (record is null || record.Activity is null || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.Hash), SHA256.HashData(Encoding.UTF8.GetBytes(record.PreviousHash + record.Payload))))
            {
                throw new InvalidDataException("journal_integrity_failure");
            }

            previous = record.Hash;
            if (record.Activity.ReleaseId == releaseId)
            {
                result.Add(record.Activity);
            }
        }
        return result;
    }

    /// <summary>
    /// Enumerates every distinct release id ever recorded in this journal, in first-seen order.
    /// Used at startup by restart reconciliation (Bishop/Hicks #2663 finding: the in-memory
    /// admission gate resets open on process restart even when a release was left mid-flight or
    /// in <see cref="HostUpdateExecutionState.RecoveryRequired"/>) to discover which releases, if
    /// any, still require the perimeter to stay fenced before any new writer is admitted.
    /// </summary>
    public IReadOnlyList<string> ListReleaseIds()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string previous = string.Empty;
        foreach (string line in File.ReadLines(path))
        {
            JournalRecord? record;
            try
            { record = JsonSerializer.Deserialize<JournalRecord>(line); }
            catch (JsonException ex) { throw new InvalidDataException("journal_corrupt", ex); }
            if (record is null || record.Activity is null || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.Hash), SHA256.HashData(Encoding.UTF8.GetBytes(record.PreviousHash + record.Payload))))
            {
                throw new InvalidDataException("journal_integrity_failure");
            }

            previous = record.Hash;
            if (seen.Add(record.Activity.ReleaseId))
            {
                ids.Add(record.Activity.ReleaseId);
            }
        }

        return ids;
    }

    public void Append(HostUpdateExecutionActivity activity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        // Kane/Bishop/Hicks review (issue #2663): the atomic temp-file-then-move pattern used here
        // for crash safety must still preserve every prior record. An earlier version wrote only
        // the newest line to the temp file before moving it over `path`, which silently truncated
        // the entire hash-chained journal down to its last entry on every append. Read the full
        // existing chain first and re-write it in full (existing lines + the new one) so a crash
        // between the write and the move can never observe a shorter-than-before journal, and a
        // successful move preserves the full chain rather than replacing it.
        string[] existingLines = File.Exists(path) ? File.ReadAllLines(path) : [];
        string previous = string.Empty;
        if (File.Exists(path))
        {
            if (existingLines.Length == 0)
            {
                throw new InvalidDataException("journal_corrupt");
            }

            string last = existingLines[^1];
            previous = JsonSerializer.Deserialize<JournalRecord>(last)?.Hash ?? throw new InvalidDataException("journal_corrupt");
        }

        string payload = JsonSerializer.Serialize(activity);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + payload))).ToLowerInvariant();
        string line = JsonSerializer.Serialize(new JournalRecord(previous, payload, hash, activity));
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var builder = new StringBuilder();
        foreach (string existingLine in existingLines)
        {
            builder.Append(existingLine).Append(Environment.NewLine);
        }

        builder.Append(line).Append(Environment.NewLine);
        using (FileStream stream = new(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(builder.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, true);
        FlushDirectory(Path.GetDirectoryName(path) ?? ".");
    }
    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using FileStream stream = new(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
