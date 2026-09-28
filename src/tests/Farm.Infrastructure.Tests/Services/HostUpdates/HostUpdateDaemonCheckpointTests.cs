using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateDaemonCheckpointTests : IDisposable
{
    private readonly DirectoryInfo root = HostStateTestPaths.CreateTempSubdirectory("pf-daemon-checkpoints-");

    private string JournalPath => Path.Combine(root.FullName, "journal.ndjson");

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public void Read_ApiDownBeforeApproval_DefersWithoutCreatingJournal()
    {
        HostUpdateDaemonJournalSnapshot snapshot = Reader().Read();

        snapshot.Checkpoint.Should().Be(HostUpdateDaemonCheckpointStatus.AwaitApproval);
        File.Exists(JournalPath).Should().BeFalse();
    }

    [Theory]
    [InlineData(HostUpdateExecutionState.Accepted, "accepted")]
    [InlineData(HostUpdateExecutionState.Preflight, "preflight:before")]
    [InlineData(HostUpdateExecutionState.Preflight, "preflight:after")]
    public void Read_ApiDownBeforeOrDuringStaging_DefersWithoutReplayingOrExtendingApproval(
        HostUpdateExecutionState state, string phase)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(state, phase));
        byte[] before = File.ReadAllBytes(JournalPath);

        for (int restart = 0; restart < 3; restart++)
        {
            HostUpdateDaemonJournalSnapshot snapshot = Reader().Read();
            snapshot.Checkpoint!.Action.Should().Be(HostUpdateDaemonCheckpointAction.AwaitConfirmation);
            snapshot.Checkpoint.Code.Should().Be("confirmation_required");
        }

        File.ReadAllBytes(JournalPath).Should().Equal(before);
    }

    [Theory]
    [InlineData(HostUpdateExecutionState.Draining, "drain:before")]
    [InlineData(HostUpdateExecutionState.Fenced, "fence:before")]
    [InlineData(HostUpdateExecutionState.BackedUp, "backup:after")]
    [InlineData(HostUpdateExecutionState.Migrating, "migration:before")]
    [InlineData(HostUpdateExecutionState.Migrating, "migration:after")]
    [InlineData(HostUpdateExecutionState.Applying, "apply:before")]
    [InlineData(HostUpdateExecutionState.Applying, "apply:after")]
    [InlineData(HostUpdateExecutionState.Verifying, "verify:before")]
    public void Read_ApiDownDuringExecution_RecordsRecoverableStopOnce(
        HostUpdateExecutionState state, string phase)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.Accepted, "accepted"));
        journal.Append(Activity(state, phase));

        HostUpdateDaemonJournalSnapshot first = Reader().Read();
        first.Checkpoint!.Code.Should().Be("execution_interrupted");
        first.Checkpoint.RecoveryHint.Should().Be("host_local_recover");
        first.RecoveryRequiredCount.Should().Be(1);
        first.InFlightCount.Should().Be(0);
        IReadOnlyList<HostUpdateExecutionActivity> stopped = journal.Read(Request().ReleaseId);
        stopped.Should().HaveCount(3);
        stopped[^1].Phase.Should().Be("failure:daemon_interrupted");
        HostUpdateRecoveryRequestResolution resolution = HostUpdateRecoveryRequestResolver.Resolve(stopped, null);
        resolution.Succeeded.Should().BeTrue("the existing host-local recovery CLI must accept the stopped request");
        HostUpdateRequestBinding.Compute(resolution.Request!).Should().Be(HostUpdateRequestBinding.Compute(Request()));
        byte[] afterStop = File.ReadAllBytes(JournalPath);

        HostUpdateDaemonJournalSnapshot restarted = Reader().Read();

        restarted.Checkpoint!.Code.Should().Be("recovery_required");
        File.ReadAllBytes(JournalPath).Should().Equal(afterStop);
    }

    [Theory]
    [InlineData("recovery:started", "recovery_interrupted")]
    [InlineData("recovery:interrupted", "recovery_interrupted")]
    [InlineData("recovery:unknown", "recovery_interrupted")]
    [InlineData("recovery:needs_operator", "recovery_required")]
    public void Read_ApiDownDuringRecovery_NeverRetriesRecovery(string phase, string code)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.RecoveryRequired, phase));
        byte[] before = File.ReadAllBytes(JournalPath);

        HostUpdateDaemonJournalSnapshot snapshot = Reader().Read();

        snapshot.Checkpoint!.Code.Should().Be(code);
        snapshot.Checkpoint.RecoveryHint.Should().Be("host_local_recover");
        File.ReadAllBytes(JournalPath).Should().Equal(before);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("fence-release:after")]
    [InlineData("recovery:rolled_back")]
    public void Read_CompletedCheckpointAcrossRestarts_NeverReplaysIt(string phase)
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.Completed, phase));
        byte[] before = File.ReadAllBytes(JournalPath);

        Reader().Read().Checkpoint!.Action.Should().Be(HostUpdateDaemonCheckpointAction.Completed);
        Reader().Read().Checkpoint!.Action.Should().Be(HostUpdateDaemonCheckpointAction.Completed);

        File.ReadAllBytes(JournalPath).Should().Equal(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("release")]
    [InlineData("request")]
    public void Read_UnprovenBinding_RequiresOperatorWithoutRewritingEvidence(string mutation)
    {
        HostUpdateExecutionActivity activity = Activity(HostUpdateExecutionState.Applying, "apply:before");
        activity = mutation switch
        {
            "missing" => activity with { RequestBinding = null },
            "hash" => activity with { RequestBindingHash = "wrong" },
            "release" => activity with { ReleaseId = "stable:9.9.9" },
            _ => activity with { RequestBinding = Request() with { RequestId = "replacement" } },
        };
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(activity);
        byte[] before = File.ReadAllBytes(JournalPath);

        HostUpdateDaemonJournalSnapshot snapshot = Reader().Read();

        snapshot.Checkpoint!.Code.Should().Be("checkpoint_binding_unproven");
        snapshot.Checkpoint.RecoveryHint.Should().Be("host_local_status");
        File.ReadAllBytes(JournalPath).Should().Equal(before);
    }

    [Fact]
    public void Read_ExecutionLeaseHeld_DoesNotInspectOrChangeCheckpoints()
    {
        var journal = new Mock<IHostUpdateExecutionJournal>(MockBehavior.Strict);
        var executionLock = new FileHostUpdateExecutionLock(Path.Combine(root.FullName, FileHostUpdateExecutionLock.FileName));
        using IHostUpdateExecutionLease lease = executionLock.Acquire(TimeSpan.Zero, CancellationToken.None);

        new HostUpdateDaemonJournalReader(root.FullName, executionLock, journal.Object).Read()
            .Should().Be(HostUpdateDaemonJournalSnapshot.LockHeld);

        journal.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2000)]
    public void Read_ManyReleases_UsesOneSnapshotAndNoPerReleaseReads(int count)
    {
        var journal = new Mock<IHostUpdateExecutionJournal>(MockBehavior.Strict);
        HostUpdateExecutionActivity[] activities = Enumerable.Range(1, count)
            .Select(i => Activity(HostUpdateExecutionState.Completed, "completed") with { ReleaseId = $"stable:1.2.{i}" }).ToArray();
        journal.Setup(j => j.ReadAll()).Returns(activities);

        HostUpdateDaemonJournalSnapshot snapshot = Reader(journal.Object).Read();

        snapshot.ReleaseCount.Should().Be(count);
        journal.Verify(j => j.ReadAll(), Times.Once);
        journal.VerifyNoOtherCalls();
    }

    [Fact]
    public void Read_ManyInterruptedReleases_AppendsAtMostOneStopPerCycle()
    {
        var journal = new Mock<IHostUpdateExecutionJournal>(MockBehavior.Strict);
        HostUpdateExecutionActivity[] activities = Enumerable.Range(1, 100)
            .Select(i => Activity(HostUpdateExecutionState.Applying, "apply:before", Request() with { ReleaseId = $"stable:1.2.{i}" })).ToArray();
        journal.Setup(j => j.ReadAll()).Returns(activities);
        journal.Setup(j => j.Append(It.IsAny<HostUpdateExecutionActivity>()));

        Reader(journal.Object).Read();

        journal.Verify(j => j.ReadAll(), Times.Once);
        journal.Verify(j => j.Append(It.Is<HostUpdateExecutionActivity>(a => a.Phase == "failure:daemon_interrupted")), Times.Once);
        journal.VerifyNoOtherCalls();
    }

    [Fact]
    public void Read_StaleStagedRewrite_UsesOnlyCommittedCheckpoint()
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.Completed, "completed"));
        File.WriteAllText(JournalPath + ".staged", "uncommitted intent");

        Reader().Read().Checkpoint!.Action.Should().Be(HostUpdateDaemonCheckpointAction.Completed);

        File.Exists(JournalPath + ".staged").Should().BeFalse();
    }

    [Fact]
    public void Read_TamperedUnrelatedRelease_FailsTheWholeSnapshot()
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.Completed, "completed"));
        journal.Append(Activity(HostUpdateExecutionState.Accepted, "accepted", Request() with { ReleaseId = "stable:9.9.9" }));
        File.AppendAllText(JournalPath, "{invalid}\n");

        Reader().Read().Failed.Should().BeTrue();
    }

    [Fact]
    public void ToReport_ReconnectedAfterRecovery_EmitsOnlyCurrentRedactedState()
    {
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(HostUpdateExecutionState.RecoveryRequired, "recovery:started"));
        HostUpdateDaemonCheckpointStatus interrupted = Reader().Read().Checkpoint!;
        journal.Append(Activity(HostUpdateExecutionState.Completed, "recovery:rolled_back"));
        HostUpdateDaemonCheckpointStatus completed = Reader().Read().Checkpoint!;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        HostUpdateDaemonStatusReportDto report = completed.ToReport(1, now);

        interrupted.ToReport(1, now).DaemonState.Should().Be(HostUpdateDaemonState.NeedsOperator);
        report.DaemonState.Should().Be(HostUpdateDaemonState.Idle);
        report.RecoveryHints.Should().BeEmpty();
        report.ExecutionMode.Should().Be(HostUpdateDaemonExecutionMode.None);
        HostUpdateDaemonStatusReportValidator.Validate(report, now).Should().BeEmpty();
        string json = JsonSerializer.Serialize(report, HostUpdateDaemonJson.Options);
        json.Should().NotContain(root.FullName).And.NotContain("request-1").And.NotContain("root-1");
    }

    private HostUpdateDaemonJournalReader Reader(IHostUpdateExecutionJournal? journal = null) =>
        new(root.FullName,
            new FileHostUpdateExecutionLock(Path.Combine(root.FullName, FileHostUpdateExecutionLock.FileName)),
            journal ?? new FileHostUpdateExecutionJournal(JournalPath));

    private static HostUpdateExecutionActivity Activity(
        HostUpdateExecutionState state, string phase, HostUpdateExecutionRequest? request = null)
    {
        request ??= Request();
        return new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBinding = request,
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
        };
    }

    private static HostUpdateExecutionRequest Request() =>
        new("stable:1.2.3", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable,
            [.. HostUpdateExecutionRequest.RequiredServiceIds.Select(id => new HostUpdateExecutionTarget(id, "linux-amd64", "sha256:" + new string('c', 64)))])
        {
            RequestId = "request-1",
            TrustRoot = "root-1",
            PolicyRevision = 1,
            PolicyFingerprint = "policy-1",
            HostPlatform = "linux-amd64",
        };
}
