#pragma warning disable CA1512, CA1849, CA1859, CA1823, S1144, S3218, SA1107, SA1210, SA1501, SA1503, SA1513, SA1516, SA1518
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Durable host-local epoch anchor. It is tamper-evident, but not host-admin rollback proof without a protected key.</summary>
public interface IHostUpdateReplayAnchorProvisioner
{
    Task ProvisionAsync(CancellationToken ct);
}

public sealed class FileHostUpdateReplayAnchor : IHostUpdateReplayAnchor, IHostUpdateReplayAnchorProvisioner, IDisposable
{
    private const int Version = 1;
    private readonly string _snapshotPath;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HostStatePath _paths;

    public FileHostUpdateReplayAnchor(HostStatePath paths)
    {
        _paths = paths;
        _snapshotPath = paths.Resolve("replay-anchor.json");
        _journalPath = paths.Resolve("replay-anchor.journal");
    }

    public async Task<long> ReadEpochAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AnchorSnapshot snapshot = ReadSnapshot();
            IReadOnlyList<AnchorEntry> entries = ReadJournal();
            if (entries.Count == 0 || entries[^1].Epoch != snapshot.Epoch || !string.Equals(entries[^1].Hash, snapshot.Hash, StringComparison.Ordinal))
                throw new InvalidDataException("host_update_replay_anchor_inconsistent");
            return snapshot.Epoch;
        }
        finally { _gate.Release(); }
    }

    public async Task AdvanceEpochAsync(long epoch, CancellationToken ct)
    {
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AnchorSnapshot? prior = File.Exists(_snapshotPath) ? ReadSnapshot() : null;
            IReadOnlyList<AnchorEntry> entries = File.Exists(_journalPath) ? ReadJournal() : [];
            if (prior is null || entries.Count == 0)
                throw new InvalidDataException("host_update_replay_anchor_not_provisioned");
            if (prior is not null && (epoch <= prior.Epoch || entries[^1].Epoch != prior.Epoch || entries[^1].Hash != prior.Hash))
                throw new InvalidDataException("host_update_replay_anchor_rollback");
            if (prior is null && entries.Count != 0) throw new InvalidDataException("host_update_replay_anchor_inconsistent");
            string previous = prior?.Hash ?? string.Empty;
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{epoch}|{previous}")));
            AnchorEntry entry = new(Version, epoch, previous, hash);
            AppendDurably(_journalPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            WriteDurably(_snapshotPath, JsonSerializer.Serialize(new AnchorSnapshot(Version, epoch, hash)));
        }
        finally { _gate.Release(); }
    }

    public async Task ProvisionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(_snapshotPath) || File.Exists(_journalPath)) throw new InvalidOperationException("host_update_replay_anchor_already_provisioned");
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|0|")));
            AppendDurably(_journalPath, JsonSerializer.Serialize(new AnchorEntry(Version, 0, string.Empty, hash)) + Environment.NewLine);
            WriteDurably(_snapshotPath, JsonSerializer.Serialize(new AnchorSnapshot(Version, 0, hash)));
        }
        finally { _gate.Release(); }
    }

    private AnchorSnapshot ReadSnapshot()
    {
        try
        {
            AnchorSnapshot? value = JsonSerializer.Deserialize<AnchorSnapshot>(File.ReadAllText(_snapshotPath));
            if (value is null || value.Version != Version || value.Epoch < 0 || string.IsNullOrWhiteSpace(value.Hash)) throw new InvalidDataException("host_update_replay_anchor_invalid");
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidDataException("host_update_replay_anchor_invalid", ex); }
    }

    private IReadOnlyList<AnchorEntry> ReadJournal()
    {
        if (!File.Exists(_journalPath)) return [];
        List<AnchorEntry> result = [];
        string previous = string.Empty;
        foreach (string line in File.ReadLines(_journalPath))
        {
            AnchorEntry? entry;
            try { entry = JsonSerializer.Deserialize<AnchorEntry>(line); } catch (JsonException ex) { throw new InvalidDataException("host_update_replay_anchor_truncated", ex); }
            if (entry is null || entry.Version != Version || entry.Epoch < 0 || entry.PreviousHash != previous || entry.Hash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{entry.Epoch}|{entry.PreviousHash}")))) throw new InvalidDataException("host_update_replay_anchor_corrupt");
            result.Add(entry); previous = entry.Hash;
        }
        return result;
    }

    private static void AppendDurably(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content); writer.Flush(); stream.Flush(true);
    }

    private static void WriteDurably(string path, string content)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            using (FileStream stream = new(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed record AnchorSnapshot(int Version, long Epoch, string Hash);
    private sealed record AnchorEntry(int Version, long Epoch, string PreviousHash, string Hash);
    public void Dispose() => _gate.Dispose();
}

