using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateFoundationTests
{
    [Fact] public async Task PlanAsync_BadSignature_RejectsBeforeStaging() => Assert.Contains("metadata_signature_invalid", (await CreateSut(new Provider(Metadata(false)), new Stager()).PlanAsync(Request(), default)).Reasons);

    [Fact]
    public async Task PlanAsync_CallerCannotSelectIncompleteTopology_UsesInstallationComponents()
    {
        HostUpdatePlanResult result = await CreateSut(new Provider(Metadata(components: Digests(("api/linux-x64", Digest("api"))))), new Stager()).PlanAsync(Request(), default);
        Assert.False(result.IsEligible);
        Assert.Contains("release_set_incomplete", result.Reasons);
    }

    [Fact]
    public async Task StageAsync_MetadataChangesUnderLock_RejectsWithoutStaging()
    {
        Provider provider = new(Metadata());
        Stager stager = new();
        HostUpdateFoundation sut = CreateSut(provider, stager);
        HostUpdatePlan plan = await PlanAsync(sut);
        provider.CurrentMetadata = Metadata(identity: Identity() with { ManifestDigest = Digest("different") });
        Assert.Equal("plan_or_metadata_changed", (await sut.StageAsync(plan, Approval(plan), default)).Code);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task StageAsync_InvalidReceipt_RecordsRecoverableFailureWithoutApplyReceipt()
    {
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata()), new Stager(validReceipt: false), journal);
        HostUpdatePlan plan = await PlanAsync(sut);
        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), default);
        Assert.True(result.IsRecoverableFailure);
        Assert.Null(result.Receipt);
        Assert.Equal(HostUpdateLifecycle.Failed, journal.Entries[^1].State);
    }

    [Fact]
    public async Task StageAsync_ExistingStagingIntent_RequiresOperatorWithoutReplay()
    {
        Stager stager = new();
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata()), stager, journal);
        HostUpdatePlan plan = await PlanAsync(sut);
        await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, Metadata()), default);
        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), default);
        Assert.True(result.RequiresOperator);
        Assert.Equal("staging_intent_unresolved", result.Code);
    }

    [Fact]
    public async Task StageAsync_CompletedIdempotency_ReturnsOriginalReceipt()
    {
        Stager stager = new();
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata()), stager, journal);
        HostUpdatePlan plan = await PlanAsync(sut);
        HostUpdateStageResult first = await sut.StageAsync(plan, Approval(plan), default);
        HostUpdateStageResult retry = await sut.StageAsync(plan, Approval(plan), default);
        Assert.True(retry.IsStaged);
        Assert.Equal(first.Receipt, retry.Receipt);
        Assert.Equal(1, stager.Calls);
    }

    [Fact]
    public async Task StageAsync_StandingPolicyChannelSwitch_Rejects()
    {
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata()), new Stager());
        HostUpdatePlan plan = await PlanAsync(sut);
        HostUpdateAuthorization authorization = Approval(plan) with { Kind = HostUpdateAuthorizationKind.StandingPolicy, SourceChannel = "insider", StandingPolicyActive = true };
        Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, authorization, default)).Code);
    }

    [Fact]
    public async Task FileJournal_BlankGapTruncationAndCorruption_FailClosed()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdatePlan plan = await PlanAsync(CreateSut(new Provider(Metadata()), new Stager()));
            await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, Metadata()), default);
            await File.AppendAllTextAsync(Path.Combine(directory, "host-update.journal.jsonl"), "\n", default);
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadAsync(default));
            await File.WriteAllTextAsync(Path.Combine(directory, "host-update.journal.jsonl"), "{\"revision\":2}", default);
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadAsync(default));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task FileJournal_MultipleInstances_AllocateMonotonicRevisions()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            HostUpdatePlan plan = await PlanAsync(CreateSut(new Provider(Metadata()), new Stager()));
            FileHostUpdateJournal first = new(directory);
            FileHostUpdateJournal second = new(directory);
            await Task.WhenAll(first.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, Metadata()), default), second.AppendAsync(HostUpdateJournalEntry.Approved(plan, Approval(plan)), default));
            Assert.Equal([1, 2], (await first.ReadAsync(default)).Select(entry => entry.Revision).Order());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static HostUpdateFoundation CreateSut(Provider provider, IHostUpdateStager stager, MemoryJournal? journal = null) => new(provider, new Compatibility(), stager, journal ?? new MemoryJournal(), new Lock());
    private static async Task<HostUpdatePlan> PlanAsync(HostUpdateFoundation sut) { HostUpdatePlanResult result = await sut.PlanAsync(Request(), default); Assert.True(result.IsEligible); return new(Request(), result.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(result.Identity), result.RequiredComponents); }
    private static HostUpdatePlanRequest Request() => new("operation-1", "key-1", "operator_1", "nonce-1", "operator_request", "stable", "stable", "policy-1", new("installation-1", Digest("installation"), Digest("source"), Digest("topology"), "linux-x64", new HashSet<string>(["api", "frontend"]), 1, 0, Digest("workers"), "postgres", Digest("schema"), Digest("config"), "1.0.0", Digest("resources"), 200, 100, 100, true, true, true));
    private static HostUpdateAuthorization Approval(HostUpdatePlan plan) => new("operator_1", "nonce-1", plan.InstallationId, plan.PlanHash, "stable", "stable", "policy-1", DateTimeOffset.UtcNow.AddMinutes(1), HostUpdateAuthorizationKind.Manual, false, false);
    private static SignedReleaseMetadata Metadata(bool signature = true, CanonicalReleaseIdentity? identity = null, IReadOnlyDictionary<string, string>? components = null) => new("stable", 1, signature, identity ?? Identity(), components ?? Digests(("api/linux-x64", Digest("api")), ("frontend/linux-x64", Digest("frontend"))));
    private static Dictionary<string, string> Digests(params (string Key, string Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value);
    private static CanonicalReleaseIdentity Identity() => new("release_1", "1.0.0", "v1.0.0", "main", Hash("commit"), Hash("commit"), "build_1", "release_1", "1.0.0", Digest("provenance"), Digest("manifest"), Digest("index"));
    private static string Digest(string value) => $"sha256:{Hash(value)}";
    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Provider(SignedReleaseMetadata metadata) : IHostUpdateMetadataProvider { public SignedReleaseMetadata CurrentMetadata { get; set; } = metadata; public Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct) => Task.FromResult(CurrentMetadata); }
    private sealed class Compatibility : IHostUpdateCompatibilityEvaluator { public IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata) => []; }
    private sealed class MemoryJournal : IHostUpdateJournal { public List<HostUpdateJournalEntry> Entries { get; } = []; public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) { Entries.Add(entry with { Revision = Entries.Count + 1 }); return Task.CompletedTask; } public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>(Entries); }
    private sealed class Lock : IHostUpdateInstallationLock { public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(new Lease()); private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; } }
    private sealed class Stager(bool validReceipt = true) : IHostUpdateStager { public int Calls { get; private set; } public Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) { Calls++; return Task.FromResult(new HostUpdateStagingReceipt(validReceipt, "staged", metadata.Identity.ManifestDigest, metadata.ComponentPlatformDigests, Digest("previous"), Digest("configuration"))); } }
}
