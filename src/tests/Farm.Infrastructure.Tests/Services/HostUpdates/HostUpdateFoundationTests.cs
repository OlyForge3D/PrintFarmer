using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
            ("api/linux-amd64", Digest("api")),
            ("frontend/linux-amd64", Digest("frontend")),
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
        HostUpdateFoundation sut = CreateSut(new Provider(Metadata(components: Digests(("api/linux-amd64", Digest("api"))))), inspector: new Inspector(Installation() with { RequiredComponents = new HashSet<string>(["api", "worker"]) }));
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
    [InlineData(HostUpdateLifecycle.Approved)]
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
                Receipt = first.Receipt! with { ComponentPlatformDigests = Digests(("api/linux-amd64", Digest("api"))) },
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
    public async Task StageAsync_ApprovedLatestState_RequiresOperatorBeforeStaging()
    {
        MemoryJournal journal = new();
        Stager stager = new();
        HostUpdateFoundation sut = CreateSut(stager: stager, journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);
        journal.Entries.Add(HostUpdateJournalEntry.Approved(plan, Metadata(), Authorization(plan)) with { Revision = 2 });

        HostUpdateStageResult result = await sut.StageAsync(plan, Authorization(plan), default);

        Assert.Equal("operation_unreconciled", result.Code);
        Assert.Equal(0, stager.Calls);
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
            TimeSpan timeout = TimeSpan.FromMilliseconds(100);
            string lockPath = Path.Combine(directory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installation-1")))}.lock");
            Directory.CreateDirectory(directory);
            await using (FileStream heldLease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                FileHostUpdateInstallationLock installationLock = new(directory, timeout);
                Stopwatch stopwatch = Stopwatch.StartNew();
                await Assert.ThrowsAsync<HostUpdateInstallationBusyException>(() => installationLock.AcquireAsync("installation-1", default));
                Assert.True(stopwatch.Elapsed >= timeout);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileHostUpdateJournal_HeldLease_StopsAfterBoundedTimeout()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await using (FileStream heldLease = new(Path.Combine(directory, "host-update.journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                TimeSpan timeout = TimeSpan.FromMilliseconds(100);
                FileHostUpdateJournal journal = new(directory, timeout);

                Stopwatch stopwatch = Stopwatch.StartNew();
                await Assert.ThrowsAsync<IOException>(() => journal.ReadAsync(default));
                Assert.True(stopwatch.Elapsed >= timeout);
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

    [Fact]
    public void HostStateDirectory_AcceptsOnlyCurrentPlatformLocalGrammarAndRemainsRooted()
    {
        string localRoot = Path.GetPathRoot(Directory.GetCurrentDirectory())!;
        string accepted = OperatingSystem.IsWindows()
            ? Path.Combine(localRoot, "printfarmer-state")
            : "/var/lib/printfarmer-state";
        string foreign = OperatingSystem.IsWindows() ? "/var/lib/printfarmer-state" : @"C:\var\lib\printfarmer-state";

        Assert.True(HostUpdateValidation.IsHostLocalStateDirectory(accepted));
        Assert.True(Path.IsPathFullyQualified(Path.GetFullPath(accepted)));
        Assert.False(HostUpdateValidation.IsHostLocalStateDirectory(foreign));
        Assert.False(HostUpdateValidation.IsHostLocalStateDirectory(@"\\server\share"));
        Assert.False(HostUpdateValidation.IsHostLocalStateDirectory(@"\\?\C:\state"));
        Assert.False(HostUpdateValidation.IsHostLocalStateDirectory(@"\??\C:\state"));
        Assert.False(HostUpdateValidation.IsHostLocalStateDirectory(Path.Combine(accepted, "..", "escape")));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    public async Task PlanAsync_DiskCapacityBoundaries_AreValidated(long available, long required)
    {
        HostUpdatePlanResult result = await CreateSut(inspector: new Inspector(Installation() with { AvailableDiskBytes = available, RequiredDiskBytes = required }))
            .PlanAsync(Request(), default);

        Assert.Equal(available >= 0 && required > 0 && available >= required, result.IsEligible);
    }

    [Theory]
    [InlineData(false, "staging_failure")]
    [InlineData(true, "staging_io_failure")]
    public async Task StageAsync_StagerFailure_IsDurablyRecordedWithoutJournalMisclassification(bool ioFailure, string expectedCode)
    {
        MemoryJournal journal = new();
        HostUpdateFoundation sut = CreateSut(stager: new ThrowingStager(ioFailure), journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);

        HostUpdateStageResult result = await sut.StageAsync(plan, Authorization(plan), default);

        Assert.True(result.RequiresOperator);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedCode, journal.Entries[^1].Code);
    }

    [Fact]
    public async Task StageAsync_StagedJournalAppendFailure_RequiresJournalSpecificOperatorAction()
    {
        FailingStagedAppendJournal journal = new();
        HostUpdateFoundation sut = CreateSut(journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);

        Assert.Equal("journal_staged_append_failure", (await sut.StageAsync(plan, Authorization(plan), default)).Code);
    }

    [Fact]
    public async Task StageAsync_FailureJournalAppendFailure_RequiresJournalSpecificOperatorAction()
    {
        FailingFailedAppendJournal journal = new();
        HostUpdateFoundation sut = CreateSut(journal: journal);
        HostUpdatePlan plan = await EligiblePlanAsync(sut);

        Assert.Equal("journal_failure_append_failure", (await sut.StageAsync(plan, Authorization(plan) with { ActorId = "attacker" }, default)).Code);
    }

    [Fact]
    public async Task FileJournal_AuthorizationAudit_RoundTripsAcceptedAndRejectedOutcomes()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdateFoundation sut = CreateSut(journal: journal);
            HostUpdatePlan plan = await EligiblePlanAsync(sut);
            HostUpdateAuthorization manualInsider = Authorization(plan) with { TargetChannel = "insider", SourceChannel = "insider", InsiderWarningAcknowledged = true };
            Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, manualInsider, default)).Code);
            HostUpdateAuthorizationAudit rejected = Assert.IsType<HostUpdateAuthorizationAudit>((await new FileHostUpdateJournal(directory).ReadAsync(default))[^1].Snapshot.AuthorizationAudit);
            Assert.True(rejected.InsiderWarningAcknowledged);
            Assert.False(rejected.ExplicitDowngradeAllowed);
            Assert.Equal("manual", rejected.Kind);
            Assert.False(rejected.Accepted);
            Assert.Equal("operator_1", rejected.ActorId);
            Assert.Equal(plan.PlanHash, rejected.PlanHash);

            HostUpdatePlanRequest secondRequest = Request() with { OperationId = "operation-2", IdempotencyKey = "key-2", Nonce = "nonce-2" };
            HostUpdateFoundation secondSut = CreateSut(journal: journal);
            HostUpdatePlanResult secondResult = await secondSut.PlanAsync(secondRequest, default);
            HostUpdatePlan secondPlan = new(secondRequest, secondResult.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(secondResult.Identity),
                secondResult.RequiredComponents, Assert.IsType<HostInstallationEvidence>(secondResult.Installation));
            HostUpdateAuthorization standing = Authorization(secondPlan) with
            {
                Kind = HostUpdateAuthorizationKind.StandingPolicy, StandingPolicyActive = true, SourceChannel = "stable", TargetChannel = "stable",
            };
            Assert.True((await secondSut.StageAsync(secondPlan, standing, default)).IsStaged);
            HostUpdateAuthorizationAudit accepted = Assert.IsType<HostUpdateAuthorizationAudit>((await new FileHostUpdateJournal(directory).ReadAsync(default))
                .Last(entry => entry.State == HostUpdateLifecycle.Approved).Snapshot.AuthorizationAudit);
            Assert.Equal("standingpolicy", accepted.Kind);
            Assert.True(accepted.StandingPolicyActive);
            Assert.False(accepted.StandingPolicyRevoked);
            Assert.True(accepted.Accepted);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileJournal_DefaultExpirationRejectedAuthorizationPersistsNullableExpirationAudit()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdateFoundation sut = CreateSut(journal: journal);
            HostUpdatePlan plan = await EligiblePlanAsync(sut);

            Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, Authorization(plan) with { ExpiresAt = default }, default)).Code);

            HostUpdateJournalEntry entry = Assert.Single(await new FileHostUpdateJournal(directory).ReadAsync(default), candidate => candidate.Code == "authorization_invalid");
            HostUpdateAuthorizationAudit audit = Assert.IsType<HostUpdateAuthorizationAudit>(entry.Snapshot.AuthorizationAudit);
            Assert.False(audit.Accepted);
            Assert.Null(audit.ExpiresAt);
            Assert.Equal(plan.Request.ActorId, audit.ActorId);
            Assert.Equal(plan.Request.Nonce, audit.Nonce);
            Assert.Equal(plan.InstallationId, audit.InstallationId);
            Assert.Equal(plan.PlanHash, audit.PlanHash);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileJournal_ForgedAuthorizationAuditRoundTripsWithoutChangingTrustedPlanIdentity()
        {
            string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
            try
            {
                FileHostUpdateJournal journal = new(directory);
                HostUpdateFoundation sut = CreateSut(journal: journal);
                HostUpdatePlan plan = await EligiblePlanAsync(sut);
                HostUpdateAuthorization forged = Authorization(plan) with
                {
                    ActorId = "attacker", Nonce = "other-nonce", InstallationId = "other-installation", PlanHash = Hash("other"),
                    SourceChannel = "insider", TargetChannel = "insider", ChannelPolicyRevision = "../invalid",
                };

                Assert.Equal("authorization_invalid", (await sut.StageAsync(plan, forged, default)).Code);
                HostUpdateJournalEntry entry = (await new FileHostUpdateJournal(directory).ReadAsync(default))[^1];
                HostUpdateAuthorizationAudit audit = Assert.IsType<HostUpdateAuthorizationAudit>(entry.Snapshot.AuthorizationAudit);
                Assert.Equal(plan.Request.ActorId, entry.Snapshot.ActorId);
                Assert.Equal(plan.Request.Nonce, entry.Snapshot.Nonce);
                Assert.Equal(plan.InstallationId, entry.Snapshot.InstallationId);
                Assert.Equal("attacker", audit.ActorId);
                Assert.Equal("other-nonce", audit.Nonce);
                Assert.Equal("other-installation", audit.InstallationId);
                Assert.Equal(Hash("other"), audit.PlanHash);
                Assert.Equal("insider", audit.SourceChannel);
                Assert.Equal("insider", audit.TargetChannel);
                Assert.Equal("redacted", audit.ChannelPolicyRevision);
                Assert.False(audit.Accepted);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

    [Fact]
    public async Task FileJournal_JsonValidMutation_FailsIntegrityValidation()
        {
            string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
            try
            {
                FileHostUpdateJournal journal = new(directory);
                HostUpdatePlan plan = await EligiblePlanAsync(CreateSut());
                await journal.AppendAsync(HostUpdateJournalEntry.Planned(plan.Request, Metadata(), HostUpdatePlanResult.Rejected("request_invalid")), default);
                string path = Path.Combine(directory, "host-update.journal.jsonl");
                HostUpdateJournalEntry entry = JsonSerializer.Deserialize<HostUpdateJournalEntry>(await File.ReadAllTextAsync(path))!;
                entry = entry with { Code = "tampered" };
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry) + "\n");

                await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateJournal(directory).ReadAsync(default));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

    [Fact]
    public async Task FileJournal_ApprovedEntryRejectsInvalidAuthorizationAuditPolicy()
            {
                string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
                try
                {
                    FileHostUpdateJournal journal = new(directory);
                    HostUpdatePlan plan = await EligiblePlanAsync(CreateSut());
                    HostUpdateJournalEntry invalid = HostUpdateJournalEntry.Approved(plan, Metadata(), Authorization(plan)) with
                    {
                        Snapshot = HostUpdateJournalEntry.Approved(plan, Metadata(), Authorization(plan)).Snapshot with
                        {
                            AuthorizationAudit = HostUpdateJournalEntry.Approved(plan, Metadata(), Authorization(plan)).Snapshot.AuthorizationAudit! with
                            {
                                Kind = "invalid",
                            },
                        },
                    };

                    await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(invalid, default));
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }

    [Fact]
    public async Task FileJournal_ApprovedEntryRejectsMismatchedAcceptedAuthorizationAuditTuple()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdatePlan plan = await EligiblePlanAsync(CreateSut());
            HostUpdateJournalEntry approved = HostUpdateJournalEntry.Approved(plan, Metadata(), Authorization(plan));
            HostUpdateJournalEntry invalid = approved with
            {
                Snapshot = approved.Snapshot with
                {
                    AuthorizationAudit = approved.Snapshot.AuthorizationAudit! with { Nonce = "other-nonce" },
                },
            };

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(invalid, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FileJournal_ApprovedInsiderEntryRejectsUnacknowledgedAuthorizationAudit()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "host-update-test-artifacts", Guid.NewGuid().ToString("N"));
        try
        {
            FileHostUpdateJournal journal = new(directory);
            HostUpdatePlanRequest request = Request() with { SourceChannel = "insider", TargetChannel = "insider" };
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
            SignedReleaseMetadata metadata = Metadata() with { Channel = "insider", Identity = insider };
            HostUpdateFoundation sut = CreateSut(new Provider(metadata), new Inspector(Installation() with { PriorReleaseIdentity = insider }));
            HostUpdatePlanResult result = await sut.PlanAsync(request, default);
            Assert.True(result.IsEligible);
            HostUpdatePlan plan = new(request, result.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(result.Identity), result.RequiredComponents,
                Assert.IsType<HostInstallationEvidence>(result.Installation));
            HostUpdateJournalEntry approved = HostUpdateJournalEntry.Approved(plan, metadata,
                Authorization(plan) with { SourceChannel = "insider", TargetChannel = "insider", InsiderWarningAcknowledged = true });
            HostUpdateJournalEntry invalid = approved with
            {
                Snapshot = approved.Snapshot with
                {
                    AuthorizationAudit = approved.Snapshot.AuthorizationAudit! with { InsiderWarningAcknowledged = false },
                },
            };

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(invalid, default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static HostUpdateFoundation CreateSut(Provider? provider = null, Inspector? inspector = null, IHostUpdateStager? stager = null, IHostUpdateJournal? journal = null, IHostUpdateAuthorizationEvaluator? authorizationEvaluator = null) =>
        new(inspector ?? new Inspector(Installation()), provider ?? new Provider(Metadata()), new Compatibility(), authorizationEvaluator ?? new AuthorizationEvaluator(), stager ?? new Stager(), journal ?? new MemoryJournal(), new Lock());
    private static async Task<HostUpdatePlan> EligiblePlanAsync(HostUpdateFoundation sut) { HostUpdatePlanResult result = await sut.PlanAsync(Request(), default); Assert.True(result.IsEligible); return new(Request(), result.PlanHash, Assert.IsType<CanonicalReleaseIdentity>(result.Identity), result.RequiredComponents, Assert.IsType<HostInstallationEvidence>(result.Installation)); }
    private static HostUpdatePlanRequest Request() => new("operation-1", "key-1", "operator_1", "nonce-1", "operator_request", "stable", "stable", "policy-1", "installation-1");
    private static HostUpdateAuthorization Authorization(HostUpdatePlan plan) => new(plan.Request.ActorId, plan.Request.Nonce, plan.InstallationId, plan.PlanHash,
        plan.Request.SourceChannel, plan.TargetChannel, plan.Request.ChannelPolicyRevision, DateTimeOffset.UtcNow.AddMinutes(1), HostUpdateAuthorizationKind.Manual,
        false, false, false, false);
    private static HostInstallationEvidence Installation() => new("installation-1", Digest("installation"), Digest("topology"), "linux-amd64", new HashSet<string>(["api", "frontend"]), "postgres", Digest("schema"), Digest("config"), "1.0.0", 1, Identity(), Digest("previous"), 200, 100, true, true, true);
    private static SignedReleaseMetadata Metadata(long sequence = 1, IReadOnlyDictionary<string, string>? components = null, string minimumUpdater = "1.0.0") => new("stable", sequence, true, Identity(), components ?? Digests(("api/linux-amd64", Digest("api")), ("frontend/linux-amd64", Digest("frontend"))), minimumUpdater);
    private static HostUpdateStagingReceipt Receipt(SignedReleaseMetadata metadata) => new(true, "staged", metadata.Identity, metadata.Identity.ManifestDigest, metadata.ComponentPlatformDigests, Installation().PriorReleaseIdentity, Digest("previous"), Digest("config"));
    private static CanonicalReleaseIdentity Identity() => new("stable:1.0.0", "1.0.0", "stable", "v1.0.0", "main", Hash("commit"), Hash("commit"), "build_1", "stable:1.0.0", "1.0.0", Digest("manifest"));
    private static Dictionary<string, string> Digests(params (string Key, string Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value);
    private static string Digest(string value) => $"sha256:{Hash(value)}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed class Provider(SignedReleaseMetadata metadata) : IHostUpdateMetadataProvider { public SignedReleaseMetadata CurrentMetadata { get; set; } = metadata; public Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct) => Task.FromResult(CurrentMetadata); }
    private sealed class Inspector(HostInstallationEvidence value) : IHostUpdateInspector { public HostInstallationEvidence Value { get; set; } = value; public Task<HostInstallationEvidence> InspectAsync(string installationId, CancellationToken ct) => Task.FromResult(Value); }
    private sealed class Compatibility : IHostUpdateCompatibilityEvaluator { public IReadOnlyList<string> Evaluate(HostInstallationEvidence installation, SignedReleaseMetadata metadata) => []; }
    private sealed class AuthorizationEvaluator : IHostUpdateAuthorizationEvaluator { public bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata) => authorization.IsStructurallyValidFor(plan); }
    private sealed class PermissiveAuthorizationEvaluator : IHostUpdateAuthorizationEvaluator { public bool IsAuthorized(HostUpdateAuthorization authorization, HostUpdatePlan plan, HostInstallationEvidence installation, SignedReleaseMetadata metadata) => true; }
    private sealed class MemoryJournal : IHostUpdateJournal { public List<HostUpdateJournalEntry> Entries { get; } = []; public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) { Entries.Add(entry with { Revision = Entries.Count + 1 }); return Task.CompletedTask; } public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUpdateJournalEntry>>(Entries); }
    private sealed class FailingStagedAppendJournal : IHostUpdateJournal { private readonly MemoryJournal inner = new(); public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct); public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) => entry.State == HostUpdateLifecycle.Staged ? Task.FromException(new IOException()) : inner.AppendAsync(entry, ct); }
    private sealed class FailingFailedAppendJournal : IHostUpdateJournal { private readonly MemoryJournal inner = new(); public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct); public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) => entry.State == HostUpdateLifecycle.Failed ? Task.FromException(new IOException()) : inner.AppendAsync(entry, ct); }
    private sealed class ThrowingJournal(Exception exception) : IHostUpdateJournal { public Task AppendAsync(HostUpdateJournalEntry entry, CancellationToken ct) => Task.FromException(exception); public Task<IReadOnlyList<HostUpdateJournalEntry>> ReadAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<HostUpdateJournalEntry>>(exception); }
    private sealed class Lock : IHostUpdateInstallationLock { public Task<IAsyncDisposable> AcquireAsync(string installationId, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(new Lease()); private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; } }
    private sealed class Stager(bool valid = true) : IHostUpdateStager { public int Calls { get; private set; } public HostUpdatePlan? LastPlan { get; private set; } public Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) { Calls++; LastPlan = plan; return Task.FromResult(valid ? Receipt(metadata) : Receipt(metadata) with { ManifestDigest = Digest("wrong") }); } }
    private sealed class ThrowingStager(bool ioFailure) : IHostUpdateStager { public Task<HostUpdateStagingReceipt> StageAsync(HostUpdatePlan plan, SignedReleaseMetadata metadata, CancellationToken ct) => Task.FromException<HostUpdateStagingReceipt>(ioFailure ? new IOException() : new InvalidOperationException()); }
}
