using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateFoundationTests
{
    [Fact]
    public async Task PlanAsync_BadSignature_RejectsBeforeStaging()
    {
        RecordingStager stager = new();
        HostUpdatePlanResult result = await CreateSut(new TestMetadataProvider(Metadata(signatureVerified: false)), stager).PlanAsync(Request(), CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Contains("metadata_signature_invalid", result.Reasons);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task PlanAsync_WrongPlatformDigest_RejectsCompleteSet()
    {
        SignedReleaseMetadata metadata = Metadata(components: new Dictionary<string, string> { ["api"] = Digest("api"), ["frontend"] = "not-a-digest" });
        HostUpdatePlanResult result = await CreateSut(new TestMetadataProvider(metadata), new RecordingStager()).PlanAsync(Request(), CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Contains("release_set_incomplete", result.Reasons);
    }

    [Fact]
    public async Task PlanAsync_MixedOciIdentity_RejectsEvenWithValidSignature()
    {
        CanonicalReleaseIdentity identity = Identity() with { OciVersionLabel = "2.0.0" };
        HostUpdatePlanResult result = await CreateSut(new TestMetadataProvider(Metadata(identity: identity)), new RecordingStager()).PlanAsync(Request(), CancellationToken.None);

        Assert.False(result.IsEligible);
        Assert.Contains("release_identity_invalid", result.Reasons);
    }

    [Fact]
    public async Task StageAsync_EqualVersionDifferentDigest_RejectsWithoutStaging()
    {
        TestMetadataProvider provider = new(Metadata());
        RecordingStager stager = new();
        HostUpdateFoundation sut = CreateSut(provider, stager);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        provider.CurrentMetadata = Metadata(identity: Identity(manifestDigest: Digest("different")));

        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), CancellationToken.None);

        Assert.Equal("plan_or_metadata_changed", result.Code);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task StageAsync_ChannelMismatch_RejectsWithoutStaging()
    {
        TestMetadataProvider provider = new(Metadata());
        RecordingStager stager = new();
        HostUpdateFoundation sut = CreateSut(provider, stager);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        provider.CurrentMetadata = Metadata(channel: "insider", identity: Identity(branch: "development"));

        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), CancellationToken.None);

        Assert.Equal("plan_or_metadata_changed", result.Code);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task StageAsync_ChannelPolicyDrift_InvalidatesAuthorizationWithoutStaging()
    {
        RecordingStager stager = new();
        HostUpdateFoundation sut = CreateSut(new TestMetadataProvider(Metadata()), stager);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        HostUpdateAuthorization changedPolicy = Approval(plan) with { ChannelPolicyRevision = "policy-2" };

        HostUpdateStageResult result = await sut.StageAsync(plan, changedPolicy, CancellationToken.None);

        Assert.Equal("authorization_invalid", result.Code);
        Assert.Equal(0, stager.Calls);
    }

    [Fact]
    public async Task StageAsync_StagingFailure_PreservesNoReceiptAndRecordsRedactedOutcome()
    {
        InMemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new TestMetadataProvider(Metadata()), new FailingStager(), journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);

        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), CancellationToken.None);
        HostUpdateJournalEntry failed = Assert.Single(journal.Entries, entry => entry.State == "Failed");

        Assert.True(result.IsRecoverableFailure);
        Assert.Null(result.StagedSetDigest);
        Assert.Null(result.RecoveryArtifactDigest);
        Assert.Equal("registry_unavailable", failed.Code);
        Assert.Equal("operator_1", failed.Snapshot.ActorId);
        Assert.DoesNotContain(":", failed.Snapshot.ReasonCode);
    }

    [Fact]
    public async Task StageAsync_CompleteStage_RecordsIntentBeforeReceiptAndPreservesRecoveryDigest()
    {
        InMemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new TestMetadataProvider(Metadata()), new RecordingStager(), journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);

        HostUpdateStageResult result = await sut.StageAsync(plan, Approval(plan), CancellationToken.None);
        HostUpdateJournalEntry staged = Assert.Single(journal.Entries, entry => entry.State == "Staged");

        Assert.True(result.IsStaged);
        Assert.Equal(["Planned", "Approved", "Staging", "Staged"], journal.Entries.Select(entry => entry.State));
        Assert.Equal(Digest("recovery"), staged.Snapshot.RecoveryArtifactDigest);
        Assert.Equal(Digest("set"), staged.Snapshot.StagedSetDigest);
    }

    [Fact]
    public async Task FileJournal_CorruptJournal_FailsClosedAfterRestart()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            await journal.AppendAsync(HostUpdateJournalEntry.StagingIntent(await EligiblePlanAsync(CreateSut(new TestMetadataProvider(Metadata()), new RecordingStager())), Metadata()), CancellationToken.None);
            await File.AppendAllTextAsync(Path.Combine(directory, "host-update.journal.jsonl"), "{not-json}" + Environment.NewLine);

            FileHostUpdateJournal reopened = new(directory);

            await Assert.ThrowsAsync<JsonException>(() => reopened.ReadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FileJournal_ReopenedAfterWrite_PreservesMonotonicRevision()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            HostUpdatePlan plan = await EligiblePlanAsync(CreateSut(new TestMetadataProvider(Metadata()), new RecordingStager()));
            FileHostUpdateJournal first = new(directory);
            await first.AppendAsync(HostUpdateJournalEntry.StagingIntent(plan, Metadata()), CancellationToken.None);
            FileHostUpdateJournal reopened = new(directory);
            await reopened.AppendAsync(HostUpdateJournalEntry.Staged(plan, HostUpdateStageResult.Staged(Digest("set"), Digest("recovery"))), CancellationToken.None);

            IReadOnlyList<HostUpdateJournalEntry> entries = await reopened.ReadAsync(CancellationToken.None);

            Assert.Equal([1, 2], entries.Select(entry => entry.Revision));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static HostUpdateFoundation CreateSut(TestMetadataProvider provider, IHostUpdateStager stager, InMemoryJournal? journal = null) =>
        new(provider, new TestCompatibilityEvaluator(), stager, journal ?? new InMemoryJournal(), new ProcessLock());

    private static async Task<HostUpdatePlan> EligiblePlanAsync(HostUpdateFoundation sut)
    {
        HostUpdatePlanResult result = await sut.PlanAsync(Request(), CancellationToken.None);
        Assert.True(result.IsEligible);
        return new HostUpdatePlan(Request(), result.PlanHash);
    }

    private static HostUpdatePlanRequest Request() =>
        new("operation-1", "key-1", "operator_1", "operator_request", "stable", "stable", "policy-1",
            new HashSet<string>(StringComparer.Ordinal) { "api", "frontend" },
            new HostInstallationEvidence("installation-1", Digest("installation"), Digest("source-target"), Digest("topology"), 1, 0, Digest("workers"), "postgres", Digest("schema"), Digest("configuration"), "1.0.0", Digest("resources"), 200, 100, 100, true, true, true));

    private static HostUpdateAuthorization Approval(HostUpdatePlan plan) =>
        new(plan.PlanHash, plan.Request.ChannelPolicyRevision, DateTimeOffset.UtcNow.AddMinutes(1), HostUpdateAuthorizationKind.Manual);

    private static SignedReleaseMetadata Metadata(bool signatureVerified = true, string channel = "stable", CanonicalReleaseIdentity? identity = null, IReadOnlyDictionary<string, string>? components = null) =>
        new(channel, 1, signatureVerified, identity ?? Identity(), components ?? new Dictionary<string, string> { ["api"] = Digest("api"), ["frontend"] = Digest("frontend") });

    private static CanonicalReleaseIdentity Identity(string branch = "main", string? manifestDigest = null) =>
        new("release_1", "1.0.0", "v1.0.0", branch, Hash("commit"), Hash("commit"), "build_1", "release_1", "1.0.0", Digest("provenance"), manifestDigest ?? Digest("manifest"), Digest("index"));

    private static string Digest(string value) => $"sha256:{Hash(value)}";
    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class TestMetadataProvider(SignedReleaseMetadata metadata) : IHostUpdateMetadataProvider
    {
        public SignedReleaseMetadata CurrentMetadata { get; set; } = metadata;
        public Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct) => Task.FromResult(CurrentMetadata);
    }

    private sealed class TestCompatibilityEvaluator : IHostUpdateCompatibilityEvaluator
    {
        public IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata) => [];
    }

    private sealed class InMemoryJournal : IHostUpdateJournal
    {
        public List<HostUpdateJournalEntry> Entries { get; } = [];
        public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct)
        {
            Entries.Add(entry with { Revision = Entries.Count + 1 });
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessLock : IHostUpdateInstallationLock
    {
        private int held;
        public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref held, 1) == 1)
            {
                throw new IOException("Installation lock is already held.");
            }

            return Task.FromResult<IAsyncDisposable>(new Release(this));
        }

        private sealed class Release(ProcessLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Volatile.Write(ref owner.held, 0);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingStager : IHostUpdateStager
    {
        public int Calls { get; private set; }
        public Task<HostUpdateStagingResult> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HostUpdateStagingResult(true, "staged", Digest("set"), Digest("recovery")));
        }
    }

    private sealed class FailingStager : IHostUpdateStager
    {
        public Task<HostUpdateStagingResult> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) =>
            Task.FromResult(new HostUpdateStagingResult(false, "registry_unavailable", null, null));
    }
}
