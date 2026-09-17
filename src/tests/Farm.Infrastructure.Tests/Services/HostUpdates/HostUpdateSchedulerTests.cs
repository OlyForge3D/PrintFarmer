#pragma warning disable VSTHRD003, S3398
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Settings;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateSchedulerTests
{
    [Fact]
    public async Task TickAsync_DefaultSettings_DoNotExecute()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(executor: executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.Disabled, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_StableOptIn_ExecutesOneImmutableRequest()
    {
        FakeExecutor executor = new();
        VerifiedHostUpdateCandidate candidate = Candidate();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(AutoEnabled: true), candidate, executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.Admitted, status.Reason);
        HostUpdateExecutorRequest request = Assert.Single(executor.Requests);
        Assert.Equal(candidate.ReleaseId, request.ReleaseId);
        Assert.Equal(candidate.SourceCommit, request.SourceCommit);
        Assert.Equal(candidate.Sequence, request.Sequence);
        Assert.Equal(candidate.ManifestDigest, request.ManifestDigest);
        Assert.Equal(candidate.Channel, request.Channel);
        Assert.Equal(candidate.TrustRoot, request.TrustRoot);
        Assert.False(string.IsNullOrWhiteSpace(request.PolicyFingerprint));
        Assert.Equal(candidate.PlatformDigests, request.PlatformDigests);
        Assert.True(request.IsValid);
    }

    [Fact]
    public async Task TickAsync_InsiderWithoutAcknowledgement_DoesNotExecute()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, Channel: UpdateChannelSettings.InsiderChannel), Candidate(UpdateChannelSettings.InsiderChannel), executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.InsiderAcknowledgementRequired, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_InsiderAcknowledged_Executes()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, Channel: UpdateChannelSettings.InsiderChannel, InsiderAcknowledged: true), Candidate(UpdateChannelSettings.InsiderChannel), executor);

        await scheduler.TickAsync();

        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_InvalidCacheAndClosedWindow_BackOffWithoutExecution()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate() with { CryptographicallyVerified = false, MaintenanceWindowOpen = false }, executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.CandidateInvalid, status.Reason);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_ExecutorFailure_BackOffsAndPreservesPolicyRevision()
    {
        FakeExecutor executor = new() { Response = new(HostUpdateExecutorResult.Failed, "failed") };
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, PolicyRevision: 7), Candidate(), executor);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.ExecutorFailed, status.Reason);
        Assert.Equal(7, status.PolicyRevision);
        Assert.NotNull(status.NextPollAt);
    }

    [Fact]
    public async Task TickAsync_MissingReplayStore_FailsClosed()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor, new MissingReplayStore());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.ReplayStoreUnavailable, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_ConcurrentCalls_DoNotOverlap()
    {
        BlockingExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor);
        Task<HostUpdateSchedulerStatus> first = scheduler.TickAsync();
        await executor.Started.Task;
        HostUpdateSchedulerStatus second = await scheduler.TickAsync();
        executor.Release.TrySetResult();
        await first;

        Assert.Equal(HostUpdateSchedulerReason.UpdateAlreadyRunning, second.Reason);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_HostShutdown_ReturnsCleanly()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        HostUpdateSchedulerStatus status = await scheduler.TickAsync(cancellation.Token);

        Assert.Equal(HostUpdateSchedulerReason.HostShutdown, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_Idle_IsNoOp()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(executor: executor);

        await scheduler.SignalSafeCheckpointCancellationAsync();

        Assert.Null(executor.CancelledRequestId);
    }

    [Fact]
    public async Task SignalSafeCheckpointCancellation_WhileExecutorRunning_SignalsActiveRequestIdExactlyOnce()
    {
        BlockingExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate(), executor);
        Task<HostUpdateSchedulerStatus> tick = scheduler.TickAsync();
        await executor.Started.Task;

        string? activeRequestId = scheduler.ActiveRequestId;
        Assert.NotNull(activeRequestId);

        await scheduler.SignalSafeCheckpointCancellationAsync();
        await scheduler.SignalSafeCheckpointCancellationAsync();

        executor.Release.TrySetResult();
        await tick;

        Assert.Equal(activeRequestId, executor.CancelledRequestId);
        Assert.Equal(1, executor.CancellationCallCount);
    }

    [Fact]
    public async Task TickAsync_KillSwitchAtEntry_NeverTouchesReplayOrExecutor()
    {
        FakeExecutor executor = new();
        ThrowingReplayStore replay = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true, KillSwitch: true), Candidate(), executor, replay);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.KillSwitch, status.Reason);
        Assert.Empty(executor.Requests);
        Assert.False(replay.WasCalled);
    }

    [Fact]
    public async Task TickAsync_AdmissionFenceActive_BlocksUpdateBeforeExecutor()
    {
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = new(
            new Settings(new HostUpdateSchedulerSettings(true)),
            new Cache(Candidate()),
            new MemoryReplayStore(),
            new AlwaysAdvancePolicyFence(),
            executor,
            new FixedClock(),
            new ZeroHostUpdateJitter(),
            admissionFence: new FixedAdmissionFence(new(true, "host_update_operation_active", "operation-1")));

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.AdmissionFenceActive, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_KillSwitchFlippedAfterReplayDecision_BlocksAdmissionBeforeExecutor()
    {
        FakeExecutor executor = new();
        MutableSettings settingsSource = new(new HostUpdateSchedulerSettings(true));
        KillSwitchFlippingReplayStore replay = new(settingsSource);
        HostUpdateScheduler scheduler = new(settingsSource, new Cache(Candidate()), replay, new AlwaysAdvancePolicyFence(), executor, new FixedClock(), new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.KillSwitch, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_BeforeNextPollAt_DoesNotEvaluate()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = new(new Settings(new HostUpdateSchedulerSettings(true)), new Cache(Candidate() with { CryptographicallyVerified = false }), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus first = await scheduler.TickAsync();
        Assert.NotNull(first.NextPollAt);

        clock.UtcNow = first.NextPollAt!.Value.AddSeconds(-1);
        HostUpdateSchedulerStatus early = await scheduler.TickAsync();
        Assert.Equal(HostUpdateSchedulerReason.TooEarly, early.Reason);
        Assert.Equal(first, early with { Reason = first.Reason });

        clock.UtcNow = first.NextPollAt!.Value;
        HostUpdateSchedulerStatus exact = await scheduler.TickAsync();
        Assert.NotEqual(HostUpdateSchedulerReason.TooEarly, exact.Reason);

        clock.UtcNow = first.NextPollAt!.Value.AddDays(1);
        HostUpdateSchedulerStatus overdue = await scheduler.TickAsync();
        Assert.NotEqual(HostUpdateSchedulerReason.TooEarly, overdue.Reason);
    }

    [Fact]
    public async Task TickAsync_SuccessfulAdmission_UsesConfiguredCadence()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = new(new Settings(new HostUpdateSchedulerSettings(true, PollIntervalSeconds: 120)), new Cache(Candidate()), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(120), status.NextPollAt);
    }

    [Fact]
    public async Task TickAsync_InsiderCadence_OnlyAppliesWhenAcknowledged()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new();
        HostUpdateSchedulerSettings settings = new(true, Channel: UpdateChannelSettings.InsiderChannel, InsiderAcknowledged: true, PollIntervalSeconds: 3600, InsiderPollIntervalSeconds: 90);
        HostUpdateScheduler scheduler = new(new Settings(settings), new Cache(Candidate(UpdateChannelSettings.InsiderChannel)), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(clock.UtcNow + TimeSpan.FromSeconds(90), status.NextPollAt);
    }

    [Fact]
    public async Task TickAsync_BackoffCap_NeverExceedsSixtyMinutesPlusBoundedJitter()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new() { Response = new(HostUpdateExecutorResult.Failed, "failed") };
        FixedJitter jitter = new(TimeSpan.FromDays(1));
        HostUpdateScheduler scheduler = new(new Settings(new HostUpdateSchedulerSettings(true)), new Cache(Candidate()), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, jitter);

        HostUpdateSchedulerStatus status = null!;
        for (int i = 0; i < 10; i++)
        {
            clock.UtcNow = clock.UtcNow.AddHours(2);
            status = await scheduler.TickAsync();
        }

        Assert.True(status.NextPollAt <= clock.UtcNow + TimeSpan.FromMinutes(65));
    }

    [Fact]
    public async Task TickAsync_NegativeJitter_IsClampedToZero()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new() { Response = new(HostUpdateExecutorResult.Failed, "failed") };
        FixedJitter jitter = new(TimeSpan.FromMinutes(-30));
        HostUpdateScheduler scheduler = new(new Settings(new HostUpdateSchedulerSettings(true)), new Cache(Candidate()), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, jitter);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(2), status.NextPollAt);
    }

    [Fact]
    public async Task TickAsync_PolicyRevisionRegression_IsFenced()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        MutableSettings settingsSource = new(new HostUpdateSchedulerSettings(true, PolicyRevision: 5));
        FileHostUpdatePolicyFence fence = new(TempRoot());
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = new(settingsSource, new Cache(Candidate()), new MemoryReplayStore(), fence, executor, clock, new ZeroHostUpdateJitter());
        HostUpdateSchedulerStatus first = await scheduler.TickAsync();
        clock.UtcNow = first.NextPollAt!.Value;

        settingsSource.Current = settingsSource.Current with { PolicyRevision = 3 };
        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.PolicyDrifted, status.Reason);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_PolicyRevisionEqualContentDrift_IsFenced()
    {
        MutableClock clock = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        MutableSettings settingsSource = new(new HostUpdateSchedulerSettings(true, PolicyRevision: 5, PollIntervalSeconds: 600));
        FileHostUpdatePolicyFence fence = new(TempRoot());
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = new(settingsSource, new Cache(Candidate()), new MemoryReplayStore(), fence, executor, clock, new ZeroHostUpdateJitter());
        HostUpdateSchedulerStatus first = await scheduler.TickAsync();
        clock.UtcNow = first.NextPollAt!.Value;

        settingsSource.Current = settingsSource.Current with { PolicyRevision = 5, PollIntervalSeconds = 1200 };
        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.PolicyDrifted, status.Reason);
    }

    [Fact]
    public async Task TickAsync_MaintenanceWindowWraparound_OpensOvernight()
    {
        MutableClock clock = new(new(2026, 1, 1, 23, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new();
        HostUpdateSchedulerSettings settings = new(true, MaintenanceWindowStartHour: 22, MaintenanceWindowEndHour: 6);
        HostUpdateScheduler scheduler = new(new Settings(settings), new Cache(Candidate()), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.Admitted, status.Reason);
    }

    [Fact]
    public async Task TickAsync_MaintenanceWindowWraparound_ClosedMidday()
    {
        MutableClock clock = new(new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        FakeExecutor executor = new();
        HostUpdateSchedulerSettings settings = new(true, MaintenanceWindowStartHour: 22, MaintenanceWindowEndHour: 6);
        HostUpdateScheduler scheduler = new(new Settings(settings), new Cache(Candidate()), new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor, clock, new ZeroHostUpdateJitter());

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.MaintenanceWindowClosed, status.Reason);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task ReplayStore_Sequence41Rejected_BlocksSequence40AndSurvivesRestart()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        VerifiedHostUpdateCandidate c41 = Candidate() with { Sequence = 41, CompatibilityReady = false };
        VerifiedHostUpdateCandidate c40 = Candidate() with { Sequence = 40 };

        FileHostUpdateReplayStore first = new(root, anchor);
        HostUpdateReplayDecision d41 = await first.DecideAsync(c41, HostUpdateReplayIntent.Reject, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, d41.Disposition);

        FileHostUpdateReplayStore restarted = new(root, anchor);
        HostUpdateReplayDecision d40 = await restarted.DecideAsync(c40, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, d40.Disposition);

        FileHostUpdateReplayStore afterRoundTrip = new(root, anchor);
        HostUpdateReplayDecision d40Again = await afterRoundTrip.DecideAsync(c40, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, d40Again.Disposition);
    }

    [Fact]
    public async Task ReplayStore_Sequence42Supersedes41()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        FileHostUpdateReplayStore store = new(root, anchor);
        VerifiedHostUpdateCandidate c41 = Candidate() with { Sequence = 41 };
        VerifiedHostUpdateCandidate c42 = Candidate() with { Sequence = 42 };

        HostUpdateReplayDecision d41 = await store.DecideAsync(c41, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, d41.Disposition);

        HostUpdateReplayDecision d42 = await store.DecideAsync(c42, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, d42.Disposition);

        HostUpdateReplayDecision d41Again = await store.DecideAsync(c41, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Superseded, d41Again.Disposition);
        Assert.True(d41Again.Reused);
    }

    [Fact]
    public async Task ReplayStore_EqualSequenceDifferentIdentity_Rejects()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        FileHostUpdateReplayStore store = new(root, new InMemoryReplayAnchor());
        VerifiedHostUpdateCandidate a = Candidate() with { Sequence = 5, ReleaseId = "release-a" };
        VerifiedHostUpdateCandidate b = Candidate() with { Sequence = 5, ReleaseId = "release-b" };

        HostUpdateReplayDecision first = await store.DecideAsync(a, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, first.Disposition);

        HostUpdateReplayDecision second = await store.DecideAsync(b, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, second.Disposition);
    }

    [Fact]
    public async Task ReplayStore_IdenticalCandidate_ReusesDecisionWithoutSecondOperation()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        FileHostUpdateReplayStore store = new(root, new InMemoryReplayAnchor());
        VerifiedHostUpdateCandidate candidate = Candidate();

        HostUpdateReplayDecision first = await store.DecideAsync(candidate, HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision second = await store.DecideAsync(candidate, HostUpdateReplayIntent.Admit, default);

        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, second.Disposition);
        Assert.True(second.Reused);
    }

    [Fact]
    public async Task ReplayStore_NamespacesAreIndependentByChannelAndTrustRoot()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        FileHostUpdateReplayStore store = new(root, new InMemoryReplayAnchor());
        VerifiedHostUpdateCandidate stableDefault = Candidate() with { Sequence = 100 };
        VerifiedHostUpdateCandidate insiderDefault = Candidate(UpdateChannelSettings.InsiderChannel) with { Sequence = 1 };
        VerifiedHostUpdateCandidate stableOtherRoot = Candidate() with { Sequence = 1, TrustRoot = "other-root" };

        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(stableDefault, HostUpdateReplayIntent.Admit, default)).Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(insiderDefault, HostUpdateReplayIntent.Admit, default)).Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, (await store.DecideAsync(stableOtherRoot, HostUpdateReplayIntent.Admit, default)).Disposition);
    }

    [Fact]
    public async Task ReplayStore_UnauthenticatedCandidate_NeverAdvancesHighWater()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        FileHostUpdateReplayStore store = new(root, new InMemoryReplayAnchor());
        VerifiedHostUpdateCandidate unauthenticated = Candidate() with { Sequence = 99, CryptographicallyVerified = false };
        VerifiedHostUpdateCandidate authenticated = Candidate() with { Sequence = 1 };

        HostUpdateReplayDecision fake = await store.DecideAsync(unauthenticated, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, fake.Disposition);

        HostUpdateReplayDecision real = await store.DecideAsync(authenticated, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, real.Disposition);
    }


    [Fact]
    public async Task TickAsync_InvalidSignature_DoesNotTouchReplayHighWater()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        using FileHostUpdateReplayStore store = new(root, anchor);
        FakeExecutor executor = new();
        HostUpdateScheduler scheduler = Create(new HostUpdateSchedulerSettings(true), Candidate() with { Sequence = 99, CryptographicallyVerified = false }, executor, store);

        HostUpdateSchedulerStatus status = await scheduler.TickAsync();
        HostUpdateReplayDecision lowerAuthenticated = await store.DecideAsync(Candidate() with { Sequence = 1 }, HostUpdateReplayIntent.Admit, default);

        Assert.Equal(HostUpdateSchedulerReason.CandidateInvalid, status.Reason);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, lowerAuthenticated.Disposition);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task TickAsync_MaintenanceWindowClosed_DoesNotTerminallyRejectCandidate()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        using FileHostUpdateReplayStore store = new(root, new InMemoryReplayAnchor());
        VerifiedHostUpdateCandidate candidate = Candidate();
        FakeExecutor blockedExecutor = new();
        HostUpdateScheduler blocked = Create(new HostUpdateSchedulerSettings(true, MaintenanceWindowStartHour: 1, MaintenanceWindowEndHour: 2), candidate, blockedExecutor, store);

        HostUpdateSchedulerStatus blockedStatus = await blocked.TickAsync();
        FakeExecutor allowedExecutor = new();
        HostUpdateScheduler allowed = Create(new HostUpdateSchedulerSettings(true), candidate, allowedExecutor, store);
        HostUpdateSchedulerStatus allowedStatus = await allowed.TickAsync();

        Assert.Equal(HostUpdateSchedulerReason.MaintenanceWindowClosed, blockedStatus.Reason);
        Assert.Equal(HostUpdateSchedulerReason.Admitted, allowedStatus.Reason);
        Assert.Empty(blockedExecutor.Requests);
        Assert.Single(allowedExecutor.Requests);
    }

    [Fact]
    public async Task ReplayStore_TerminalRejectedSequence41RemainsRejectedAfterSequence42SupersedesHighWater()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        using FileHostUpdateReplayStore store = new(root, anchor);
        VerifiedHostUpdateCandidate c41 = Candidate() with { Sequence = 41, ReleaseId = "release-1.5", SourceCommit = new string('4', 40) };
        VerifiedHostUpdateCandidate c42 = Candidate() with { Sequence = 42, ReleaseId = "release-1.6", SourceCommit = new string('5', 40) };

        HostUpdateReplayDecision rejected = await store.DecideAsync(c41, HostUpdateReplayIntent.Reject, default);
        HostUpdateReplayDecision accepted = await store.DecideAsync(c42, HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision rejectedAgain = await new FileHostUpdateReplayStore(root, anchor).DecideAsync(c41, HostUpdateReplayIntent.Admit, default);

        Assert.Equal(HostUpdateReplayDisposition.Rejected, rejected.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, accepted.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Rejected, rejectedAgain.Disposition);
        Assert.True(rejectedAgain.Reused);
    }

    [Fact]
    public async Task FileReplayStore_MissingState_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateReplayStore(TempRoot(), new InMemoryReplayAnchor()).DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default));
    }

    [Fact]
    public async Task FileReplayStore_UnavailableAnchor_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateReplayStore(TempRoot(), new UnavailableHostUpdateReplayAnchor()).DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default));
    }

    [Fact]
    public async Task FileReplayStore_CorruptedChecksum_FailsClosed()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        FileHostUpdateReplayStore store = new(root, anchor);
        await store.DecideAsync(Candidate(), HostUpdateReplayIntent.Admit, default);

        string path = Path.Combine(root, "host-update-replay.json");
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"Checksum\":\"sha256:", "\"Checksum\":\"sha256:ffff"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateReplayStore(root, anchor).DecideAsync(Candidate() with { Sequence = 2 }, HostUpdateReplayIntent.Admit, default));
    }

    [Fact]
    public async Task FileReplayStore_RestoredStaleSnapshot_FailsClosedViaAnchor()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        FileHostUpdateReplayStore store = new(root, anchor);
        await store.DecideAsync(Candidate() with { Sequence = 1 }, HostUpdateReplayIntent.Admit, default);
        string path = Path.Combine(root, "host-update-replay.json");
        string staleSnapshot = await File.ReadAllTextAsync(path);

        await store.DecideAsync(Candidate() with { Sequence = 2 }, HostUpdateReplayIntent.Admit, default);

        // Simulate an attacker (or operator) restoring the earlier, syntactically valid file.
        await File.WriteAllTextAsync(path, staleSnapshot);

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileHostUpdateReplayStore(root, anchor).DecideAsync(Candidate() with { Sequence = 3 }, HostUpdateReplayIntent.Admit, default));
    }

    [Fact]
    public async Task FileReplayStore_InterruptedTempWrite_DoesNotCorruptStateOnRestart()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        FileHostUpdateReplayStore store = new(root, anchor);
        await store.DecideAsync(Candidate() with { Sequence = 1 }, HostUpdateReplayIntent.Admit, default);

        await File.WriteAllTextAsync(Path.Combine(root, "host-update-replay.json.staged"), "garbage");

        FileHostUpdateReplayStore restarted = new(root, anchor);
        HostUpdateReplayDecision decision = await restarted.DecideAsync(Candidate() with { Sequence = 2 }, HostUpdateReplayIntent.Admit, default);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, decision.Disposition);
    }

    [Fact]
    public async Task ReplayStore_HighWaterIsIndependentPerChannel()
    {
        string root = TempRoot();
        await SeedEmptyReplayFileAsync(root);
        InMemoryReplayAnchor anchor = new();
        FileHostUpdateReplayStore store = new(root, anchor);

        HostUpdateReplayDecision stable = await store.DecideAsync(Candidate() with { Sequence = 8 }, HostUpdateReplayIntent.Admit, default);
        HostUpdateReplayDecision insider = await store.DecideAsync(Candidate(UpdateChannelSettings.InsiderChannel) with { Sequence = 1 }, HostUpdateReplayIntent.Admit, default);

        Assert.Equal(HostUpdateReplayDisposition.Accepted, stable.Disposition);
        Assert.Equal(HostUpdateReplayDisposition.Accepted, insider.Disposition);
    }

    private static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "printfarmer-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // The production replay store never self-provisions its backing file (see
    // FileReplayStore_MissingState_FailsClosed). Tests that exercise decision semantics on an
    // otherwise-fresh store must seed an empty-but-valid envelope first, mirroring the install-time
    // provisioning step that is intentionally out of scope for this milestone.
    private static async Task SeedEmptyReplayFileAsync(string root)
    {
        string checksum = HostUpdateCanonical.Hash(new
        {
            Version = 1,
            Epoch = 0L,
            HighWater = Array.Empty<object>(),
            Identities = Array.Empty<object>(),
        });
        var dto = new
        {
            Version = 1,
            Epoch = 0L,
            Checksum = checksum,
            HighWaterByNamespace = new Dictionary<string, object>(),
            Identities = new Dictionary<string, object>(),
        };
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "host-update-replay.json"), JsonSerializer.Serialize(dto));
    }

    private static string EmptyReplayStateHash()
    {
        string checksum = HostUpdateCanonical.Hash(new
        {
            Version = 1,
            Epoch = 0L,
            HighWater = Array.Empty<object>(),
            Identities = Array.Empty<object>(),
        });
        string json = JsonSerializer.Serialize(new
        {
            Version = 1,
            Epoch = 0L,
            Checksum = checksum,
            HighWaterByNamespace = new Dictionary<string, object>(),
            Identities = new Dictionary<string, object>(),
        });
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
    private static VerifiedHostUpdateCandidate Candidate(string channel = UpdateChannelSettings.StableChannel) => new("release-1", "commit-1", 1, "sha256:manifest", channel, true, true, true, true, true, true, new("sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "sha256:" + new string('c', 64), "sha256:" + new string('d', 64), "sha256:" + new string('e', 64), "sha256:" + new string('f', 64)));

    private static HostUpdateScheduler Create(HostUpdateSchedulerSettings? settings = null, VerifiedHostUpdateCandidate? candidate = null, FakeExecutor? executor = null, IHostUpdateReplayStore? replay = null) =>
        new(new Settings(settings ?? new()), new Cache(candidate), replay ?? new MemoryReplayStore(), new AlwaysAdvancePolicyFence(), executor ?? new FakeExecutor(), new FixedClock(), new ZeroHostUpdateJitter());

    private sealed class Settings(HostUpdateSchedulerSettings value) : IHostUpdateSchedulerSettings { public HostUpdateSchedulerSettings Current { get; } = value; }

    private sealed class MutableSettings(HostUpdateSchedulerSettings value) : IHostUpdateSchedulerSettings { public HostUpdateSchedulerSettings Current { get; set; } = value; }

    private sealed class Cache(VerifiedHostUpdateCandidate? value) : IHostUpdateSchedulerCandidateCache { public VerifiedHostUpdateCandidate? Current { get; } = value; public string? LastError => null; }

    private sealed class FixedClock : IHostUpdateClock { public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); }

    private sealed class MutableClock(DateTimeOffset value) : IHostUpdateClock { public DateTimeOffset UtcNow { get; set; } = value; }

    private sealed class FixedJitter(TimeSpan value) : IHostUpdateJitter { public TimeSpan For(string identity, int attempt) => value; }

    private sealed class AlwaysAdvancePolicyFence : IHostUpdatePolicyFence { public Task<bool> TryAdvanceAsync(long revision, string fingerprint, CancellationToken ct) => Task.FromResult(true); }

    private sealed class FixedAdmissionFence(HostUpdateAdmissionFenceStatus status) : IHostUpdateAdmissionFence { public HostUpdateAdmissionFenceStatus GetStatus() => status; }

    private sealed class InMemoryReplayAnchor : IHostUpdateReplayAnchor
    {
        private long _epoch;
        private string _stateHash = EmptyReplayStateHash();
        public Task<long> ReadEpochAsync(CancellationToken ct) => Task.FromResult(_epoch);
        public Task<string> ReadStateHashAsync(CancellationToken ct) => Task.FromResult(_stateHash);
        public Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct) { _epoch = epoch; _stateHash = stateHash; return Task.CompletedTask; }
    }

    private class FakeExecutor : IHostUpdateSchedulerExecutor
    {
        public List<HostUpdateExecutorRequest> Requests { get; } = [];
        public HostUpdateExecutorResponse Response { get; set; } = new(HostUpdateExecutorResult.Accepted);
        public string? CancelledRequestId { get; private set; }
        public int CancellationCallCount { get; private set; }
        public virtual Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(Response); }
        public virtual Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct) { CancelledRequestId = requestId; CancellationCallCount++; return Task.CompletedTask; }
    }

    private sealed class BlockingExecutor : FakeExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct) { Requests.Add(request); Started.TrySetResult(); await Release.Task.WaitAsync(ct); return Response; }
    }

    private sealed class MemoryReplayStore : IHostUpdateReplayStore
    {
        private readonly Dictionary<string, (long Sequence, string Identity)> _highWater = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (HostUpdateReplayDisposition Disposition, string CorrelationId)> _identities = new(StringComparer.Ordinal);

        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct)
        {
            if (!candidate.CryptographicallyVerified || string.IsNullOrWhiteSpace(candidate.TrustRoot))
            {
                return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Rejected, "unauthenticated:" + candidate.Identity, false));
            }

            if (_identities.TryGetValue(candidate.Identity, out (HostUpdateReplayDisposition Disposition, string CorrelationId) existing))
            {
                return Task.FromResult(new HostUpdateReplayDecision(existing.Disposition, existing.CorrelationId, true));
            }

            string ns = candidate.TrustRoot + "::" + candidate.Channel;
            string correlationId = "decision:" + candidate.Identity;
            _highWater.TryGetValue(ns, out (long Sequence, string Identity) highWater);

            if (highWater.Identity is not null && candidate.Sequence < highWater.Sequence)
            {
                _identities[candidate.Identity] = (HostUpdateReplayDisposition.Rejected, correlationId);
                return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Rejected, correlationId, false));
            }

            if (highWater.Identity is not null && candidate.Sequence == highWater.Sequence && highWater.Identity != candidate.Identity)
            {
                _identities[candidate.Identity] = (HostUpdateReplayDisposition.Rejected, correlationId);
                return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Rejected, correlationId, false));
            }

            HostUpdateReplayDisposition disposition = intent == HostUpdateReplayIntent.Admit ? HostUpdateReplayDisposition.Accepted : HostUpdateReplayDisposition.Rejected;
            if (highWater.Identity is not null && candidate.Sequence > highWater.Sequence &&
                _identities.TryGetValue(highWater.Identity, out (HostUpdateReplayDisposition Disposition, string CorrelationId) previous) &&
                previous.Disposition == HostUpdateReplayDisposition.Accepted)
            {
                _identities[highWater.Identity] = (HostUpdateReplayDisposition.Superseded, previous.CorrelationId);
            }

            _highWater[ns] = (candidate.Sequence, candidate.Identity);
            _identities[candidate.Identity] = (disposition, correlationId);
            return Task.FromResult(new HostUpdateReplayDecision(disposition, correlationId, false));
        }
    }

    private sealed class MissingReplayStore : IHostUpdateReplayStore
    {
        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct) => throw new InvalidDataException("missing");
    }

    private sealed class ThrowingReplayStore : IHostUpdateReplayStore
    {
        public bool WasCalled { get; private set; }
        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct) { WasCalled = true; throw new InvalidOperationException("must not be called"); }
    }

    private sealed class KillSwitchFlippingReplayStore(MutableSettings settings) : IHostUpdateReplayStore
    {
        public Task<HostUpdateReplayDecision> DecideAsync(VerifiedHostUpdateCandidate candidate, HostUpdateReplayIntent intent, CancellationToken ct)
        {
            settings.Current = settings.Current with { KillSwitch = true };
            return Task.FromResult(new HostUpdateReplayDecision(HostUpdateReplayDisposition.Accepted, "decision:" + candidate.Identity, false));
        }
    }
}
