#pragma warning disable S2681
#pragma warning disable IDISP007
#pragma warning disable SA1516
#pragma warning disable SA1513
#pragma warning disable SA1518
#pragma warning disable SA1507, SA1515
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

public enum HostUpdateExecutionState
{
    Accepted,
    Preflight,
    Draining,
    Fenced,
    BackedUp,
    Migrating,
    Applying,
    Verifying,
    Completed,
    RecoveryRequired,
}

public enum HostUpdateExecutionChannel
{
    Stable,
    Insider,
}

public sealed record HostUpdateExecutionTarget(string ServiceId, string Platform, string ChildDigest);

public sealed record HostUpdateExecutionRequest(string ReleaseId, long AuthenticatedSequence, string ManifestDigest, string SourceCommit, HostUpdateExecutionChannel Channel, IReadOnlyList<HostUpdateExecutionTarget> Targets)
{
    public const int RequiredTargetCount = 6;

    public static IReadOnlySet<string> RequiredServiceIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"
    };

    public string RequestId { get; init; } = string.Empty;

    public string TrustRoot { get; init; } = string.Empty;

    public long PolicyRevision { get; init; } = -1;

    public string PolicyFingerprint { get; init; } = string.Empty;

    public string HostPlatform { get; init; } = string.Empty;

    public HostUpdateAuthorizationKind AuthorizationKind { get; init; } = HostUpdateAuthorizationKind.StandingPolicy;

    public bool IsValid(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(RequestId) || string.IsNullOrWhiteSpace(ReleaseId) || AuthenticatedSequence < 1 || !Digest(ManifestDigest) || !Commit(SourceCommit) ||
            string.IsNullOrWhiteSpace(TrustRoot) || PolicyRevision < 0 || string.IsNullOrWhiteSpace(PolicyFingerprint) ||
            !SignedUpdateManifestValidator.IsPlatform(HostPlatform))
        {
            error = "release_binding_invalid";
            return false;
        }

        if (Targets is null || Targets.Count != RequiredTargetCount)
        {
            error = "target_invalid";
            return false;
        }

        if (Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.ServiceId) ||
            !SignedUpdateManifestValidator.IsPlatform(t.Platform) ||
            !string.Equals(t.Platform, HostPlatform, StringComparison.Ordinal) || !Digest(t.ChildDigest)))
        {
            error = "target_invalid";
            return false;
        }

        if (Targets.Select(t => t.ServiceId).ToHashSet(StringComparer.Ordinal).SetEquals(RequiredServiceIds) is false)
        {
            error = "target_set_invalid";
            return false;
        }

        return true;
    }

    private static bool Digest(string value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);

    private static bool Commit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);
}

public sealed record HostUpdateExecutionActivity(
    string ActivityId,
    string ReleaseId,
    HostUpdateExecutionState State,
    string Phase,
    DateTimeOffset RecordedAt,
    string? RequestFingerprint = null)
{
    public string? RequestBindingHash { get; init; }

    public HostUpdateExecutionRequest? RequestBinding { get; init; }
}

public sealed record HostUpdateExecutionResult(string ReleaseId, HostUpdateExecutionState State, string? FailureCode, IReadOnlyList<HostUpdateExecutionActivity> Activities)
{
    public bool Succeeded => State == HostUpdateExecutionState.Completed;
}

public interface IHostUpdateExecutor
{
    Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface IHostUpdateExecutionSteps
{
    Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct);

    Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct);
}

public interface IHostUpdateExecutionJournal
{
    IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId);

    IReadOnlyList<string> ListReleaseIds();

    void Append(HostUpdateExecutionActivity activity);
}

public interface IHostUpdateExecutionLease : IDisposable
{
}

public interface IHostUpdateExecutionLock
{
    IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class HostUpdateExecutor(
    IHostUpdateExecutionSteps steps,
    IHostUpdateExecutionJournal journal,
    IHostUpdateExecutionLock updateLock,
    IHostUpdateSideEffectReconciler? sideEffectReconciler = null) : IHostUpdateExecutor
{
    private static readonly (HostUpdateExecutionState State, string Phase, bool Safe)[] Plan =
    [
        (HostUpdateExecutionState.Preflight, "preflight", true),
        (HostUpdateExecutionState.Draining, "drain", true),
        (HostUpdateExecutionState.Fenced, "fence", true),
        (HostUpdateExecutionState.BackedUp, "backup", true),
        (HostUpdateExecutionState.Migrating, "migration", false),
        (HostUpdateExecutionState.Applying, "apply", false),
        (HostUpdateExecutionState.Verifying, "verify", true),
    ];

    public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid(out string error))
        {
            return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, error, []);
        }

        string bindingHash = HostUpdateRequestBinding.Compute(request);
        using IHostUpdateExecutionLease lease = updateLock.Acquire(TimeSpan.FromSeconds(30), cancellationToken);
        List<HostUpdateExecutionActivity> activities = journal.Read(request.ReleaseId).ToList();
        HostUpdateExecutionState current = activities.LastOrDefault()?.State ?? HostUpdateExecutionState.Accepted;
        if (activities.Any(activity => activity.RequestBindingHash is not null && activity.RequestBindingHash != bindingHash))
        {
            return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "request_binding_conflict", activities);
        }

        if (current == HostUpdateExecutionState.Completed || current == HostUpdateExecutionState.RecoveryRequired)
        {
            return new(request.ReleaseId, current, current == HostUpdateExecutionState.Completed ? null : "recovery_required", activities);
        }

        if (current == HostUpdateExecutionState.Accepted)
        {
            Append(activities, request, current, "accepted");
        }

        try
        {
            foreach ((HostUpdateExecutionState state, string phase, bool safe) in Plan)
            {
                if (activities.Any(a => a.State == state && a.Phase.EndsWith(":after", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!safe && activities.Any(a => a.State == state && string.Equals(a.Phase, phase + ":before", StringComparison.Ordinal)))
                {
                    HostUpdateSideEffectReconciliation reconciliation = sideEffectReconciler is null
                        ? HostUpdateSideEffectReconciliation.Uncertain("reconciler_unavailable")
                        : await sideEffectReconciler.ReconcileAsync(phase, request, cancellationToken).ConfigureAwait(false);
                    if (!reconciliation.Reconciled)
                    {
                        string failure = "uncertain_side_effect:" + phase + ":" + reconciliation.Detail;
                        Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + failure);
                        return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, failure, activities);
                    }

                    Append(activities, request, state, phase + ":after");
                    current = state;
                    continue;
                }

                if (safe && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }

                Append(activities, request, state, phase + ":before");
                try
                {
                    await InvokeAsync(state, request, safe ? cancellationToken : CancellationToken.None);
                }
                catch (OperationCanceledException) when (!safe)
                {
                    string failure = "uncertain_side_effect:" + phase + ":canceled";
                    Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + failure);
                    return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, failure, activities);
                }

                Append(activities, request, state, phase + ":after");
                current = state;
                if (safe && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }
            }

            Append(activities, request, HostUpdateExecutionState.Completed, "fence-release:after");
            Append(activities, request, HostUpdateExecutionState.Completed, "completed");
            return new(request.ReleaseId, HostUpdateExecutionState.Completed, null, activities);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(request.ReleaseId, current, "canceled", activities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + ex.GetType().Name);
            return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, ex.GetType().Name, activities);
        }
    }

    private Task InvokeAsync(HostUpdateExecutionState state, HostUpdateExecutionRequest request, CancellationToken cancellationToken) => state switch
    {
        HostUpdateExecutionState.Preflight => steps.PreflightAsync(request, cancellationToken),
        HostUpdateExecutionState.Draining => steps.DrainAsync(request, cancellationToken),
        HostUpdateExecutionState.Fenced => steps.FenceAsync(request, cancellationToken),
        HostUpdateExecutionState.BackedUp => steps.BackupAsync(request, cancellationToken),
        HostUpdateExecutionState.Migrating => steps.MigrateAsync(request, cancellationToken),
        HostUpdateExecutionState.Applying => steps.ApplyAsync(request, cancellationToken),
        HostUpdateExecutionState.Verifying => steps.VerifyAsync(request, cancellationToken),
        _ => Task.CompletedTask,
    };


    private void Append(List<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase)
    {
        HostUpdateExecutionActivity activity = new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
            RequestBinding = request,
        };
        journal.Append(activity);
        activities.Add(activity);
    }
}

/// <summary>Outcome of proving whether a prior unsafe side effect already reached its target state.</summary>
public sealed record HostUpdateSideEffectReconciliation(bool Reconciled, string Detail)
{
    public static HostUpdateSideEffectReconciliation Complete(string detail) => new(true, detail);

    public static HostUpdateSideEffectReconciliation Uncertain(string detail) => new(false, detail);
}

/// <summary>Proves migration or apply completion after a crash before its completion marker was durable.</summary>
public interface IHostUpdateSideEffectReconciler
{
    Task<HostUpdateSideEffectReconciliation> ReconcileAsync(
        string phase,
        HostUpdateExecutionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Reconciles migration and apply state only through their authoritative state probes.</summary>
public sealed class HostUpdateSideEffectReconciler(
    IHostUpdateMigrationReconciler migrationReconciler,
    IHostUpdateDigestVerifier digestVerifier) : IHostUpdateSideEffectReconciler
{
    public async Task<HostUpdateSideEffectReconciliation> ReconcileAsync(
        string phase,
        HostUpdateExecutionRequest request,
        CancellationToken cancellationToken)
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
                IReadOnlyDictionary<string, string> digests = request.Targets.ToDictionary(
                    target => target.ServiceId,
                    target => target.ChildDigest,
                    StringComparer.Ordinal);
                await digestVerifier.VerifyDigestsAsync(digests, cancellationToken).ConfigureAwait(false);
                return HostUpdateSideEffectReconciliation.Complete("running_digests_match");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return HostUpdateSideEffectReconciliation.Uncertain(
                    "running_digests_unverified:" + exception.GetType().Name);
            }
        }

        return HostUpdateSideEffectReconciliation.Uncertain("unsupported_phase");
    }
}


public static class HostUpdateExecutionRequestBuilder
{
    private static readonly string[] ServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    public static HostUpdateExecutionRequest FromExecutorRequest(
        HostUpdateExecutorRequest request,
        string hostPlatform,
        HostUpdateAuthorizationKind authorizationKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPlatform);

        HostUpdateExecutionChannel channel = request.Channel switch
        {
            "stable" => HostUpdateExecutionChannel.Stable,
            "insider" => HostUpdateExecutionChannel.Insider,
            _ => throw new ArgumentException("channel_invalid", nameof(request)),
        };

        return new HostUpdateExecutionRequest(
            request.ReleaseId,
            request.Sequence,
            request.ManifestDigest,
            request.SourceCommit,
            channel,
            CreateTargets(request.PlatformDigests, hostPlatform))
        {
            RequestId = request.RequestId,
            TrustRoot = request.TrustRoot,
            PolicyRevision = request.PolicyRevision,
            PolicyFingerprint = request.PolicyFingerprint,
            HostPlatform = hostPlatform,
            AuthorizationKind = authorizationKind,
        };
    }

    private static List<HostUpdateExecutionTarget> CreateTargets(HostUpdatePlatformDigests digests, string hostPlatform) =>
    [
        new(ServiceIds[0], hostPlatform, digests.Api),
        new(ServiceIds[1], hostPlatform, digests.Frontend),
        new(ServiceIds[2], hostPlatform, digests.SlicerHost),
        new(ServiceIds[3], hostPlatform, digests.PrinterDiscovery),
        new(ServiceIds[4], hostPlatform, digests.OrcaslicerWorker),
        new(ServiceIds[5], hostPlatform, digests.Monolith),
    ];
}

public static class HostUpdateRequestBinding
{
    public static string Compute(HostUpdateExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HostUpdateCanonical.Hash(new
        {
            request.TrustRoot,
            request.PolicyRevision,
            request.PolicyFingerprint,
            request.ReleaseId,
            request.Channel,
            request.RequestId,
            request.AuthenticatedSequence,
            request.ManifestDigest,
            request.SourceCommit,
            request.HostPlatform,
            request.AuthorizationKind,
            Targets = request.Targets.OrderBy(target => target.ServiceId, StringComparer.Ordinal),
        });
    }
}

/// <summary>
/// Compatibility facade for recovery components. Scheduler-bound requests use the stronger
/// immutable binding hash, including authorization and policy identity, for every comparison.
/// </summary>
public static class HostUpdateRequestFingerprint
{
    public static string Compute(HostUpdateExecutionRequest request) => HostUpdateRequestBinding.Compute(request);

    public static bool Matches(
        IReadOnlyList<HostUpdateExecutionActivity> activities,
        HostUpdateExecutionRequest request,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(request);

        string expected = Compute(request);
        foreach (HostUpdateExecutionActivity activity in activities)
        {
            string? actual = activity.RequestBindingHash ?? activity.RequestFingerprint;
            if (string.IsNullOrWhiteSpace(actual))
            {
                error = "request_fingerprint_missing";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(actual),
                    Encoding.UTF8.GetBytes(expected)))
            {
                error = "request_fingerprint_mismatch";
                return false;
            }
        }

        error = string.Empty;
        return true;
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
            {
                HostStateFileSecurity.RejectReparseTarget(path);
                FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                using (StreamWriter writer = new(stream, leaveOpen: true))
                {
                    writer.Write($"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}");
                    writer.Flush();
                }

                stream.Flush(true);
                return new Lease(stream);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < timeout)
            {
                Thread.Sleep(25);
            }
            catch (IOException)
            {
                throw new TimeoutException("host_update_lock_timeout");
            }
        }
    }

    private sealed class Lease(FileStream stream) : IHostUpdateExecutionLease
    {
        public void Dispose() => stream.Dispose();
    }
}

public sealed class FileHostUpdateExecutionJournal(string path) : IHostUpdateExecutionJournal
{
    private readonly string stagedPath = path + ".staged";

    private sealed record JournalRecord(string PreviousHash, string Payload, string Hash, HostUpdateExecutionActivity Activity);

    public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) =>
        ReadValidatedRecords().Where(record => record.Activity.ReleaseId == releaseId).Select(record => record.Activity).ToArray();

    public IReadOnlyList<string> ListReleaseIds() =>
        ReadValidatedRecords()
            .Select(record => record.Activity.ReleaseId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public void Append(HostUpdateExecutionActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        RecoverStagedFile();
        List<JournalRecord> records = ReadValidatedRecords();
        string previous = records.LastOrDefault()?.Hash ?? string.Empty;
        string payload = JsonSerializer.Serialize(activity);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previous + payload)));
        records.Add(new JournalRecord(previous, payload, hash, activity));
        string contents = string.Join('\n', records.Select(record => JsonSerializer.Serialize(record))) + "\n";
        _ = Parse(contents);
        AtomicRewrite(contents);
    }

    private List<JournalRecord> ReadValidatedRecords()
    {
        RecoverStagedFile();
        if (!File.Exists(path))
        {
            return [];
        }

        HostStateFileSecurity.RejectReparseTarget(path);
        return Parse(File.ReadAllText(path));
    }

    private static List<JournalRecord> Parse(string contents)
    {
        if (contents.Length != 0 && !contents.EndsWith('\n'))
        {
            throw new InvalidDataException("journal_corrupt");
        }

        List<JournalRecord> records = [];
        string previous = string.Empty;
        foreach (string line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("journal_corrupt", ex);
            }

            byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes((record?.PreviousHash ?? string.Empty) + (record?.Payload ?? string.Empty)));
            byte[] actual;
            try
            {
                actual = Convert.FromHexString(record?.Hash ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("journal_integrity_failure", ex);
            }

            if (record?.Activity is null || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException("journal_integrity_failure");
            }

            records.Add(record);
            previous = record.Hash;
        }

        return records;
    }

    private void AtomicRewrite(string contents)
    {
        HostStateFileSecurity.RejectReparseTarget(stagedPath);
        try
        {
            using (FileStream stream = new(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }

            _ = Parse(File.ReadAllText(stagedPath));
            HostStateFileSecurity.RejectReparseTarget(path);
            File.Move(stagedPath, path, true);
        }
        finally
        {
            if (File.Exists(stagedPath) && !HostStateFileSecurity.IsReparsePoint(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }

    private void RecoverStagedFile()
    {
        if (!File.Exists(stagedPath))
        {
            return;
        }

        HostStateFileSecurity.RejectReparseTarget(stagedPath);
        // Rename is the commit point. A surviving stage was never committed, so the validated
        // authoritative chain wins even when the staged rewrite is complete.
        File.Delete(stagedPath);
    }
}
