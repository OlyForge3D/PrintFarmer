using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateFoundationTests
{
    [Fact]
    public async Task PlanAsync_InvalidMetadataIdentity_DoesNotPoisonFollowingJournalEntry()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            Provider provider = new(Metadata() with { Identity = Identity() with { ReleaseId = "bad identity" } });
            HostUpdateFoundation sut = CreateSut(provider, journal: journal);
            Assert.False((await sut.PlanAsync(Request(), default)).IsEligible);
            provider.CurrentMetadata = Metadata();
            Assert.True((await sut.PlanAsync(Request() with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" }, default)).IsEligible);
            Assert.Null((await journal.ReadAsync(default))[0].Snapshot.Identity);
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
    public async Task PlanAsync_InspectorIdentityMismatch_RejectsCallerInstallationEvidence()
    {
        HostUpdateFoundation sut = CreateSut(inspector: new Inspector(Installation() with { InstallationId = "other-installation" }));
        Assert.Contains("installation_untrusted", (await sut.PlanAsync(Request(), default)).Reasons);
    }

    [Fact]
    public async Task PlanAsync_StaleInspectorTopology_DerivesComponentsAndRejectsMissingPlatformDigest()
    {
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata(components: Digests(("api/linux-x64", Digest("api"))))), inspector: new Inspector(Installation() with { RequiredComponents = new HashSet<string>(["api", "worker"]) }));
        Assert.Contains("release_set_incomplete", (await sut.PlanAsync(Request(), default)).Reasons);
    }

    [Fact]
    public async Task StageAsync_FreshInspectorEvidenceChangesUnderLock_RequiresOperator()
    {
        Inspector inspector = new(Installation());
        HostUpdateFoundation sut = CreateSut(inspector: inspector);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        inspector.Value = Installation() with { TopologyFingerprint = Digest("changed") };
        Assert.Equal("plan_or_metadata_changed", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
    }

    [Fact]
    public async Task StageAsync_ForgedOrUndefinedOrRevokedAuthorization_Rejects()
    {
        HostUpdateFoundation sut = CreateSut();
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, Authorization(plan) with { ActorId = "attacker" }, default)).Code);
        sut = CreateSut();
        plan = await EligiblePlanAsync(sut);
        Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, Authorization(plan) with { Kind = (HostUpdateAuthorizationKind)99 }, default)).Code);
        sut = CreateSut();
        plan = await EligiblePlanAsync(sut);
        Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, Authorization(plan) with { Kind = HostUpdateAuthorizationKind.StandingPolicy, StandingPolicyActive = true, StandingPolicyRevoked = true }, default)).Code);
    }

    [Fact]
    public async Task StageAsync_InvalidReceiptThenRetry_RequiresOperatorWithoutReplay()
    {
        Stager stager = new(false);
        HostUpdateFoundation sut = CreateSut(stager: stager);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        Assert.True((await sut.StageAsync(plan, Authorization(plan), default)).RequiresOperator);
        Assert.True((await sut.StageAsync(plan, Authorization(plan), default)).RequiresOperator);
        Assert.Equal(1, stager.Calls);
    }

    [Fact]
    public async Task PlanAsync_EqualDigestConflictAndMinimumUpdater_Rejects()
    {
        Assert.Contains("release_sequence_conflict", (await CreateSut(new Provider(Metadata() with { Identity = Identity() with { ManifestDigest = Digest("other") } })).PlanAsync(Request(), default)).Reasons);
        Assert.Contains("updater_too_old", (await CreateSut(new Provider(Metadata(minimumUpdater: "2.0.0"))).PlanAsync(Request(), default)).Reasons);
    }

    [Fact]
    public async Task PlanAsync_MalformedUpdaterVersions_RejectsWithoutThrowing()
    {
        Assert.Contains("updater_version_invalid", (await CreateSut(inspector: new Inspector(Installation() with { UpdaterVersion = "not-a-version" })).PlanAsync(Request(), default)).Reasons);
        Assert.Contains("release_identity_invalid", (await CreateSut(new Provider(Metadata(minimumUpdater: "not-a-version"))).PlanAsync(Request(), default)).Reasons);
    }

    [Fact]
    public async Task StageAsync_DowngradeWithoutExplicitAuthorization_Rejects()
    {
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata(sequence: 1)), inspector: new Inspector(Installation() with { CurrentSequence = 2 }));
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        Assert.Equal("downgrade_not_authorized", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
    }

    [Fact]
    public async Task FileJournal_InvalidLifecycleAndHostInspection_RejectsInvalidRecordAndReadsLocalSnapshot()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdatePlan plan = await EligiblePlanAsync(CreateSut());
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(HostUpdateJournalEntry.Staged(plan, Metadata(), HostUpdateStageResult.Staged(Receipt(Metadata()))), default));
            HostUpdatePlanResult planned = await CreateSut().PlanAsync(Request(), default);
            await journal.AppendAsync(HostUpdateJournalEntry.Planned(plan.Request, Metadata(), planned), default);
            HostUpdatePlan secondPlan = plan with { Request = plan.Request with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" } };
            await journal.AppendAsync(HostUpdateJournalEntry.Planned(secondPlan.Request, Metadata(), planned), default);
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(HostUpdateJournalEntry.Staged(plan, Metadata(), HostUpdateStageResult.Staged(Receipt(Metadata()))), default));
            Assert.Equal(2, (await HostUpdateJournalInspection.ReadSnapshotAsync(plan.InstallationId, directory, default)).Count);
            await Assert.ThrowsAsync<ArgumentException>(() => HostUpdateJournalInspection.ReadSnapshotAsync("../bad", directory, default));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static HostUpdateFoundation CreateSut(Provider? provider = null, Inspector? inspector = null, Stager? stager = null, IHostUpdateJournal? journal = null) =>
        new(inspector ?? new Inspector(Installation()), provider ?? new Provider(Metadata()), new Compatibility(), new AuthorizationEvaluator(), stager ?? new Stager(), journal ?? new MemoryJournal(), new Lock());
    private static async Task<HostUpdatePlan> EligiblePlanAsync(HostUpdateFoundation sut) { HostUpdatePlanResult result = await sut.PlanAsync(Request(), default); Assert.True(result.IsEligible); return new(Request(), result.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(result.Identity), result.RequiredComponents, Assert.IsType<HostInstallationEvidence>(result.Installation)); }
    private static HostUpdatePlanRequest Request() => new("operation-1", "key-1", "operator_1", "nonce-1", "operator_request", "stable", "stable", "policy-1", "installation-1");
    private static HostUpdateAuthorization Authorization(HostUpdatePlan plan) => new("operator_1", "nonce-1", plan.InstallationId, plan.PlanHash, "stable", "stable", "policy-1", DateTimeOffset.UtcNow.AddMinutes(1), HostUpdateAuthorizationKind.Manual, false, false, false, false);
    private static HostInstallationEvidence Installation() => new("installation-1", Digest("installation"), Digest("topology"), "linux-x64", new HashSet<string>(["api", "frontend"]), "postgres", Digest("schema"), Digest("config"), "1.0.0", 1, Identity(), Digest("previous"), 200, 100, true, true, true);
    private static SignedReleaseMetadata Metadata(long sequence = 1, IReadOnlyDictionary<string, string>? components = null, string minimumUpdater = "1.0.0") => new("stable", sequence, true, Identity(), components ?? Digests(("api/linux-x64", Digest("api")), ("frontend/linux-x64", Digest("frontend"))), minimumUpdater);
    private static HostUpdateStagingReceipt Receipt(SignedReleaseMetadata metadata) => new(true, "staged", metadata.Identity, metadata.Identity.ManifestDigest, metadata.ComponentPlatformDigests, Installation().PriorReleaseIdentity, Digest("previous"), Digest("config"));
    private static CanonicalReleaseIdentity Identity() => new("release_1", "1.0.0", "stable", "v1.0.0", "main", Hash("commit"), Hash("commit"), "build_1", "release_1", "1.0.0", Digest("provenance"), Digest("manifest"), Digest("index"));
    private static Dictionary<string, string> Digests(params (string Key, string Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value);
    private static string Digest(string value) => $"sha256:{Hash(value)}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed class Provider(SignedReleaseMetadata metadata) : IHostUpdateMetadataProvider { public SignedReleaseMetadata CurrentMetadata { get; set; } = metadata; public Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct) => Task.FromResult(CurrentMetadata); }
    private sealed class Inspector(HostInstallationEvidence value) : IHostUpdateInspector { public HostInstallationEvidence Value { get; set; } = value; public Task<HostInstallationEvidence> InspectAsync(string installationId, CancellationToken ct) => Task.FromResult(Value); }
    private sealed class Compatibility : IHostUpdateCompatibilityEvaluator { public IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata) => []; }
    private sealed class AuthorizationEvaluator : IHostUpdateAuthorizationEvaluator { public bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata) => authorization.IsStructurallyValidFor(plan); }
    private sealed class MemoryJournal : IHostUpdateJournal { public List<HostUpdateJournalEntry> Entries { get; } = []; public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) { Entries.Add(entry with { Revision = Entries.Count + 1 }); return Task.CompletedTask; } public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>(Entries); }
    private sealed class Lock : IHostUpdateInstallationLock { public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(new Lease()); private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; } }
    private sealed class Stager(bool valid = true) : IHostUpdateStager { public int Calls { get; private set; } public Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) { Calls++; return Task.FromResult(valid ? Receipt(metadata) : Receipt(metadata) with { ManifestDigest = Digest("wrong") }); } }
}
