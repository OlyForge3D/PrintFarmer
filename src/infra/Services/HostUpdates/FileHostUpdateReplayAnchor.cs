#pragma warning disable S3218, SA1501, SA1503, SA1516, SA1513, SA1408
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

public interface IHostUpdateReplayAnchorProvisioner
{
    Task ProvisionAsync(CancellationToken ct);
}

public sealed class FileHostUpdateReplayAnchor : IHostUpdateReplayAnchor, IHostUpdateReplayAnchorProvisioner, IDisposable
{
    private const int Version = 1;
    private readonly string _snapshotPath;
    private readonly string _journalPath;
    private readonly string _replayPath;
    private readonly string _replayStagePath;
    private readonly Action<string>? _boundary;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileHostUpdateReplayAnchor(HostStatePath paths, Action<string>? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _snapshotPath = paths.Resolve("replay-anchor.json");
        _journalPath = paths.Resolve("replay-anchor.journal");
        _replayPath = paths.Resolve("host-update-replay.json");
        _replayStagePath = paths.Resolve("host-update-replay.json.staged");
        _boundary = boundary;
    }

    public async Task<long> ReadEpochAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AnchorEntry> entries = ReadJournal();
            if (entries.Count == 0)
            {
                throw new InvalidDataException("host_update_replay_anchor_not_provisioned");
            }

            AnchorEntry head = entries[^1];
            AnchorSnapshot snapshot;
            try
            {
                snapshot = ReadSnapshot();
            }
            catch (InvalidDataException)
            {
                WriteSnapshot(head);
                snapshot = new AnchorSnapshot(Version, head.Epoch, head.StateHash, head.Hash);
            }
            AnchorEntry? snapEntry = entries.SingleOrDefault(entry => entry.Epoch == snapshot.Epoch);
            if (snapshot.Epoch > head.Epoch || snapEntry is null || (!string.Equals(snapEntry.Hash, snapshot.Hash, StringComparison.Ordinal) || !string.Equals(snapEntry.StateHash, snapshot.StateHash, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("host_update_replay_anchor_inconsistent");
            }

            if (snapshot.Epoch < head.Epoch)
            {
                WriteSnapshot(head);
                snapshot = new AnchorSnapshot(Version, head.Epoch, head.StateHash, head.Hash);
            }

            return snapshot.Epoch;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadStateHashAsync(CancellationToken ct)
    {
        _ = await ReadEpochAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ReadJournal()[^1].StateHash;
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);

        // An empty state hash would append an anchor entry that can never be reconciled against
        // the replay state it is supposed to authenticate.
        ArgumentException.ThrowIfNullOrWhiteSpace(stateHash);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<AnchorEntry> entries = ReadJournal();
            if (entries.Count == 0)
            {
                throw new InvalidDataException("host_update_replay_anchor_not_provisioned");
            }

            AnchorEntry prior = entries[^1];
            AnchorSnapshot snapshot;
            try
            {
                snapshot = ReadSnapshot();
            }
            catch (InvalidDataException)
            {
                WriteSnapshot(prior);
                snapshot = new AnchorSnapshot(Version, prior.Epoch, prior.StateHash, prior.Hash);
            }
            if (snapshot.Epoch > prior.Epoch || epoch <= prior.Epoch)
            {
                throw new InvalidDataException("host_update_replay_anchor_rollback");
            }

            if (snapshot.Epoch < prior.Epoch)
            {
                AnchorEntry? matching = entries.SingleOrDefault(entry => entry.Epoch == snapshot.Epoch && entry.StateHash == snapshot.StateHash && entry.Hash == snapshot.Hash);
                if (matching is null)
                {
                    throw new InvalidDataException("host_update_replay_anchor_inconsistent");
                }

                WriteSnapshot(prior);
            }
            else if (!string.Equals(snapshot.Hash, prior.Hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("host_update_replay_anchor_inconsistent");
            }

            string hash = Hash(epoch, prior.Hash, stateHash);
            AnchorEntry next = new(Version, epoch, prior.Hash, stateHash, hash);
            AppendDurably(JsonSerializer.Serialize(next) + "\n");
            _boundary?.Invoke("anchor-journal-committed");
            WriteSnapshot(next);
            _boundary?.Invoke("anchor-snapshot-replaced");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ProvisionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_journalPath))
            {
                IReadOnlyList<AnchorEntry> existing = ReadJournal();
                if (existing.Count != 1 || existing[0].Epoch != 0)
                {
                    throw new InvalidOperationException("host_update_replay_already_provisioned");
                }

                try
                { _ = ReadSnapshot(); }
                catch (InvalidDataException) { WriteSnapshot(existing[0]); }
                if (File.Exists(_replayPath))
                {
                    HostUpdateReplayFileState replay = HostUpdateReplayPersistenceCodec.Deserialize(await File.ReadAllTextAsync(_replayPath, ct));
                    if (replay.Epoch != 0)
                    {
                        throw new InvalidOperationException("host_update_replay_already_provisioned");
                    }

                    return;
                }

                PromoteProvisionStage();
                return;
            }

            if (File.Exists(_snapshotPath) || File.Exists(_replayPath))
            {
                throw new InvalidOperationException("host_update_replay_partially_provisioned_invalid");
            }

            if (File.Exists(_replayStagePath))
            {
                HostUpdateReplayFileState staged = HostUpdateReplayPersistenceCodec.Deserialize(await File.ReadAllTextAsync(_replayStagePath, ct));
                if (staged.Epoch != 0 || staged.Identities.Count != 0 || staged.HighWaterByNamespace.Count != 0)
                {
                    throw new InvalidDataException("host_update_replay_provision_stage_invalid");
                }
            }
            else
            {
                await FileHostUpdateReplayStore.WriteDurablyAsync(_replayStagePath, HostUpdateReplayPersistenceCodec.Serialize(HostUpdateReplayPersistenceCodec.Empty()), ct);
            }

            _boundary?.Invoke("provision-replay-staged");
            string stateHash = StateHash(_replayStagePath);
            string hash = Hash(0, string.Empty, stateHash);
            AnchorEntry initial = new(Version, 0, string.Empty, stateHash, hash);
            AppendDurably(JsonSerializer.Serialize(initial) + "\n");
            _boundary?.Invoke("provision-anchor-journal-committed");
            WriteSnapshot(initial);
            _boundary?.Invoke("provision-anchor-snapshot-replaced");
            PromoteProvisionStage();
            _boundary?.Invoke("provision-replay-committed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PromoteProvisionStage()
    {
        if (!File.Exists(_replayStagePath))
        {
            throw new InvalidDataException("host_update_replay_provision_stage_missing");
        }

        HostUpdateReplayFileState staged = HostUpdateReplayPersistenceCodec.Deserialize(File.ReadAllText(_replayStagePath));
        if (staged.Epoch != 0 || staged.Identities.Count != 0 || staged.HighWaterByNamespace.Count != 0)
        {
            throw new InvalidDataException("host_update_replay_provision_stage_invalid");
        }

        HostStateFileSecurity.RejectReparseTarget(_replayPath);
        File.Move(_replayStagePath, _replayPath, true);
    }
    private AnchorSnapshot ReadSnapshot()
    {
        HostStateFileSecurity.RejectReparseTarget(_snapshotPath);
        try
        {
            AnchorSnapshot? value = JsonSerializer.Deserialize<AnchorSnapshot>(File.ReadAllText(_snapshotPath));
            if (value is null || value.Version != Version || value.Epoch < 0 || string.IsNullOrWhiteSpace(value.Hash))
            {
                throw new InvalidDataException("host_update_replay_anchor_invalid");
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("host_update_replay_anchor_invalid", ex);
        }
    }

    private List<AnchorEntry> ReadJournal()
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        HostStateFileSecurity.RejectReparseTarget(_journalPath);
        string contents = File.ReadAllText(_journalPath);
        if (contents.Length == 0 || !contents.EndsWith('\n'))
        {
            throw new InvalidDataException("host_update_replay_anchor_truncated");
        }

        List<AnchorEntry> result = [];
        string previous = string.Empty;
        long priorEpoch = -1;
        foreach (string line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            AnchorEntry? entry;
            try
            { entry = JsonSerializer.Deserialize<AnchorEntry>(line); }
            catch (JsonException ex) { throw new InvalidDataException("host_update_replay_anchor_truncated", ex); }
            if (entry is null || entry.Version != Version || entry.Epoch <= priorEpoch || entry.PreviousHash != previous || string.IsNullOrWhiteSpace(entry.StateHash) || entry.Hash != Hash(entry.Epoch, entry.PreviousHash, entry.StateHash))
            {
                throw new InvalidDataException("host_update_replay_anchor_corrupt");
            }

            result.Add(entry);
            previous = entry.Hash;
            priorEpoch = entry.Epoch;
        }

        return result;
    }

    private void AppendDurably(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        HostStateFileSecurity.RejectReparseTarget(_journalPath);
        using FileStream stream = new(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    private void WriteSnapshot(AnchorEntry entry)
    {
        string staged = _snapshotPath + ".staged";
        HostStateFileSecurity.RejectReparseTarget(staged);
        try
        {
            using (FileStream stream = new(staged, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(new AnchorSnapshot(Version, entry.Epoch, entry.StateHash, entry.Hash)));
                writer.Flush();
                stream.Flush(true);
            }

            HostStateFileSecurity.RejectReparseTarget(_snapshotPath);
            File.Move(staged, _snapshotPath, true);
        }
        finally
        {
            if (File.Exists(staged) && !HostStateFileSecurity.IsReparsePoint(staged))
            {
                File.Delete(staged);
            }
        }
    }

    private static string Hash(long epoch, string previous, string stateHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{epoch}|{previous}|{stateHash}")));

    private static string StateHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record AnchorSnapshot(int Version, long Epoch, string StateHash, string Hash);
    private sealed record AnchorEntry(int Version, long Epoch, string PreviousHash, string StateHash, string Hash);
    public void Dispose() => _gate.Dispose();
}
