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

    public bool IsValid(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(RequestId) || string.IsNullOrWhiteSpace(ReleaseId) || AuthenticatedSequence < 1 || !Digest(ManifestDigest) || !Commit(SourceCommit) ||
            string.IsNullOrWhiteSpace(TrustRoot) || PolicyRevision < 0 || string.IsNullOrWhiteSpace(PolicyFingerprint) || string.IsNullOrWhiteSpace(HostPlatform))
        {
            error = "release_binding_invalid";
            return false;
        }

        if (Targets is null || Targets.Count != RequiredTargetCount)
        {
            error = "target_invalid";
            return false;
        }

        if (Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.ServiceId) || !string.Equals(t.Platform, HostPlatform, StringComparison.Ordinal) || !Digest(t.ChildDigest)))
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

public sealed record HostUpdateExecutionActivity(string ActivityId, string ReleaseId, HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt)
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

    void Append(HostUpdateExecutionActivity activity);
}

public interface IHostUpdateExecutionLease : IDisposable
{
}

public interface IHostUpdateExecutionLock
{
    IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class HostUpdateExecutor(IHostUpdateExecutionSteps steps, IHostUpdateExecutionJournal journal, IHostUpdateExecutionLock updateLock) : IHostUpdateExecutor
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

        foreach ((HostUpdateExecutionState state, string phase, bool safe) in Plan)
        {
            bool started = activities.Any(a => a.State == state && string.Equals(a.Phase, phase + ":before", StringComparison.Ordinal));
            bool finished = activities.Any(a => a.State == state && string.Equals(a.Phase, phase + ":after", StringComparison.Ordinal));
            if (!safe && started && !finished)
            {
                Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "restart_uncertain:" + phase);
                return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, "unsafe_phase_interrupted", activities);
            }
        }

        try
        {
            foreach ((HostUpdateExecutionState state, string phase, bool safe) in Plan)
            {
                if (activities.Any(a => a.State == state && a.Phase.EndsWith(":after", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (safe && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }

                Append(activities, request, state, phase + ":before");
                await InvokeAsync(state, request, safe ? cancellationToken : CancellationToken.None);
                Append(activities, request, state, phase + ":after");
                current = state;
                if (safe && cancellationToken.IsCancellationRequested)
                {
                    return new(request.ReleaseId, current, "canceled", activities);
                }
            }

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
            Targets = request.Targets.OrderBy(target => target.ServiceId, StringComparer.Ordinal),
        });
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
