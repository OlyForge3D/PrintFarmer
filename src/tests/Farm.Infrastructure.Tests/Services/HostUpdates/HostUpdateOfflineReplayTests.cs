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

    private static async Task<(FileHostUpdateReplayStore Store, string Root, InMemoryAnchor Anchor)> NewStoreAsync()
    {
        string root = Path.Combine(HostStateTestPaths.TempRoot, "printfarmer-offline-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string json = EmptyStateJson();
        await File.WriteAllTextAsync(Path.Combine(root, "host-update-replay.json"), json);
        InMemoryAnchor anchor = new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json))));
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

    private sealed class InMemoryAnchor(string initialHash) : IHostUpdateReplayAnchor
    {
        private long _epoch;
        private string _stateHash = initialHash;
        public Task<long> ReadEpochAsync(CancellationToken ct) => Task.FromResult(_epoch);
        public Task<string> ReadStateHashAsync(CancellationToken ct) => Task.FromResult(_stateHash);
        public Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct) { _epoch = epoch; _stateHash = stateHash; return Task.CompletedTask; }
    }
}
