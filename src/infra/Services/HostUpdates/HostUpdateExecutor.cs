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
    public const int RequiredTargetCount = 6;
    public bool IsValid(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(ReleaseId) || AuthenticatedSequence < 1 || !Digest(ManifestDigest) || !Commit(SourceCommit) || Targets is null || Targets.Count != RequiredTargetCount) { error = "release_identity_invalid"; return false; }
        if (Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.ServiceId) || string.IsNullOrWhiteSpace(t.Platform) || !Digest(t.ChildDigest))) { error = "target_invalid"; return false; }
        if (Targets.Select(t => t.ServiceId).Distinct(StringComparer.Ordinal).Count() != RequiredTargetCount) { error = "target_set_invalid"; return false; }
        return true;
    }
    private static bool Digest(string value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71 && value[7..].All(Uri.IsHexDigit);
    private static bool Commit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);
}
public sealed record HostUpdateExecutionActivity(string ActivityId, string ReleaseId, HostUpdateExecutionState State, string Phase, DateTimeOffset RecordedAt);
public sealed record HostUpdateExecutionResult(string ReleaseId, HostUpdateExecutionState State, string? FailureCode, IReadOnlyList<HostUpdateExecutionActivity> Activities) { public bool Succeeded => State == HostUpdateExecutionState.Completed; }
public interface IHostUpdateExecutor { Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default); }
public interface IHostUpdateExecutionSteps
{
    Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct); Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct);
}
public interface IHostUpdateExecutionJournal { IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId); void Append(HostUpdateExecutionActivity activity); }
public interface IHostUpdateExecutionLease : IDisposable { }
public interface IHostUpdateExecutionLock { IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken); }

public sealed class HostUpdateExecutor(IHostUpdateExecutionSteps steps, IHostUpdateExecutionJournal journal, IHostUpdateExecutionLock updateLock) : IHostUpdateExecutor
{
    private static readonly (HostUpdateExecutionState State, string Phase, bool Safe)[] Plan =
    [ (HostUpdateExecutionState.Preflight, "preflight", true), (HostUpdateExecutionState.Draining, "drain", true), (HostUpdateExecutionState.Fenced, "fence", true), (HostUpdateExecutionState.BackedUp, "backup", true), (HostUpdateExecutionState.Migrating, "migration", false), (HostUpdateExecutionState.Applying, "apply", false), (HostUpdateExecutionState.Verifying, "verify", true) ];
    public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid(out string error)) return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, error, []);
        using IHostUpdateExecutionLease lease = updateLock.Acquire(TimeSpan.FromSeconds(30), cancellationToken);
        List<HostUpdateExecutionActivity> activities = journal.Read(request.ReleaseId).ToList(); HostUpdateExecutionState current = activities.LastOrDefault()?.State ?? HostUpdateExecutionState.Accepted;
        if (current == HostUpdateExecutionState.Completed || current == HostUpdateExecutionState.RecoveryRequired) return new(request.ReleaseId, current, current == HostUpdateExecutionState.Completed ? null : "recovery_required", activities);
        if (current == HostUpdateExecutionState.Accepted) Append(activities, request, current, "accepted");
        try
        {
            foreach ((HostUpdateExecutionState state, string phase, bool safe) in Plan)
            {
                if (activities.Any(a => a.State == state && a.Phase.EndsWith(":after", StringComparison.Ordinal))) continue;
                if (safe && cancellationToken.IsCancellationRequested) return new(request.ReleaseId, current, "canceled", activities);
                Append(activities, request, state, phase + ":before"); await InvokeAsync(state, request); Append(activities, request, state, phase + ":after"); current = state;
                if (safe && cancellationToken.IsCancellationRequested) return new(request.ReleaseId, current, "canceled", activities);
            }
            Append(activities, request, HostUpdateExecutionState.Completed, "completed"); return new(request.ReleaseId, HostUpdateExecutionState.Completed, null, activities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Append(activities, request, HostUpdateExecutionState.RecoveryRequired, "failure:" + ex.GetType().Name); return new(request.ReleaseId, HostUpdateExecutionState.RecoveryRequired, ex.GetType().Name, activities); }
    }
    private Task InvokeAsync(HostUpdateExecutionState state, HostUpdateExecutionRequest request) => state switch { HostUpdateExecutionState.Preflight => steps.PreflightAsync(request, CancellationToken.None), HostUpdateExecutionState.Draining => steps.DrainAsync(request, CancellationToken.None), HostUpdateExecutionState.Fenced => steps.FenceAsync(request, CancellationToken.None), HostUpdateExecutionState.BackedUp => steps.BackupAsync(request, CancellationToken.None), HostUpdateExecutionState.Migrating => steps.MigrateAsync(request, CancellationToken.None), HostUpdateExecutionState.Applying => steps.ApplyAsync(request, CancellationToken.None), HostUpdateExecutionState.Verifying => steps.VerifyAsync(request, CancellationToken.None), _ => Task.CompletedTask };
    private void Append(List<HostUpdateExecutionActivity> activities, HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) { var activity = new HostUpdateExecutionActivity(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow); journal.Append(activity); activities.Add(activity); }
}

public sealed class FileHostUpdateExecutionLock(string path) : IHostUpdateExecutionLock
{
    public IHostUpdateExecutionLease Acquire(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "."); long start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); stream.SetLength(0); using (StreamWriter writer = new(stream, leaveOpen: true)) { writer.Write($"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}"); writer.Flush(); } stream.Flush(true); return new Lease(stream); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < timeout) { Thread.Sleep(25); }
            catch (IOException) { throw new TimeoutException("host_update_lock_timeout"); }
        }
    }
    private sealed class Lease(FileStream stream) : IHostUpdateExecutionLease { public void Dispose() => stream.Dispose(); }
}

public sealed class FileHostUpdateExecutionJournal(string path) : IHostUpdateExecutionJournal
{
    private sealed record JournalRecord(string PreviousHash, string Payload, string Hash, HostUpdateExecutionActivity Activity);
    public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId)
    {
        if (!File.Exists(path)) return []; List<HostUpdateExecutionActivity> result = []; string previous = string.Empty;
        foreach (string line in File.ReadLines(path))
        {
            JournalRecord? record; try { record = JsonSerializer.Deserialize<JournalRecord>(line); } catch (JsonException ex) { throw new InvalidDataException("journal_corrupt", ex); }
            if (record is null || record.Activity is null || !string.Equals(record.PreviousHash, previous, StringComparison.Ordinal) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.Hash), SHA256.HashData(Encoding.UTF8.GetBytes(record.PreviousHash + record.Payload)))) throw new InvalidDataException("journal_integrity_failure");
            previous = record.Hash; if (record.Activity.ReleaseId == releaseId) result.Add(record.Activity);
        }
        return result;
    }
    public void Append(HostUpdateExecutionActivity activity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "."); string previous = string.Empty; if (File.Exists(path)) { string? last = File.ReadLines(path).LastOrDefault(); if (last is null) throw new InvalidDataException("journal_corrupt"); previous = JsonSerializer.Deserialize<JournalRecord>(last)?.Hash ?? throw new InvalidDataException("journal_corrupt"); }
        string payload = JsonSerializer.Serialize(activity); string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + payload))).ToLowerInvariant(); string line = JsonSerializer.Serialize(new JournalRecord(previous, payload, hash, activity)); string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, line + Environment.NewLine, new UTF8Encoding(false)); using (FileStream stream = new(temp, FileMode.Open, FileAccess.Read, FileShare.Read)) stream.Flush(true); File.Move(temp, path, true);
    }
}
