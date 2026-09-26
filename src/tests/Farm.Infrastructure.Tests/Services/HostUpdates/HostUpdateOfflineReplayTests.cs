using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateOfflineReplayTests
{
    [Fact]
    public async Task Import_FreshCandidate_RecordsImportedAndAdvancesHighWater()
    {
        (FileHostUpdateReplayStore store, _, _) = await NewStoreAsync();

        HostUpdateReplayDecision imported = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision older = await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Imported, imported.Disposition);
        Assert.False(imported.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, older.Disposition);
    }

    [Fact]
    public async Task Import_SameIdentityTwice_ReusesImportedDecision()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        HostUpdateReplayDecision first = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        HostUpdateReplayDecision again = await new FileHostUpdateReplayStore(root, anchor).DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Imported, again.Disposition);
        Assert.True(again.Reused);
        Assert.Equal(first.CorrelationId, again.CorrelationId);
    }

    [Fact]
    public async Task Import_After42Accepted_Refuses41AndEqualSequenceDifferentIdentityAcrossRestart()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Admit, default)).Disposition);

        FileHostUpdateReplayStore restarted = new(root, anchor);
        HostUpdateReplayDecision downgrade = await restarted.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision substitute = await restarted.DecideAsync(Candidate(42) with { ManifestDigest = "sha256:other" }, HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision sameAccepted = await restarted.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, downgrade.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, substitute.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, sameAccepted.Disposition);
        Assert.True(sameAccepted.Reused);
    }

    [Fact]
    public async Task Import42_ThenOnline41_IsRejectedAndOnline42MayStillBeAdmittedOnce()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        FileHostUpdateReplayStore online = new(root, anchor);
        HostUpdateReplayDecision online41 = await online.DecideAsync(Candidate(41), HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision reserve42 = await online.DecideAsync(Candidate(42), HostUpdateReplayIntent.Reserve, default);
        HostUpdateReplayDecision admit42 = await online.DecideAsync(Candidate(42), HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision admit42Again = await online.DecideAsync(Candidate(42), HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision reimport42 = await online.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, online41.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, reserve42.Disposition);
        Assert.False(reserve42.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, admit42.Disposition);
        Assert.False(admit42.Reused);
        Assert.True(admit42Again.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, reimport42.Disposition);
        Assert.True(reimport42.Reused);
    }

    [Fact]
    public async Task Import42_SupersedesImported41_AndOnlineAdmitSupersedesImported()
    {
        (FileHostUpdateReplayStore store, _, _) = await NewStoreAsync();
        await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, (await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default)).Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, (await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Admit, default)).Disposition);

        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(Candidate(43), HostUpdateReplayIntent.Admit, default)).Disposition);
        HostUpdateReplayDecision imported42 = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, imported42.Disposition);
        Assert.True(imported42.Reused);
    }

    [Fact]
    public async Task Import_RejectedIdentity_StaysRejectedAcrossRoundTrip()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Reject, default);

        HostUpdateReplayDecision imported = await new FileHostUpdateReplayStore(root, anchor).DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, imported.Disposition);
        Assert.True(imported.Reused);
    }

    [Fact]
    public async Task Import_ThenOnlineTerminalReject_KeepsImportedIdentity()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        HostUpdateReplayDecision reject = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Reject, default);
        HostUpdateReplayDecision admit = await new FileHostUpdateReplayStore(root, anchor).DecideAsync(Candidate(42), HostUpdateReplayIntent.Admit, default);

        Assert.Equal(HostUpdateReplayDisposition.Imported, reject.Disposition);
        Assert.True(reject.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, admit.Disposition);
        Assert.False(admit.Reused);
    }

    [Fact]
    public async Task Import_ChannelsAreIndependent()
    {
        (FileHostUpdateReplayStore store, _, _) = await NewStoreAsync();
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        HostUpdateReplayDecision insider = await store.DecideAsync(Candidate(1, UpdateChannelSettings.InsiderChannel), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Imported, insider.Disposition);
    }

    [Fact]
    public async Task Import_UnauthenticatedCandidate_IsRejectedWithoutPersisting()
    {
        (FileHostUpdateReplayStore store, _, _) = await NewStoreAsync();

        HostUpdateReplayDecision fake = await store.DecideAsync(Candidate(99) with { CryptographicallyVerified = false }, HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision real = await store.DecideAsync(Candidate(1), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, fake.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Imported, real.Disposition);
    }

    [Fact]
    public async Task ConcurrentStores_SharingOneRoot_SerializeCommitsWithoutLosingStageOrAnchor()
    {
        // Separate store instances model the API scheduler and offline-admit CLI processes: each has
        // its own in-process gate, so only the cross-process lock file can serialize them (#3077).
        (_, string root, InMemoryAnchor anchor) = await NewStoreAsync(TimeSpan.FromMilliseconds(5));
        using FileHostUpdateReplayStore scheduler = new(root, anchor);
        using FileHostUpdateReplayStore cli = new(root, anchor);

        HostUpdateReplayDecision[] decisions = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            (i % 2 == 0 ? scheduler : cli).DecideAsync(
                Candidate(i, i % 2 == 0 ? UpdateChannelSettings.StableChannel : UpdateChannelSettings.InsiderChannel),
                i % 2 == 0 ? HostUpdateReplayIntent.Admit : HostUpdateReplayIntent.Import, default))));

        Assert.All(decisions, decision => Assert.False(decision.Reused));
        Assert.False(File.Exists(Path.Combine(root, "host-update-replay.json.staged")));
        using FileHostUpdateReplayStore restarted = new(root, anchor);
        HostUpdateReplayDecision replay = await restarted.DecideAsync(Candidate(20), HostUpdateReplayIntent.Admit, default);
        Assert.True(replay.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, replay.Disposition);
    }

    [Fact]
    public async Task HeldCrossProcessLock_FailsClosedWithoutTouchingState_ThenProceedsOnceReleased()
    {
        (_, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        string snapshot = await File.ReadAllTextAsync(Path.Combine(root, "host-update-replay.json"));
        using FileHostUpdateReplayStore store = new(root, anchor, crossProcessLockTimeout: TimeSpan.FromMilliseconds(200));

        using (new FileStream(Path.Combine(root, "host-update-replay.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            InvalidDataException busy = await Assert.ThrowsAsync<InvalidDataException>(() => store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default));
            Assert.Equal("host_update_replay_lock_unavailable", busy.Message);
        }

        Assert.Equal(snapshot, await File.ReadAllTextAsync(Path.Combine(root, "host-update-replay.json")));
        Assert.Equal(0, await anchor.ReadEpochAsync(default));
        Assert.Equal(HostUpdateReplayDisposition.Imported, (await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default)).Disposition);
    }

    [Fact]
    public async Task Seq41RejectedAboveInstalled_Then42ImportedWithoutInstall_StayReplayProtectedAcrossChannelRoundTripsAndRestart()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(Candidate(40), HostUpdateReplayIntent.Admit, default)).Disposition);
        HostUpdateReplayDecision rejected41 = await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Reject, default);
        HostUpdateReplayDecision imported42 = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, rejected41.Disposition);
        Assert.False(rejected41.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Imported, imported42.Disposition);

        // Stable -> insider -> stable, then insider again after a restart: the independent channel
        // accepts its own equal sequence while the stable decisions never move.
        Assert.Equal(HostUpdateReplayDisposition.Imported,
            (await store.DecideAsync(Candidate(41, UpdateChannelSettings.InsiderChannel), HostUpdateReplayIntent.Import, default)).Disposition);
        await AssertStableReplayProtectedAsync(store, imported42.CorrelationId);
        using FileHostUpdateReplayStore restarted = new(root, anchor);
        HostUpdateReplayDecision insiderAgain = await restarted.DecideAsync(Candidate(41, UpdateChannelSettings.InsiderChannel), HostUpdateReplayIntent.Import, default);
        Assert.Equal(HostUpdateReplayDisposition.Imported, insiderAgain.Disposition);
        Assert.True(insiderAgain.Reused);
        await AssertStableReplayProtectedAsync(restarted, imported42.CorrelationId);

        HostUpdateReplayDecision admit42 = await restarted.DecideAsync(Candidate(42), HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, admit42.Disposition);
        Assert.False(admit42.Reused);
    }

    [Fact]
    public async Task MovedAliasOrForgedPromotion_AtTheImportedSequence_IsRejectedWithoutDisturbingTheImport()
    {
        (FileHostUpdateReplayStore store, _, _) = await NewStoreAsync();
        HostUpdateReplayDecision imported = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        // Same release id and sequence re-pointed at another commit/manifest (a moved tag or a
        // promoted build from another branch) is a different identity at the high-water sequence.
        HostUpdateReplayDecision moved = await store.DecideAsync(
            Candidate(42) with { SourceCommit = "commit-other-branch", ManifestDigest = "sha256:moved" }, HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision original = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, moved.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Imported, original.Disposition);
        Assert.True(original.Reused);
        Assert.Equal(imported.CorrelationId, original.CorrelationId);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, (await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default)).Disposition);
    }

    [Fact]
    public async Task RestoredOlderReplaySnapshot_FailsClosedUntilTheCommittedStateReturns()
    {
        (FileHostUpdateReplayStore store, string root, _) = await NewStoreAsync();
        string statePath = Path.Combine(root, "host-update-replay.json");
        await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);
        byte[] older = await File.ReadAllBytesAsync(statePath);
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);
        byte[] committed = await File.ReadAllBytesAsync(statePath);

        await File.WriteAllBytesAsync(statePath, older);
        InvalidDataException rollback = await Assert.ThrowsAsync<InvalidDataException>(() => store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default));

        Assert.Equal("host_update_replay_state_rollback", rollback.Message);
        Assert.Equal(older, await File.ReadAllBytesAsync(statePath));
        await File.WriteAllBytesAsync(statePath, committed);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, (await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default)).Disposition);
    }

    [Fact]
    public async Task RestoredOlderAnchor_EditedSnapshot_OrForeignSameEpochSnapshot_FailsClosed()
    {
        (FileHostUpdateReplayStore store, string root, InMemoryAnchor anchor) = await NewStoreAsync();
        string statePath = Path.Combine(root, "host-update-replay.json");
        string initialHash = await anchor.ReadStateHashAsync(default);
        await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        using FileHostUpdateReplayStore restoredAnchor = new(root, new InMemoryAnchor(initialHash));
        InvalidDataException anchorRollback = await Assert.ThrowsAsync<InvalidDataException>(() =>
            restoredAnchor.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default));
        Assert.Equal("host_update_replay_anchor_rollback", anchorRollback.Message);

        string current = await File.ReadAllTextAsync(statePath);
        string edited = current.Replace("\"Sequence\":42", "\"Sequence\":40", StringComparison.Ordinal);
        Assert.NotEqual(current, edited);
        await File.WriteAllTextAsync(statePath, edited);
        InvalidDataException corrupt = await Assert.ThrowsAsync<InvalidDataException>(() => store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default));
        Assert.Equal("host_update_replay_state_corrupt", corrupt.Message);

        // A well-formed snapshot from another backup at the same epoch still fails the anchor hash.
        (FileHostUpdateReplayStore other, string otherRoot, _) = await NewStoreAsync();
        await other.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);
        File.Copy(Path.Combine(otherRoot, "host-update-replay.json"), statePath, true);
        InvalidDataException mismatch = await Assert.ThrowsAsync<InvalidDataException>(() => store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default));
        Assert.Equal("host_update_replay_state_anchor_hash_mismatch", mismatch.Message);
    }

    private static async Task AssertStableReplayProtectedAsync(FileHostUpdateReplayStore store, string imported42CorrelationId)
    {
        HostUpdateReplayDecision import41 = await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Import, default);
        HostUpdateReplayDecision admit41 = await store.DecideAsync(Candidate(41), HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision admit40 = await store.DecideAsync(Candidate(40), HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision import42 = await store.DecideAsync(Candidate(42), HostUpdateReplayIntent.Import, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, import41.Disposition);
        Assert.True(import41.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, admit41.Disposition);
        Assert.True(admit41.Reused);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, admit40.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Imported, import42.Disposition);
        Assert.True(import42.Reused);
        Assert.Equal(imported42CorrelationId, import42.CorrelationId);
    }

    private static async Task<(FileHostUpdateReplayStore Store, string Root, InMemoryAnchor Anchor)> NewStoreAsync(TimeSpan? anchorDelay = null)
    {
        string root = Path.Combine(HostStateTestPaths.TempRoot, "printfarmer-offline-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string json = EmptyStateJson();
        await File.WriteAllTextAsync(Path.Combine(root, "host-update-replay.json"), json);
        InMemoryAnchor anchor = new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))), anchorDelay ?? TimeSpan.Zero);
        return (new FileHostUpdateReplayStore(root, anchor), root, anchor);
    }

    private static string EmptyStateJson()
    {
        string checksum = HostUpdateCanonical.Hash(new
        {
            Version = 1,
            Epoch = 0L,
            HighWater = Array.Empty<object>(),
            Identities = Array.Empty<object>(),
        });
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            Epoch = 0L,
            Checksum = checksum,
            HighWaterByNamespace = new Dictionary<string, object>(),
            Identities = new Dictionary<string, object>(),
        });
    }

    private static VerifiedHostUpdateCandidate Candidate(long sequence, string channel = UpdateChannelSettings.StableChannel) =>
        new(channel + ":1.0." + sequence, "commit-" + sequence, sequence, "sha256:manifest-" + sequence, channel, true, true, true, true, true, true,
            new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), "sha256:" + new string('d', 64), "sha256:" + new string('e', 64), "sha256:" + new string('f', 64)));

    private sealed class InMemoryAnchor(string initialHash, TimeSpan delay = default) : IHostUpdateReplayAnchor
    {
        private long _epoch;
        private string _stateHash = initialHash;
        public Task<long> ReadEpochAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref _epoch));
        public Task<string> ReadStateHashAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref _stateHash));
        public async Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct)
        {
            // A non-zero delay widens the stage-to-promote window a racing writer could exploit.
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
            Volatile.Write(ref _stateHash, stateHash);
            Volatile.Write(ref _epoch, epoch);
        }
    }
}
