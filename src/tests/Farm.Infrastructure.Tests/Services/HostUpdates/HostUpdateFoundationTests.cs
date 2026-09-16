using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateFoundationTests
{
    [Fact]
    public async Task PlanAsync_InvalidOrAbsentMetadataIdentityPersistsRedactedJournalEvidence()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            Provider provider = new(Metadata() with { Identity = null! });
            HostUpdateFoundation sut = CreateSut(provider, journal: journal);
            Assert.False((await sut.PlanAsync(Request(), default)).IsEligible);
            HostUpdateJournalEntry rejected = Assert.Single(await journal.ReadAsync(default));
            Assert.Null(rejected.Snapshot.Identity);
            Assert.Equal("redacted", rejected.Snapshot.Platform);
            Assert.Equal("sha256:0000000000000000000000000000000000000000000000000000000000000000", rejected.Snapshot.TopologyFingerprint);
            provider.CurrentMetadata = null!;
            Assert.False((await sut.PlanAsync(Request() with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" }, default)).IsEligible);
            Assert.Null((await journal.ReadAsync(default))[1].Snapshot.Identity);
            provider.CurrentMetadata = Metadata();
            Assert.True((await sut.PlanAsync(Request() with { OperationId = "operation-3", IdempotencyKey = "key-3", Nonce = "nonce-3" }, default)).IsEligible);
            Assert.Equal(3, (await journal.ReadAsync(default)).Count);
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
    public async Task PlanAsync_JournalFailure_ReturnsMeaningfulRejection()
    {
        Assert.Equal("journal_io_failure", (await CreateSut(journal: new ThrowingJournal(new IOException())).PlanAsync(Request(), default)).Reasons.Single());
        Assert.Equal("journal_unreconciled", (await CreateSut(journal: new ThrowingJournal(new InvalidDataException())).PlanAsync(Request(), default)).Reasons.Single());
    }

    [Fact]
    public async Task PlanAsync_SourceChannelDiffersFromTrustedPriorRelease_Rejects()
    {
        HostUpdatePlanResult result = await CreateSut(inspector: new Inspector(Installation() with { PriorReleaseIdentity = Identity() with { Channel = "insider" } })).PlanAsync(Request(), default);
        Assert.Contains("source_channel_mismatch", result.Reasons);
    }

    [Fact]
    public async Task PlanAsync_ExtraPlatformMetadata_ProjectsExactTopologySet()
    {
        SignedReleaseMetadata metadata = Metadata(components: Digests(
            ("api/linux-x64", Digest("api")),
            ("frontend/linux-x64", Digest("frontend")),
            ("extra/linux-arm64", Digest("extra"))));
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(new Provider(metadata), journal: journal);

        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        Assert.True((await sut.StageAsync(plan, Authorization(plan), default)).IsStaged);
        Assert.Equal(2, Assert.Single(journal.Entries, entry => entry.State == HostUpdateLifecycle.Staged).Snapshot.ComponentPlatformDigests.Count);
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
    public async Task StageAsync_EvaluatorCannotBypassMalformedStructuralAuthorization()
    {
        HostUpdateFoundation sut = CreateSut(authorizationEvaluator: new PermissiveAuthorizationEvaluator());
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, Authorization(plan) with { ActorId = "attacker" }, default)).Code);
    }

    [Fact]
    public async Task StageAsync_JournalIoFailure_RequiresOperatorInsteadOfReportingBusy()
    {
        HostUpdateFoundation sut = CreateSut(journal: new ThrowingJournal(new IOException()));
        HostUpdatePlan plan = await EligiblePlanAsync(CreateSut());
        Assert.Equal("journal_io_failure", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
    }

    [Theory]
    [InlineData(HostUpdateLifecycle.Staging)]
    [InlineData(HostUpdateLifecycle.NeedsOperator)]
    [InlineData(HostUpdateLifecycle.Staged)]
    public async Task StageAsync_OtherUnresolvedInstallationOperation_RequiresOperator(HostUpdateLifecycle state)
    {
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        HostUpdatePlanRequest other = Request() with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" };
        journal.Entries.Add(HostUpdateJournalEntry.Planned(other, Metadata(), HostUpdatePlanResult.Rejected("request_invalid")) with { State = state });

        Assert.Equal("installation_operation_unreconciled", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
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
    public async Task StageAsync_RebuildsTrustedJournalPlanAndRejectsMismatchedIdentityReuse()
    {
        Stager stager = new();
        HostUpdateFoundation sut = CreateSut(stager: stager);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        HostUpdatePlan forged = plan with { Request = plan.Request with { ActorId = "attacker" } };
        Assert.True((await sut.StageAsync(forged, Authorization(plan), default)).IsStaged);
        Assert.Equal("operator_1", stager.LastPlan!.Request.ActorId);
        Assert.Equal("operation_identity_conflict", (await sut.StageAsync(plan with { Request = plan.Request with { IdempotencyKey = "other-key" } }, Authorization(plan), default)).Code);
    }

    [Fact]
    public async Task StageAsync_PersistedReceiptMustMatchTrustedSnapshotAndExactDuplicateDoesNotRestage()
    {
        MemoryJournal journal = new();
        Stager stager = new();
        HostUpdateFoundation sut = CreateSut(stager: stager, journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        HostUpdateStageResult first = await sut.StageAsync(plan, Authorization(plan), default);
        HostUpdateStageResult duplicate = await sut.StageAsync(plan, Authorization(plan), default);
        Assert.Same(first.Receipt, duplicate.Receipt);
        Assert.Equal(1, stager.Calls);
        int stagedIndex = journal.Entries.FindIndex(entry => entry.State == HostUpdateLifecycle.Staged);
        journal.Entries[stagedIndex] = journal.Entries[stagedIndex] with
        {
            Snapshot = journal.Entries[stagedIndex].Snapshot with
            {
                Receipt = first.Receipt! with { ComponentPlatformDigests = Digests(("api/linux-x64", Digest("api"))) },
            },
        };
        Assert.Equal("staged_receipt_untrusted", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
    }

    [Fact]
    public async Task PlanAsync_NullNestedRuntimeEvidenceRejectsWithoutThrowing()
    {
        HostUpdateFoundation sut = CreateSut(inspector: new Inspector(Installation() with { RequiredComponents = null!, PriorReleaseIdentity = null! }));
        Assert.Contains("topology_invalid", (await sut.PlanAsync(Request(), default)).Reasons);
        Assert.Contains("installation_identity_invalid", (await sut.PlanAsync(Request() with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" }, default)).Reasons);
        Assert.False(new HostUpdatePlan(null!, Hash("plan"), Identity(), null!, null!).IsValid);
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
    public async Task PlanAsync_RejectsNonCanonicalReleaseIdentityCombinations()
    {
        CanonicalReleaseIdentity valid = Identity();
        CanonicalReleaseIdentity[] malformed =
        [
            valid with { Version = "1.0" },
            valid with { Version = "1.0.0-beta.1" },
            valid with { Version = "01.0.0" },
            valid with { SourceTag = "v1.0.1" },
            valid with { ReleaseId = "release_1", OciReleaseLabel = "release_1" },
            valid with { ReleaseId = "stable:1.0.1", OciReleaseLabel = "stable:1.0.1" },
        ];

        foreach (CanonicalReleaseIdentity identity in malformed)
        {
            HostUpdatePlanResult result = await CreateSut(new Provider(Metadata() with { Identity = identity })).PlanAsync(Request(), default);
            Assert.Contains("release_identity_invalid", result.Reasons);
        }

        foreach (string prereleaseLabel in new[] { "insider", "beta", "rc" })
        {
            CanonicalReleaseIdentity prerelease = Identity() with
            {
                Channel = "insider",
                Version = $"1.0.0-{prereleaseLabel}.1",
                ReleaseId = $"insider:1.0.0-{prereleaseLabel}.1",
                OciReleaseLabel = $"insider:1.0.0-{prereleaseLabel}.1",
                OciVersionLabel = $"1.0.0-{prereleaseLabel}.1",
                SourceTag = $"v1.0.0-{prereleaseLabel}.1",
                SourceBranch = "development",
            };
            HostUpdatePlanRequest request = Request() with { SourceChannel = "insider", TargetChannel = "insider" };

            Assert.True((await CreateSut(
                new Provider(Metadata() with { Channel = "insider", Identity = prerelease }),
                inspector: new Inspector(Installation() with { PriorReleaseIdentity = prerelease })).PlanAsync(request, default)).IsEligible);
            Assert.Contains("release_identity_invalid", (await CreateSut(new Provider(Metadata() with
            {
                Channel = "insider",
                Identity = prerelease with
                {
                    Version = $"1.0.0-{prereleaseLabel}.0",
                    ReleaseId = $"insider:1.0.0-{prereleaseLabel}.0",
                    OciReleaseLabel = $"insider:1.0.0-{prereleaseLabel}.0",
                    OciVersionLabel = $"1.0.0-{prereleaseLabel}.0",
                    SourceTag = $"v1.0.0-{prereleaseLabel}.0",
                },
            })).PlanAsync(request, default)).Reasons);
        }

        foreach (string channel in new[] { "beta", "rc" })
        {
            HostUpdatePlanRequest request = Request() with { SourceChannel = channel, TargetChannel = channel };
            Assert.False(request.IsValid);
            Assert.Contains("request_invalid", (await CreateSut().PlanAsync(request, default)).Reasons);
        }

        CanonicalReleaseIdentity insider = Identity() with
        {
            Channel = "insider",
            Version = "1.0.0-insider.1",
            ReleaseId = "insider:1.0.0-insider.1",
            OciReleaseLabel = "insider:1.0.0-insider.1",
            OciVersionLabel = "1.0.0-insider.1",
            SourceTag = "v1.0.0-insider.1",
            SourceBranch = "development",
        };
        HostUpdatePlanRequest insiderRequest = Request() with { SourceChannel = "insider", TargetChannel = "insider" };
        Assert.Contains("release_identity_invalid", (await CreateSut(new Provider(Metadata() with { Channel = "insider", Identity = insider with
        {
            Version = "1.0.0-insider.01",
            ReleaseId = "insider:1.0.0-insider.01",
            OciReleaseLabel = "insider:1.0.0-insider.01",
            OciVersionLabel = "1.0.0-insider.01",
            SourceTag = "v1.0.0-insider.01",
        } })).PlanAsync(insiderRequest, default)).Reasons);
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

    [Fact]
    public async Task FileHostUpdateInstallationLock_DisposedLease_ReleasesFileForNextAcquire()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateInstallationLock installationLock = new(directory);
            await using (await installationLock.AcquireAsync("installation-1", default))
            {
            }

            await using IAsyncDisposable lease = await installationLock.AcquireAsync("installation-1", default);
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
    public async Task FileHostUpdateJournal_EmptyFile_ReadsAsNoEntries()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "host-update.journal.jsonl"), string.Empty);
            Assert.Empty(await new FileHostUpdateJournal(directory).ReadAsync(default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileHostUpdateInspection_MissingLocalRoot_DoesNotCreateState()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(await HostUpdateJournalInspection.ReadSnapshotAsync("installation-1", directory, default));
            Assert.False(Directory.Exists(directory));
            Assert.Throws<ArgumentException>(() => new FileHostUpdateJournal("relative-state"));
            Assert.Throws<ArgumentException>(() => new FileHostUpdateInstallationLock(@"\\server\share"));
            Assert.Throws<ArgumentException>(() => new FileHostUpdateJournal(Path.Combine(directory, "..", "escaped-state")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileHostUpdateInstallationLock_CancelledBeforeAcquire_ThrowsCancellation()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileHostUpdateInstallationLock(directory).AcquireAsync("installation-1", cancellation.Token));
    }

    [Fact]
    public async Task FileHostUpdateInstallationLock_LeaseRemainsHeld_ThrowsAfterBoundedTimeout()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateInstallationLock installationLock = new(directory, TimeSpan.FromMilliseconds(25));
            await using (IAsyncDisposable lease = await installationLock.AcquireAsync("installation-1", default))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => installationLock.AcquireAsync("installation-1", default));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    public void HostUpdatePlanRequest_DangerousIdentifiers_AreInvalid(string identifier)
    {
        Assert.False((Request() with { OperationId = identifier }).IsValid);
    }

    private static HostUpdateFoundation CreateSut(Provider? provider = null, Inspector? inspector = null, Stager? stager = null, IHostUpdateJournal? journal = null, IHostUpdateAuthorizationEvaluator? authorizationEvaluator = null) =>
        new(inspector ?? new Inspector(Installation()), provider ?? new Provider(Metadata()), new Compatibility(), authorizationEvaluator ?? new AuthorizationEvaluator(), stager ?? new Stager(), journal ?? new MemoryJournal(), new Lock());
    private static async Task<HostUpdatePlan> EligiblePlanAsync(HostUpdateFoundation sut) { HostUpdatePlanResult result = await sut.PlanAsync(Request(), default); Assert.True(result.IsEligible); return new(Request(), result.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(result.Identity), result.RequiredComponents, Assert.IsType<HostInstallationEvidence>(result.Installation)); }
    private static HostUpdatePlanRequest Request() => new("operation-1", "key-1", "operator_1", "nonce-1", "operator_request", "stable", "stable", "policy-1", "installation-1");
    private static HostUpdateAuthorization Authorization(HostUpdatePlan plan) => new("operator_1", "nonce-1", plan.InstallationId, plan.PlanHash, "stable", "stable", "policy-1", DateTimeOffset.UtcNow.AddMinutes(1), HostUpdateAuthorizationKind.Manual, false, false, false, false);
    private static HostInstallationEvidence Installation() => new("installation-1", Digest("installation"), Digest("topology"), "linux-x64", new HashSet<string>(["api", "frontend"]), "postgres", Digest("schema"), Digest("config"), "1.0.0", 1, Identity(), Digest("previous"), 200, 100, true, true, true);
    private static SignedReleaseMetadata Metadata(long sequence = 1, IReadOnlyDictionary<string, string>? components = null, string minimumUpdater = "1.0.0") => new("stable", sequence, true, Identity(), components ?? Digests(("api/linux-x64", Digest("api")), ("frontend/linux-x64", Digest("frontend"))), minimumUpdater);
    private static HostUpdateStagingReceipt Receipt(SignedReleaseMetadata metadata) => new(true, "staged", metadata.Identity, metadata.Identity.ManifestDigest, metadata.ComponentPlatformDigests, Installation().PriorReleaseIdentity, Digest("previous"), Digest("config"));
    private static CanonicalReleaseIdentity Identity() => new("stable:1.0.0", "1.0.0", "stable", "v1.0.0", "main", Hash("commit"), Hash("commit"), "build_1", "stable:1.0.0", "1.0.0", Digest("provenance"), Digest("manifest"), Digest("index"));
    private static Dictionary<string, string> Digests(params (string Key, string Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value);
    private static string Digest(string value) => $"sha256:{Hash(value)}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed class Provider(SignedReleaseMetadata metadata) : IHostUpdateMetadataProvider { public SignedReleaseMetadata CurrentMetadata { get; set; } = metadata; public Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct) => Task.FromResult(CurrentMetadata); }
    private sealed class Inspector(HostInstallationEvidence value) : IHostUpdateInspector { public HostInstallationEvidence Value { get; set; } = value; public Task<HostInstallationEvidence> InspectAsync(string installationId, CancellationToken ct) => Task.FromResult(Value); }
    private sealed class Compatibility : IHostUpdateCompatibilityEvaluator { public IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata) => []; }
    private sealed class AuthorizationEvaluator : IHostUpdateAuthorizationEvaluator { public bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata) => authorization.IsStructurallyValidFor(plan); }
    private sealed class PermissiveAuthorizationEvaluator : IHostUpdateAuthorizationEvaluator { public bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata) => true; }
    private sealed class MemoryJournal : IHostUpdateJournal { public List<HostUpdateJournalEntry> Entries { get; } = []; public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) { Entries.Add(entry with { Revision = Entries.Count + 1 }); return Task.CompletedTask; } public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>(Entries); }
    private sealed class ThrowingJournal(Exception exception) : IHostUpdateJournal { public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) => Task.FromException(exception); public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<HostUpdateJournalEntry>>(exception); }
    private sealed class Lock : IHostUpdateInstallationLock { public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(new Lease()); private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; } }
    private sealed class Stager(bool valid = true) : IHostUpdateStager { public int Calls { get; private set; } public HostUpdatePlan? LastPlan { get; private set; } public Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) { Calls++; LastPlan = plan; return Task.FromResult(valid ? Receipt(metadata) : Receipt(metadata) with { ManifestDigest = Digest("wrong") }); } }
}
