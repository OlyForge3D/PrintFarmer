using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.HostUpdates.PullApproval;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #3119: end-to-end proof that the enrolled host-update daemon fails closed across stale
/// approval, revoked identity, API partition and host/service restart, and resumes durable
/// checkpoints idempotently. Each test composes the real daemon core, pull-contract verifiers,
/// signed-release verifier, executor, execution lock and hash-chained journals over one host
/// state directory. A host restart is a fresh object graph over the same directory; nothing is
/// carried in memory across it. The only execution gate that ever opens is a test double used
/// to drive the real executor into a partial state: every daemon boot uses the production gate.
/// </summary>
public sealed class HostUpdateDaemonReconciliationProofTests : IDisposable
{
    private const string ReleaseId = "insider:1.2.3-insider.42";
    private const string RequestId = "request-proof-0001";
    private const string ApprovalId = "approval-proof-0001";

    private static readonly DateTimeOffset Start = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostUpdateSchedulerSettings Policy =
        HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(new HostUpdateAutomationPolicy());

    private readonly DirectoryInfo root = HostStateTestPaths.CreateTempSubdirectory("pf-daemon-proof-");
    private readonly MutableTime time = new(Start);
    private readonly RecordingSignatureVerifier signature = new();
    private readonly FakeReleaseSource source;
    private readonly byte[] manifest;
    private readonly string manifestDigest;
    private readonly string trustedRootPath;
    private readonly string cosignPath;
    private readonly List<HostUpdateDaemonStatus> published = [];
    private readonly List<string> logged = [];

    public HostUpdateDaemonReconciliationProofTests()
    {
        Directory.CreateDirectory(StateDirectory);
        manifest = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "scripts", "ci", "fixtures", "update-manifest.golden.json"));
        manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifest));
        trustedRootPath = Path.Combine(root.FullName, "trusted_root.json");
        File.WriteAllText(trustedRootPath, TrustedRoot());
        cosignPath = Path.Combine(root.FullName, "cosign");
        source = new FakeReleaseSource(new HostUpdateDaemonSignedArtifacts(manifest, "{\"bundle\":true}"u8.ToArray()));
    }

    private string StateDirectory => Path.Combine(root.FullName, "state");

    private string JournalPath => Path.Combine(StateDirectory, "journal.ndjson");

    private string LockPath => Path.Combine(StateDirectory, FileHostUpdateExecutionLock.FileName);

    private string VerificationPath => Path.Combine(StateDirectory, FileHostUpdateDaemonVerificationJournal.FileName);

    public void Dispose()
    {
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ── Disabled by default ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DisabledByDefault_OnlyProductionGateExists_AndRuntimeAutoUpdateIsHardOff()
    {
        Type[] gates = [.. typeof(IHostUpdateDaemonExecutionGate).Assembly.GetTypes()
            .Where(t => typeof(IHostUpdateDaemonExecutionGate).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })];

        gates.Should().Equal(typeof(DisabledHostUpdateDaemonExecutionGate));
        HostUpdatePullRuntimeGate.AutomaticUpdatesEnabled.Should().BeFalse();
        new DisabledHostUpdateDaemonExecutionGate().Evaluate().Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        typeof(HostUpdateDaemonVerifiedRelease).GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should().BeEmpty("no API payload, saved setting or caller can mint verification proof");
    }

    [Fact]
    public async Task DisabledByDefault_VerifiedReleaseIsNeverHandedToTheExecutor()
    {
        HostUpdateDaemonVerifiedRelease verified = await VerifiedAsync();
        var steps = new CrashingSteps(null, null);
        (HostUpdateExecutor executor, _) = Executor(steps);

        HostUpdateDaemonDispatchResult result = await new HostUpdateDaemonExecutionDispatcher(
            new DisabledHostUpdateDaemonExecutionGate(), executor, time).DispatchAsync(verified, RequestFor(verified), CancellationToken.None);

        result.Dispatched.Should().BeFalse();
        result.RefusalCode.Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        steps.Calls.Should().BeEmpty();
        File.Exists(JournalPath).Should().BeFalse("a refused dispatch writes no execution checkpoint");
        HostUpdateDaemonStatus cycle = await BootAsync();
        cycle.Checkpoint.Should().Be(HostUpdateDaemonCheckpointStatus.AwaitApproval);
        cycle.VerificationCode.Should().Be("verified:verified");
    }

    // ── Stale approval ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "approval_expired")]
    [InlineData(-1, "approval_expired")]
    [InlineData(6, "approval_lifetime_exceeded")]
    public async Task StaleApproval_IsRefusedBeforeAnyFetchOrExecution(int expiresInMinutes, string code)
    {
        HostUpdateDaemonVerificationResult result = await Verifier().VerifyAsync(Approved(Start.AddMinutes(expiresInMinutes)), Context(), CancellationToken.None);

        result.Verified.Should().BeFalse();
        result.RefusalCode.Should().Be(code);
        source.Requests.Should().BeEmpty();
        signature.Calls.Should().Be(0);
        HostUpdateDaemonStatus cycle = await BootAsync();
        cycle.VerificationCode.Should().Be("refused:" + code);
        cycle.Checkpoint.Should().Be(HostUpdateDaemonCheckpointStatus.AwaitApproval);
        File.Exists(JournalPath).Should().BeFalse();
    }

    [Fact]
    public async Task StaleApproval_PartitionOutlivesVerification_IsNeverDispatchedOrReverified()
    {
        HostUpdateDaemonVerifiedRelease verified = await VerifiedAsync();
        var executor = new Mock<IHostUpdateExecutor>(MockBehavior.Strict);
        time.Now = verified.ExpiresAt;

        HostUpdateDaemonDispatchResult open = await new HostUpdateDaemonExecutionDispatcher(new TestOnlyOpenGate(), executor.Object, time)
            .DispatchAsync(verified, RequestFor(verified), CancellationToken.None);
        HostUpdateDaemonDispatchResult production = await new HostUpdateDaemonExecutionDispatcher(new DisabledHostUpdateDaemonExecutionGate(), executor.Object, time)
            .DispatchAsync(verified, RequestFor(verified), CancellationToken.None);
        HostUpdateDaemonVerificationResult reverified = await Verifier().VerifyAsync(Approved(Start.AddMinutes(4)), Context(), CancellationToken.None);

        open.RefusalCode.Should().Be(HostUpdateDaemonExecutionDispatcher.VerificationExpiredCode);
        production.RefusalCode.Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        reverified.RefusalCode.Should().Be("approval_expired");
        executor.VerifyNoOtherCalls();
        (await BootAsync()).VerificationCode.Should().Be("refused:approval_expired");
    }

    [Fact]
    public async Task HostRestart_AfterVerification_GrantsNoOfflineAuthorization()
    {
        await VerifiedAsync();

        for (int boot = 0; boot < 3; boot++)
        {
            time.Now = Start.AddMinutes(10 * boot);
            HostUpdateDaemonStatus cycle = await BootAsync();

            cycle.VerificationCode.Should().Be("verified:verified", "evidence is reported, not replayed");
            cycle.Checkpoint.Should().Be(HostUpdateDaemonCheckpointStatus.AwaitApproval, "verification evidence is not an approval");
            cycle.ExecutionEnabled.Should().BeFalse();
            cycle.Enrolled.Should().BeFalse("no enrollment transport exists yet (#3115 routes are unmapped)");
        }

        File.Exists(JournalPath).Should().BeFalse();
    }

    // ── Revoked identity ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HostUpdateDaemonEnrollmentState.Active, false, HostUpdateDaemonRequestOutcome.Accepted)]
    [InlineData(HostUpdateDaemonEnrollmentState.Revoked, false, HostUpdateDaemonRequestOutcome.Revoked)]
    [InlineData(HostUpdateDaemonEnrollmentState.Quarantined, false, HostUpdateDaemonRequestOutcome.Quarantined)]
    [InlineData(HostUpdateDaemonEnrollmentState.Active, true, HostUpdateDaemonRequestOutcome.Expired)]
    public void RevokedIdentity_ApprovalPullIsRefusedByTheContract(
        HostUpdateDaemonEnrollmentState state, bool keyExpired, HostUpdateDaemonRequestOutcome expected)
    {
        using var daemonKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string keyId = "daemon-key-0000000001";
        HostUpdateDaemonSignedRequest request = HostUpdateDaemonRequestSignature.Sign(
            "POST", HostUpdateDaemonApiRoutes.Approval, "?", "{}"u8.ToArray(), 5, Start, "AAAAAAAAAAAAAAAAAAAAAA", keyId, daemonKey);
        var record = new HostUpdateDaemonKeyRecord
        {
            KeyId = keyId,
            InstallationId = new string('1', 32),
            EnrollmentEpoch = 3,
            State = state,
            PublicKey = daemonKey.ExportSubjectPublicKeyInfo(),
            KeyExpiresAt = keyExpired ? Start : Start.AddDays(30),
            EnrollmentStateRevision = 7,
            HighWaterCounter = 4,
        };

        HostUpdateDaemonRequestVerification result = HostUpdateDaemonRequestVerifier.Verify(
            request, id => id == keyId ? record : null, new EmptyNonceLedger(), Start, enrollmentStatusRequest: false);

        result.Outcome.Should().Be(expected);
        if (expected != HostUpdateDaemonRequestOutcome.Accepted)
        {
            result.Accepted.Should().BeFalse();
            HostUpdatePullIdentifiers.IsReasonCode(result.ReasonCode).Should().BeTrue();
        }
    }

    [Fact]
    public void RevokedIdentity_ReenrolledOrRekeyedApiAfterReconnect_AdmitsNothing()
    {
        using var responseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rotatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        HostUpdateDaemonResponseExpectation expectation = Expectation(responseKey);

        HostUpdateDaemonResponseVerification reenrolled = HostUpdateDaemonResponseVerifier.Verify(
            HostUpdateDaemonResponseSigner.Sign(Envelope(epoch: 4), responseKey, expectation.PinnedResponseKeyId), expectation, Start);
        HostUpdateDaemonResponseVerification rekeyed = HostUpdateDaemonResponseVerifier.Verify(
            HostUpdateDaemonResponseSigner.Sign(Envelope(epoch: 3), rotatedKey, expectation.PinnedResponseKeyId), expectation, Start);

        reenrolled.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.NeedsOperator);
        reenrolled.ReasonCode.Should().Be("enrollment_epoch_changed");
        rekeyed.Outcome.Should().Be(HostUpdateDaemonResponseOutcome.Discard);
        rekeyed.ReasonCode.Should().Be("response_signature_invalid");
    }

    // ── API partition ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiPartition_ReleaseSourceUnavailable_FailsClosedWithRedactedStatus()
    {
        source.Throw = new HttpRequestException("api down at " + root.FullName);

        HostUpdateDaemonVerificationResult result = await Verifier().VerifyAsync(Approved(Start.AddMinutes(4)), Context(), CancellationToken.None);

        result.RefusalCode.Should().Be("release_source_unavailable");
        signature.Calls.Should().Be(0);
        HostUpdateDaemonStatus cycle = await BootAsync();
        cycle.Code.Should().Be("daemon_cycle_completed");
        cycle.VerificationCode.Should().Be("refused:release_source_unavailable");
        cycle.Checkpoint.Should().Be(HostUpdateDaemonCheckpointStatus.AwaitApproval);
        AssertRedacted();
    }

    // ── Host / service restart across durable checkpoints ─────────────────────────────────

    [Theory]
    [InlineData(HostUpdateExecutionState.Preflight, "confirmation_required", false)]
    [InlineData(HostUpdateExecutionState.Draining, "execution_interrupted", true)]
    [InlineData(HostUpdateExecutionState.Migrating, "execution_interrupted", true)]
    [InlineData(HostUpdateExecutionState.Applying, "execution_interrupted", true)]
    [InlineData(HostUpdateExecutionState.Verifying, "execution_interrupted", true)]
    public async Task HostRestart_DuringStagingOrExecution_ReconcilesOnceAndNeverReplays(
        HostUpdateExecutionState crashAt, string firstCode, bool recordsStop)
    {
        HostUpdateDaemonVerifiedRelease verified = await VerifiedAsync();
        HostUpdateExecutionRequest request = RequestFor(verified);
        var crashing = new CrashingSteps(crashAt, null);
        (HostUpdateExecutor executor, PowerCutJournal journal) = Executor(crashing);
        crashing.Journal = journal;

        // Drive the real executor through the daemon's single dispatch path until the host loses power.
        Func<Task> dispatch = () => new HostUpdateDaemonExecutionDispatcher(new TestOnlyOpenGate(), executor, time)
            .DispatchAsync(verified, request, CancellationToken.None);
        await dispatch.Should().ThrowAsync<IOException>();
        IReadOnlyList<string> completedBeforeCrash = crashing.Calls;
        byte[] atCrash = File.ReadAllBytes(JournalPath);

        HostUpdateDaemonStatus first = await BootAsync();

        first.Checkpoint!.Code.Should().Be(firstCode);
        first.VerificationCode.Should().Be("verified:verified");
        first.InFlightCount.Should().Be(recordsStop ? 0 : 1);
        first.RecoveryRequiredCount.Should().Be(recordsStop ? 1 : 0);
        IReadOnlyList<HostUpdateExecutionActivity> history = new FileHostUpdateExecutionJournal(JournalPath).Read(ReleaseId);
        byte[] afterFirst = File.ReadAllBytes(JournalPath);
        if (recordsStop)
        {
            history[^1].State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
            history[^1].Phase.Should().Be("failure:daemon_interrupted");
            history.Take(history.Count - 1).Select(a => a.Phase).Should().Equal(Phases(atCrash));
            HostUpdateRecoveryRequestResolver.Resolve(history, null).Succeeded.Should().BeTrue("the existing host-local recovery accepts the stop");
        }
        else
        {
            afterFirst.Should().Equal(atCrash, "staging is deferred, never rewritten");
        }

        for (int boot = 0; boot < 3; boot++)
        {
            time.Now = Start.AddMinutes(1 + boot);
            HostUpdateDaemonStatus again = await BootAsync();
            again.Checkpoint!.Code.Should().Be(recordsStop ? "recovery_required" : "confirmation_required");
            File.ReadAllBytes(JournalPath).Should().Equal(afterFirst, "a restart appends at most one stop, once");
        }

        // A re-dispatch after restart is refused by the production gate before the executor.
        HostUpdateDaemonDispatchResult production = await new HostUpdateDaemonExecutionDispatcher(
            new DisabledHostUpdateDaemonExecutionGate(), executor, time).DispatchAsync(verified, request, CancellationToken.None);
        production.RefusalCode.Should().Be(DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        crashing.Calls.Should().Equal(completedBeforeCrash);

        if (recordsStop)
        {
            // Even the executor itself refuses to replay completed or interrupted side effects.
            var replay = new CrashingSteps(null, "replayed");
            (HostUpdateExecutor resumed, _) = Executor(replay);
            HostUpdateExecutionResult rerun = await resumed.ExecuteAsync(request, CancellationToken.None);
            rerun.State.Should().Be(HostUpdateExecutionState.RecoveryRequired);
            rerun.FailureCode.Should().Be("recovery_required");
            replay.Calls.Should().BeEmpty();
            File.ReadAllBytes(JournalPath).Should().Equal(afterFirst);
        }

        AssertRedacted(request);
    }

    [Fact]
    public async Task HostRestart_DuringHostLocalRecovery_NeverWaitsRetriesOrRewrites()
    {
        HostUpdateExecutionRequest request = RequestFor(await VerifiedAsync());
        var journal = new FileHostUpdateExecutionJournal(JournalPath);
        journal.Append(Activity(request, HostUpdateExecutionState.Accepted, "accepted"));
        journal.Append(Activity(request, HostUpdateExecutionState.Applying, "apply:before"));
        journal.Append(Activity(request, HostUpdateExecutionState.RecoveryRequired, "recovery:started"));
        byte[] before = File.ReadAllBytes(JournalPath);

        HostUpdateDaemonStatus held;
        using (new FileHostUpdateExecutionLock(LockPath).Acquire(TimeSpan.Zero, CancellationToken.None))
        {
            held = await BootAsync();
        }

        held.JournalCode.Should().Be(HostUpdateDaemonExecutionDispatcher.ExecutionLockHeldCode);
        held.Code.Should().Be("daemon_cycle_completed");
        File.ReadAllBytes(JournalPath).Should().Equal(before);
        for (int boot = 0; boot < 2; boot++)
        {
            HostUpdateDaemonStatus cycle = await BootAsync();
            cycle.Checkpoint!.Code.Should().Be("recovery_interrupted");
            cycle.Checkpoint.RecoveryHint.Should().Be("host_local_recover");
            File.ReadAllBytes(JournalPath).Should().Equal(before);
        }

        HostUpdateDaemonStatusReportDto report = published[^3].Checkpoint!.ToReport(Policy.PolicyRevision, time.GetUtcNow());
        report.DaemonState.Should().Be(HostUpdateDaemonState.NeedsOperator);
        report.LastResult.Outcome.Should().Be(HostUpdateDaemonResultOutcome.RecoveryRequired);
        HostUpdateDaemonStatusReportValidator.Validate(report, time.GetUtcNow()).Should().BeEmpty();
    }

    [Fact]
    public async Task Reconnect_AfterHostLocalRecovery_ReportsOnlyRedactedCurrentState()
    {
        HostUpdateDaemonVerifiedRelease verified = await VerifiedAsync();
        HostUpdateExecutionRequest request = RequestFor(verified);
        var crashing = new CrashingSteps(HostUpdateExecutionState.Applying, null);
        (HostUpdateExecutor executor, PowerCutJournal journal) = Executor(crashing);
        crashing.Journal = journal;
        await FluentActions.Awaiting(() => executor.ExecuteAsync(request, CancellationToken.None)).Should().ThrowAsync<IOException>();
        HostUpdateDaemonStatus interrupted = await BootAsync();

        // The operator's existing host-local recovery completes the release (rolled back).
        new FileHostUpdateExecutionJournal(JournalPath).Append(Activity(request, HostUpdateExecutionState.Completed, "recovery:rolled_back"));
        HostUpdateDaemonStatus recovered = await BootAsync();
        DateTimeOffset now = time.GetUtcNow();
        HostUpdateDaemonStatusReportDto before = interrupted.Checkpoint!.ToReport(Policy.PolicyRevision, now);
        HostUpdateDaemonStatusReportDto after = recovered.Checkpoint!.ToReport(Policy.PolicyRevision, now);

        before.DaemonState.Should().Be(HostUpdateDaemonState.NeedsOperator);
        before.RecoveryHints.Should().Equal("host_local_recover");
        recovered.Checkpoint.Action.Should().Be(HostUpdateDaemonCheckpointAction.Completed);
        recovered.RecoveryRequiredCount.Should().Be(0);
        after.DaemonState.Should().Be(HostUpdateDaemonState.Idle);
        after.RecoveryHints.Should().BeEmpty();
        after.ExecutionMode.Should().Be(HostUpdateDaemonExecutionMode.None);
        HostUpdateDaemonStatusReportValidator.Validate(before, now).Should().BeEmpty();
        HostUpdateDaemonStatusReportValidator.Validate(after, now).Should().BeEmpty();
        AssertRedacted(request, JsonSerializer.Serialize(before, HostUpdateDaemonJson.Options), JsonSerializer.Serialize(after, HostUpdateDaemonJson.Options));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>Boots a fresh daemon (production gate) over the shared state, runs one cycle and stops.</summary>
    private async Task<HostUpdateDaemonStatus> BootAsync()
    {
        var sink = new ListSink(published);
        var daemon = new HostUpdateDaemon(
            new HostUpdateDaemonOptions(),
            StateDirectory,
            root.FullName,
            new HostUpdateDaemonJournalReader(
                StateDirectory,
                new FileHostUpdateExecutionLock(LockPath),
                new FileHostUpdateExecutionJournal(JournalPath),
                new FileHostUpdateDaemonVerificationJournal(VerificationPath)),
            new DisabledHostUpdateDaemonExecutionGate(),
            sink,
            new ListLogger(logged),
            time);
        int first = published.Count;

        (await daemon.RunAsync(once: true, CancellationToken.None)).Should().BeNull();

        HostUpdateDaemonStatus[] boot = [.. published.Skip(first)];
        boot.Select(s => s.Lifecycle).Should().Equal(
            HostUpdateDaemonLifecycle.Starting, HostUpdateDaemonLifecycle.Running, HostUpdateDaemonLifecycle.Stopping, HostUpdateDaemonLifecycle.Stopped);
        boot.Should().OnlyContain(s => !s.ExecutionEnabled && s.ExecutionGateCode == DisabledHostUpdateDaemonExecutionGate.DisabledCode && !s.Enrolled);
        return boot[1];
    }

    private void AssertRedacted(HostUpdateExecutionRequest? request = null, params string[] extra)
    {
        string surface = string.Join('\n', published.Select(s => JsonSerializer.Serialize(s, HostUpdateDaemonJson.Options)).Concat(logged).Concat(extra));
        surface.Should().NotContain(root.FullName).And.NotContain(root.FullName.Replace("\\", "\\\\", StringComparison.Ordinal))
            .And.NotContain(ApprovalId).And.NotContain(manifestDigest).And.NotContain("api down")
            .And.NotContain(nameof(IOException)).And.NotContain("power_lost");
        if (request is not null)
        {
            surface.Should().NotContain(request.RequestId).And.NotContain(request.SourceCommit).And.NotContain(request.PolicyFingerprint);
        }
    }

    private async Task<HostUpdateDaemonVerifiedRelease> VerifiedAsync()
    {
        HostUpdateDaemonVerificationResult result = await Verifier().VerifyAsync(Approved(Start.AddMinutes(4)), Context(), CancellationToken.None);
        result.Verified.Should().BeTrue(result.RefusalCode);
        return result.Release!;
    }

    private HostUpdateDaemonReleaseVerifier Verifier() =>
        new(source, new NoReplay(), new FileHostUpdateDaemonVerificationJournal(VerificationPath), _ => signature, time);

    private HostUpdateDaemonApprovedRelease Approved(DateTimeOffset expiresAt) =>
        new(ApprovalId, ReleaseId, "insider", 10020000300042, manifestDigest, HostUpdateTrustRoot.DefaultTrustRoot, expiresAt);

    private HostUpdateDaemonVerificationContext Context() => new("insider", "linux-amd64", trustedRootPath, cosignPath);

    private (HostUpdateExecutor Executor, PowerCutJournal Journal) Executor(IHostUpdateExecutionSteps steps)
    {
        var journal = new PowerCutJournal(new FileHostUpdateExecutionJournal(JournalPath));
        return (new HostUpdateExecutor(steps, journal, new FileHostUpdateExecutionLock(LockPath), new InlinePolicyRepository()), journal);
    }

    private static HostUpdateExecutionRequest RequestFor(HostUpdateDaemonVerifiedRelease release) =>
        new(release.ReleaseId, release.Sequence, release.ManifestDigest, release.SourceCommit, HostUpdateExecutionChannel.Insider, [.. release.Targets])
        {
            RequestId = RequestId,
            TrustRoot = release.TrustRoot,
            PolicyRevision = Policy.PolicyRevision,
            PolicyFingerprint = Policy.Fingerprint,
            HostPlatform = release.HostPlatform,
        };

    private static HostUpdateExecutionActivity Activity(HostUpdateExecutionRequest request, HostUpdateExecutionState state, string phase) =>
        new(Guid.NewGuid().ToString("N"), request.ReleaseId, state, phase, DateTimeOffset.UtcNow)
        {
            RequestBinding = request,
            RequestBindingHash = HostUpdateRequestBinding.Compute(request),
        };

    private string[] Phases(byte[] journalBytes)
    {
        string copy = Path.Combine(root.FullName, "journal-" + Guid.NewGuid().ToString("N") + ".ndjson");
        File.WriteAllBytes(copy, journalBytes);
        return [.. new FileHostUpdateExecutionJournal(copy).Read(ReleaseId).Select(a => a.Phase)];
    }

    private static HostUpdateDaemonResponseExpectation Expectation(ECDsa responseKey) => new()
    {
        PinnedResponseKeyId = "api-response-key-0001",
        PinnedResponseKey = responseKey.ExportSubjectPublicKeyInfo(),
        InstallationId = new string('1', 32),
        KeyId = "daemon-key-0000000001",
        EnrollmentEpoch = 3,
        RequestNonce = "AAAAAAAAAAAAAAAAAAAAAA",
        RequestCounter = 11,
        LastAcknowledgedCounter = 10,
        LastEnrollmentStateRevision = 7,
    };

    private static HostUpdateDaemonResponseEnvelope<HostUpdateDaemonEnrollmentStatusDto> Envelope(long epoch) => new()
    {
        ProtocolVersion = HostUpdateDaemonProtocol.Version,
        ResponseType = HostUpdateDaemonResponseType.EnrollmentStatus,
        InstallationId = new string('1', 32),
        KeyId = "daemon-key-0000000001",
        EnrollmentEpoch = epoch,
        RequestNonce = "AAAAAAAAAAAAAAAAAAAAAA",
        AcknowledgedCounter = 10,
        EnrollmentStateRevision = 7,
        IssuedAt = Start,
        Payload = new HostUpdateDaemonEnrollmentStatusDto
        {
            State = HostUpdateDaemonEnrollmentState.Active,
            KeyExpiresAt = Start.AddDays(30),
            Reasons = [],
        },
    };

    private static string TrustedRoot()
    {
        var validFor = new { start = "2020-01-01T00:00:00Z" };
        return JsonSerializer.Serialize(new
        {
            mediaType = "application/vnd.dev.sigstore.trustedroot+json;version=0.1",
            certificateAuthorities = new[] { new { validFor } },
            tlogs = new[] { new { publicKey = new { validFor } } },
        });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VERSION")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>Test double only: production has no gate that can open (see the first test).</summary>
    private sealed class TestOnlyOpenGate : IHostUpdateDaemonExecutionGate
    {
        public string? Evaluate() => null;
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeReleaseSource(HostUpdateDaemonSignedArtifacts artifacts) : IHostUpdateDaemonReleaseSource
    {
        public Exception? Throw { get; set; }

        public List<(string Channel, string Tag)> Requests { get; } = [];

        public Task<HostUpdateDaemonSignedArtifacts?> FetchAsync(string channel, string tag, CancellationToken cancellationToken)
        {
            Requests.Add((channel, tag));
            return Throw is null ? Task.FromResult<HostUpdateDaemonSignedArtifacts?>(artifacts) : Task.FromException<HostUpdateDaemonSignedArtifacts?>(Throw);
        }
    }

    /// <summary>Stands in for Cosign; the signed-verification path around it is the production verifier.</summary>
    private sealed class RecordingSignatureVerifier : ISignedReleaseVerifier
    {
        public int Calls { get; private set; }

        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(certificateIdentity == HostUpdateTrustRoot.CertificateIdentity("insider"));
        }
    }

    private sealed class NoReplay : IHostUpdateReplayAdmissionReader
    {
        public Task<string?> EvaluateAdmissionAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class EmptyNonceLedger : IHostUpdateDaemonNonceLedger
    {
        public bool Contains(string keyId, string nonce) => false;
    }

    private sealed class InlinePolicyRepository : IHostUpdateAutomationPolicyRepository
    {
        public HostUpdatePolicyReadResult Read() => new(true, new HostUpdateAutomationPolicy(), null);

        public Task<HostUpdatePolicyReadResult> ReplaceAsync(HostUpdateAutomationPolicy policy, long expectedRevision, CancellationToken ct) =>
            throw new InvalidOperationException("policy is never replaced during reconciliation");
    }

    /// <summary>Loses power on demand: once cut, no further journal bytes become durable.</summary>
    private sealed class PowerCutJournal(IHostUpdateExecutionJournal inner) : IHostUpdateExecutionJournal
    {
        public bool Cut { get; set; }

        public IReadOnlyList<HostUpdateExecutionActivity> ReadAll() => inner.ReadAll();

        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => inner.Read(releaseId);

        public IReadOnlyList<string> ListReleaseIds() => inner.ListReleaseIds();

        public void Append(HostUpdateExecutionActivity activity)
        {
            if (Cut)
            {
                throw new IOException("power_lost");
            }

            inner.Append(activity);
        }
    }

    /// <summary>Executor steps that cut power inside <paramref name="crashAt"/>, or fail on any call when <paramref name="failAll"/> is set.</summary>
    private sealed class CrashingSteps(HostUpdateExecutionState? crashAt, string? failAll) : IHostUpdateExecutionSteps
    {
        private readonly List<string> calls = [];

        public PowerCutJournal? Journal { get; set; }

        public IReadOnlyList<string> Calls => [.. calls];

        public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Preflight);

        public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Draining);

        public Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Fenced);

        public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.BackedUp);

        public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Migrating);

        public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Applying);

        public Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct) => StepAsync(HostUpdateExecutionState.Verifying);

        private Task StepAsync(HostUpdateExecutionState state)
        {
            calls.Add(state.ToString());
            if (failAll is not null)
            {
                throw new InvalidOperationException(failAll);
            }

            if (state == crashAt)
            {
                Journal!.Cut = true;
                throw new IOException("power_lost");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ListSink(List<HostUpdateDaemonStatus> statuses) : IHostUpdateDaemonStatusSink
    {
        public void Publish(HostUpdateDaemonStatus status) => statuses.Add(status);
    }

    private sealed class ListLogger(List<string> messages) : ILogger<HostUpdateDaemon>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
