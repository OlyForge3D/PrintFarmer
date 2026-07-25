using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

/// <summary>
/// Behavioral tests for <see cref="ShiftPlanCompiler"/>: dedupe by
/// (SourceKind, SourceId), in-place refresh, auto-complete on source
/// resolution, source-failure isolation (Fix 4), and conditional-write
/// optimization (Fix 3).
/// </summary>
[SuppressMessage("Design", "CA2201:Do not raise reserved exception types",
    Justification = "Tests intentionally construct plain Exception instances to simulate provider-agnostic DbUpdateException inner exceptions (Fix R3-2).")]
public class ShiftPlanCompilerTests
{
    private static readonly Guid PrinterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Mock<IUserTaskRepository> _tasks = new();
    private readonly List<UserTask> _tracked = new();
    private readonly List<UserTask> _trackedUpdated = new();

    public ShiftPlanCompilerTests()
    {
        // Fix 3/4: all tests should expect TrackAddAsync/TrackUpdateAsync, not AddAsync/UpdateAsync.
        _tasks.Setup(r => r.TrackAddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((t, _) => _tracked.Add(t))
            .Returns(Task.CompletedTask);
        _tasks.Setup(r => r.TrackUpdateAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((t, _) => _trackedUpdated.Add(t))
            .Returns(Task.CompletedTask);
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Fix F: default to no suppressed source keys so existing tests behave as before.
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string, long)>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string, long)>());
        // Fix R3-5: default auto-complete to "won the race" so existing auto-complete
        // tests behave as before unless a test explicitly overrides this.
        _tasks.Setup(r => r.TryAutoCompleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _tasks.Setup(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task CompileAsync_CreatesTaskForNewSpec()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Single(_tracked);
        Assert.Equal("failure:1", _tracked[0].SourceId);
        Assert.Equal(UserTaskSourceKind.FailureIncident, _tracked[0].SourceKind);
    }

    [Fact]
    public async Task CompileAsync_ExistingOpenTaskWithSameSource_UpdatesInPlaceInsteadOfDuplicating()
    {
        UserTask existing = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = UserTaskSourceKind.FailureIncident,
            SourceId = "failure:1",
            Status = UserTaskStatus.InProgress,
            Title = "old",
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1", title: "new")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Same(existing, _trackedUpdated.Single());
        Assert.Equal("new", existing.Title);
        Assert.Equal(UserTaskStatus.InProgress, existing.Status);
    }

    /// <summary>Fix 3: if spec fields match the existing task, no TrackUpdateAsync call is made.</summary>
    [Fact]
    public async Task CompileAsync_ExistingTaskUnchanged_DoesNotCallTrackUpdate()
    {
        ShiftPlanTaskSpec spec = Spec("failure:1", title: "same title");
        UserTask existing = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = spec.SourceKind,
            SourceId = spec.SourceId,
            Status = UserTaskStatus.InProgress,
            Title = spec.Title,
            Description = spec.Description,
            Priority = spec.Priority,
            AnchorKind = spec.AnchorKind,
            AnchorAtUtc = spec.AnchorAtUtc,
            WindowStartUtc = spec.WindowStartUtc,
            WindowEndUtc = spec.WindowEndUtc,
            DueAt = spec.DueAt,
            EntityType = spec.EntityType,
            EntityId = spec.EntityId,
            TaskType = spec.TaskType,
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], spec));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Updated);
        Assert.Empty(_trackedUpdated);
    }

    [Fact]
    public async Task CompileAsync_MissingSpec_AutoCompletesOpenTask()
    {
        UserTask stale = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = UserTaskSourceKind.FailureIncident,
            SourceId = "failure:gone",
            Status = UserTaskStatus.Pending,
            Title = "resolved elsewhere",
            LastMutationSequence = 1,
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stale });

        // Source owns FailureIncident and succeeds (no specs → all FailureIncident tasks stale).
        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn", [UserTaskSourceKind.FailureIncident]));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Completed, stale.Status);
        Assert.NotNull(stale.CompletedAt);
    }

    [Fact]
    public async Task CompileAsync_SourceThatThrows_IsIsolated_OtherSourcesStillMaterialize()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanCompiler compiler = BuildCompiler(
            new ThrowingSource([UserTaskSourceKind.Maintenance]),
            new StubSource("attn", [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.SourceFailures);
    }

    /// <summary>Fix 4: a task whose source failed this pass MUST remain open.</summary>
    [Fact]
    public async Task CompileAsync_SourceThatThrows_PreservesTasksOwnedByFailedSource()
    {
        UserTask maintenanceTask = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = UserTaskSourceKind.Maintenance,
            SourceId = "maint:1",
            Status = UserTaskStatus.Pending,
            Title = "maintenance window",
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { maintenanceTask });

        // Maintenance source throws; attention source succeeds with no specs.
        ShiftPlanCompiler compiler = BuildCompiler(
            new ThrowingSource([UserTaskSourceKind.Maintenance]),
            new StubSource("attn", [UserTaskSourceKind.FailureIncident]));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        // The maintenance task must NOT be auto-completed.
        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, maintenanceTask.Status);
        Assert.Null(maintenanceTask.CompletedAt);
    }

    /// <summary>
    /// An indeterminate maintenance key is preserved explicitly without turning unrelated
    /// authoritative maintenance keys into source-wide failures.
    /// </summary>
    [Fact]
    public async Task CompileAsync_MaintenanceSourceIndeterminateEligibility_PreservesOpenMaintenanceTask()
    {
        Guid alertId = Guid.Parse("B1B1B1B1-B1B1-B1B1-B1B1-B1B1B1B1B1B1");

        UserTask maintenanceTask = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = UserTaskSourceKind.Maintenance,
            SourceId = $"maintenancealert:{alertId}",
            Status = UserTaskStatus.Pending,
            Title = "maintenance window",
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { maintenanceTask });

        Mock<IMaintenanceAlertRepository> alerts = new();
        alerts.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert>
            {
                new()
                {
                    Id = alertId,
                    PrinterId = PrinterId,
                    Title = "Check nozzle",
                    Message = "Nozzle needs cleaning.",
                    Severity = 2,
                    Status = MaintenanceAlertStatus.Active,
                },
            });

        // Scorer outage: the alerted printer is indeterminate (absent from Windows).
        Mock<IIdleWindowService> idle = new();
        idle.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow>(), new HashSet<Guid> { PrinterId }));

        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.Get<ShiftPlanSettings>()).Returns(new ShiftPlanSettings());

        Mock<IOperatorFeatureGate> featureGate = new();
        featureGate.Setup(g => g.IsEnabled(It.IsAny<OperatorFeature>())).Returns(true);

        MaintenanceIdleWindowShiftPlanTaskSource maintenanceSource = new(
            alerts.Object,
            idle.Object,
            settings.Object,
            featureGate.Object,
            NullLogger<MaintenanceIdleWindowShiftPlanTaskSource>.Instance);

        ShiftPlanCompiler compiler = BuildCompiler(maintenanceSource);
        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.SourceFailures);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, maintenanceTask.Status);
        Assert.Null(maintenanceTask.CompletedAt);
    }

    [Fact]
    public async Task CompileAsync_BaselineResultWithoutAuthority_PreservesOpenTask()
    {
        UserTask stale = OpenTask(UserTaskSourceKind.FailureIncident, "failure:baseline", sequence: 1);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([stale]);
        ShiftPlanCompiler compiler = BuildCompiler(
            new ControlledSource(
                "baseline",
                [UserTaskSourceKind.FailureIncident],
                [],
                new ShiftPlanSourceResult([], OriginWatermark: 10)));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.AutoCompleted);
        _tasks.Verify(r => r.TryAutoCompleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompileAsync_CompleteKindWithPreservedSourceId_ResolvesOnlyAuthoritativeAbsence()
    {
        UserTask resolvable = OpenTask(UserTaskSourceKind.FailureIncident, "failure:resolved", sequence: 2);
        UserTask indeterminate = OpenTask(UserTaskSourceKind.FailureIncident, "failure:unknown", sequence: 2);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([resolvable, indeterminate]);
        ShiftPlanCompiler compiler = BuildCompiler(
            AuthoritySource(
                "authority",
                UserTaskSourceKind.FailureIncident,
                originWatermark: 2,
                preservedSourceIds: new HashSet<string>(
                    ["failure:unknown"],
                    StringComparer.Ordinal)));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Completed, resolvable.Status);
        Assert.Equal(UserTaskStatus.Pending, indeterminate.Status);
        _tasks.Verify(r => r.TryAutoCompleteAsync(
                resolvable.Id,
                2,
                2,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _tasks.Verify(r => r.TryAutoCompleteAsync(
                indeterminate.Id,
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0L, 10L)]
    [InlineData(1L, null)]
    [InlineData(11L, 10L)]
    public async Task CompileAsync_UnprovenCausalFence_PreservesOpenTask(
        long sequence,
        long? originWatermark)
    {
        UserTask stale = OpenTask(UserTaskSourceKind.FailureIncident, "failure:fenced", sequence);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([stale]);
        ShiftPlanCompiler compiler = BuildCompiler(
            AuthoritySource(
                "authority",
                UserTaskSourceKind.FailureIncident,
                originWatermark));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, stale.Status);
    }

    [Fact]
    public async Task CompileAsync_DuplicateKindOwners_PreservesAbsentTask()
    {
        UserTask stale = OpenTask(UserTaskSourceKind.FailureIncident, "failure:ambiguous", sequence: 1);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([stale]);
        ShiftPlanCompiler compiler = BuildCompiler(
            AuthoritySource("first", UserTaskSourceKind.FailureIncident, originWatermark: 1),
            AuthoritySource("second", UserTaskSourceKind.FailureIncident, originWatermark: 1));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, stale.Status);
    }

    [Fact]
    public async Task CompileAsync_CollidingSpecs_DoesNotChooseWinnerOrResolveExistingTask()
    {
        const string sourceId = "failure:collision";
        UserTask existing = OpenTask(UserTaskSourceKind.FailureIncident, sourceId, sequence: 1);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        ShiftPlanTaskSpec first = Spec(sourceId, title: "first");
        ShiftPlanTaskSpec second = Spec(sourceId, title: "second");
        ShiftPlanCompiler compiler = BuildCompiler(
            AuthoritySource("first", UserTaskSourceKind.FailureIncident, 1, specs: [first]),
            AuthoritySource("second", UserTaskSourceKind.FailureIncident, 1, specs: [second]));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal((0, 0, 0), (result.Created, result.Updated, result.AutoCompleted));
        Assert.Equal("task", existing.Title);
    }

    /// <summary>
    /// Fix R4-3: at the end of a pass the suppression watermark is advanced to
    /// <c>now - SuppressionWatermarkOverlap</c> (15s), not exactly <c>now</c>, so a user
    /// skip stamped just before this pass but committed just after its suppression query
    /// is still inside the next pass's lookback.
    /// </summary>
    [Fact]
    public async Task CompileAsync_R4_3_AdvancesSuppressionWatermarkWithOverlap()
    {
        ShiftPlanSuppressionState state = new();
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MutableClock clock = new(t0);

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanCompiler compiler = BuildCompiler(clock, new StubSource("attn",
            [UserTaskSourceKind.FailureIncident]));

        _ = await compiler.CompileAsync(state);

        Assert.Equal(t0.UtcDateTime.AddSeconds(-15), state.LastPassAtUtc);
    }

    /// <summary>
    /// Fix R4-3 (behavioral): a skip whose <c>UpdatedAt</c> is stamped just before a
    /// pass's <c>now</c> but whose transaction commits just after that pass's suppression
    /// query runs is missed by the pass that created the task. The 15s watermark overlap
    /// ensures the NEXT pass's lookback still includes it, so the compiler does not
    /// recreate the task the user just dismissed. Without the overlap the next pass would
    /// query <c>[now, ...)</c> and miss the skip forever.
    /// </summary>
    [Fact]
    public async Task CompileAsync_R4_3_SkipCommittedAfterQuery_IsObservedNextPassViaOverlap()
    {
        ShiftPlanSuppressionState state = new();
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        DateTime skipUpdatedAt = t0.UtcDateTime.AddMilliseconds(-1);
        MutableClock clock = new(t0);

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // Pass 1's suppression query runs before the skip commits → returns nothing.
        // Pass 2's query observes the skip iff its lookback watermark reaches back to
        // skipUpdatedAt (t0 - 1ms). With the overlap the watermark is t0 - 15s, which
        // does; without it the watermark would be t0, which does not.
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime since, CancellationToken _) =>
            {
                IReadOnlyCollection<(UserTaskSourceKind, string, long)> observed = since <= skipUpdatedAt
                    ? [(UserTaskSourceKind.FailureIncident, "failure:1", 1L)]
                    : Array.Empty<(UserTaskSourceKind, string, long)>();
                return Task.FromResult(observed);
            });

        ShiftPlanCompiler compiler = BuildCompiler(clock, new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        // Pass 1 at t0: skip not yet committed → task created.
        ShiftPlanCompileResult r1 = await compiler.CompileAsync(state);
        Assert.Equal(1, r1.Created);

        // Advance one compile cadence; pass 2 must observe the now-committed skip and
        // NOT recreate the task.
        clock.Advance(TimeSpan.FromSeconds(15));
        ShiftPlanCompileResult r2 = await compiler.CompileAsync(state);
        Assert.Equal(0, r2.Created);
    }

    /// <summary>
    /// Fix R6-1: a source that failed before it could bootstrap remains unbootstrapped
    /// even though the global delta watermark advances. When that source later recovers,
    /// its active-key query must still recover the pre-restart dismissal before upsert.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SourceFailsBeforeBootstrapThenRecovers_PreservesPreRestartDismissal()
    {
        ShiftPlanSuppressionState state = new();
        ShiftPlanTaskSpec spec = Spec("failure:pre-restart");
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string, long)>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(key => key.SourceKind == UserTaskSourceKind.FailureIncident
                        && key.SourceId == "failure:pre-restart")),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, "failure:pre-restart", 1L)]);

        ShiftPlanCompiler failingCompiler = BuildCompiler(
            new ThrowingSource([UserTaskSourceKind.FailureIncident]));
        ShiftPlanCompileResult failedPass = await failingCompiler.CompileAsync(state);

        Assert.Equal(1, failedPass.SourceFailures);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        ShiftPlanCompiler recoveredCompiler = BuildCompiler(
            new StubSource("attn", [UserTaskSourceKind.FailureIncident], spec));
        ShiftPlanCompileResult recoveredPass = await recoveredCompiler.CompileAsync(state);

        Assert.Equal(0, recoveredPass.Created);
        Assert.Empty(_tracked);
        Assert.True(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(key => key.SourceKind == UserTaskSourceKind.FailureIncident
                        && key.SourceId == "failure:pre-restart")),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompileAsync_IncompleteSourceEmitsSuppressedSpec_BootstrapsKeyWithoutRecreatingTask()
    {
        const string sourceId = "failure:pre-restart";
        ShiftPlanSuppressionState state = new();
        ShiftPlanTaskSpec spec = Spec(sourceId);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(key => key.SourceKind == UserTaskSourceKind.FailureIncident
                        && key.SourceId == sourceId)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, sourceId, 1L)]);
        ShiftPlanSourceResult incomplete = new([spec], OriginWatermark: 10)
        {
            Authority = new ShiftPlanSourceAuthority(
            [
                new ShiftPlanKindAuthority(
                    UserTaskSourceKind.FailureIncident,
                    IsAuthoritativeComplete: false,
                    PreservedSourceIds: new HashSet<string>(StringComparer.Ordinal),
                    IncompleteReasons: ["bounded"]),
            ]),
        };
        ShiftPlanCompiler compiler = BuildCompiler(new ControlledSource(
            "incomplete",
            [UserTaskSourceKind.FailureIncident],
            [spec],
            incomplete));

        ShiftPlanCompileResult result = await compiler.CompileAsync(state);

        Assert.Equal(0, result.Created);
        Assert.Empty(_tracked);
        Assert.Contains((UserTaskSourceKind.FailureIncident, sourceId), state.SuppressedKeys);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(key => key.SourceKind == UserTaskSourceKind.FailureIncident
                        && key.SourceId == sourceId)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompileAsync_CollidingSuppressedKey_PreservesSuppressionUntilCollisionClears()
    {
        const string sourceId = "failure:collision";
        ShiftPlanSuppressionState state = new();
        _ = state.SuppressedKeys.Add((UserTaskSourceKind.FailureIncident, sourceId));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        ShiftPlanTaskSpec first = Spec(sourceId, title: "first");
        ShiftPlanTaskSpec second = Spec(sourceId, title: "second");
        ShiftPlanCompiler collidingCompiler = BuildCompiler(
            AuthoritySource(
                "colliding",
                UserTaskSourceKind.FailureIncident,
                originWatermark: 10,
                specs: [first, second]));

        ShiftPlanCompileResult collidingPass = await collidingCompiler.CompileAsync(state);

        Assert.Equal(0, collidingPass.Created);
        Assert.Contains((UserTaskSourceKind.FailureIncident, sourceId), state.SuppressedKeys);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        ShiftPlanCompiler recoveredCompiler = BuildCompiler(
            AuthoritySource(
                "recovered",
                UserTaskSourceKind.FailureIncident,
                originWatermark: 10,
                specs: [first]));
        ShiftPlanCompileResult recoveredPass = await recoveredCompiler.CompileAsync(state);

        Assert.Equal(0, recoveredPass.Created);
        Assert.Empty(_tracked);
        Assert.Contains((UserTaskSourceKind.FailureIncident, sourceId), state.SuppressedKeys);
        Assert.True(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
    }

    /// <summary>
    /// Issue #823 (mixed-collision): a persistent spec collision on key B keeps the whole
    /// source kind unbootstrapped, which leaves the exact-key durable bootstrap permanently
    /// active. A different key A that was authoritatively cleared this process must NOT
    /// inherit its stale durable Skip/Dismiss row when A genuinely recurs — the new episode
    /// materializes instead of being conservatively suppressed. The collision invariant is
    /// preserved: the kind stays unbootstrapped until B's ambiguity clears.
    /// </summary>
    [Fact]
    public async Task CompileAsync_ClearedKeyExcludedFromBootstrap_WhilePersistentCollisionKeepsKindUnbootstrapped()
    {
        const string keyA = "failure:A";
        const string keyB = "failure:B";
        ShiftPlanSuppressionState state = new();
        // The user previously dismissed A: it is currently suppressed.
        _ = state.SuppressedKeys.Add((UserTaskSourceKind.FailureIncident, keyA));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        // A's stale durable Skip row is available to exact-key bootstrap if it is ever queried.
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyA)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyA, 1L)]);

        // Pass 1: source produces B twice (persistent collision) and NOT A. A is
        // authoritatively absent -> cleared; B collision keeps the kind unbootstrapped.
        ShiftPlanTaskSpec bFirst = Spec(keyB, title: "b-first");
        ShiftPlanTaskSpec bSecond = Spec(keyB, title: "b-second");
        ShiftPlanCompiler pass1 = BuildCompiler(
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [bFirst, bSecond]));
        ShiftPlanCompileResult r1 = await pass1.CompileAsync(state);

        Assert.Equal(0, r1.Created);
        Assert.DoesNotContain((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        Assert.True(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        // Pass 2: A genuinely recurs while B still collides (kind still unbootstrapped).
        // Cleared evidence excludes A from exact-key bootstrap, so its stale durable row
        // does NOT re-suppress the new episode: a fresh A task materializes.
        ShiftPlanTaskSpec aSpec = Spec(keyA, title: "a-new");
        ShiftPlanCompiler pass2 = BuildCompiler(
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [aSpec, bFirst, bSecond]));
        ShiftPlanCompileResult r2 = await pass2.CompileAsync(state);

        Assert.Equal(1, r2.Created);
        Assert.Contains(_tracked, t => t.SourceId == keyA);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        // Exact-key bootstrap must NEVER have been asked about A: its cleared evidence
        // holds it back, so its stale durable row cannot re-suppress the new episode.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyA)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        // While A is cleared and B collides, exact-key durable bootstrap never runs at all:
        // the colliding key B is removed from positive resolution (frozen), and the cleared
        // key A is held back — so no candidate key remains and A's stale durable Skip row is
        // never imported. The kind stays fail-closed (unbootstrapped) on its collision.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #823 (fresh-dismissal): a Skip/Dismiss strictly newer than a key's cleared
    /// version must re-suppress the key and evict its cleared evidence, so the user's new
    /// action is honored rather than treated as an already-cleared prior episode.
    /// </summary>
    [Fact]
    public async Task CompileAsync_FreshDismissalOfClearedKey_ReSuppressesAndEvictsClearedEvidence()
    {
        const string keyA = "failure:A";
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        DateTime watermark = t0.UtcDateTime.AddMinutes(-5);
        MutableClock clock = new(t0);
        ShiftPlanSuppressionState state = new();
        // A was authoritatively cleared at dismissal version 3 earlier this process.
        state.MarkCleared((UserTaskSourceKind.FailureIncident, keyA), version: 3, clearedAtUtc: t0.UtcDateTime.AddMinutes(-10));
        // A prior pass ran, so the live-tracking watermark is set.
        state.LastPassAtUtc = watermark;
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        // The user freshly dismissed A: the delta row's mutation version (4) is strictly
        // newer than the cleared version (3), so it is a genuine new dismissal, not a replay.
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyA, 4L)]);

        ShiftPlanCompiler compiler = BuildCompiler(clock, new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec(keyA)));
        ShiftPlanCompileResult result = await compiler.CompileAsync(state);

        Assert.Equal(0, result.Created);
        Assert.Empty(_tracked);
        Assert.Contains((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        Assert.False(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        // The replay tombstone advanced to the fresh dismissal version (4), so a later replay
        // of the old cleared row (v3) stays idempotent.
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone fresh));
        Assert.Equal(4L, fresh.Version);
        // The delta was observed exactly once, from the retained pass watermark.
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(watermark, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Issue #823 (overlap replay): the suppression-delta windows intentionally overlap by
    /// 15s, so the SAME durable dismissal row can be returned on consecutive passes. A replay
    /// (equal mutation version) must be idempotent — it must NOT re-suppress a key that was
    /// authoritatively cleared, so a genuinely recurring key still materializes. A strictly
    /// newer dismissal, however, must re-suppress and evict the cleared evidence.
    /// </summary>
    [Fact]
    public async Task CompileAsync_OverlappedDeltaReplaysClearedDismissal_DoesNotReSuppress_ButStrictlyNewerDoes()
    {
        const string keyA = "failure:A";
        const string keyB = "failure:B";
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MutableClock clock = new(t0);
        ShiftPlanSuppressionState state = new();
        // A prior pass ran, so Path 1 (the overlapped delta) is active from pass 1. Each pass
        // ends by advancing the watermark to (now - 15s overlap), so the exact delta watermarks
        // are deterministic: passes 1 and 2 both read t0-15s, pass 3 reads t0.
        DateTime wmPass12 = t0.UtcDateTime.AddSeconds(-15); // 11:59:45 — read by passes 1 and 2
        DateTime wmPass3 = t0.UtcDateTime;                  // 12:00:00 — read by pass 3
        state.LastPassAtUtc = wmPass12;

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // The overlapped delta the compiler reads each pass. Mutated between passes to model
        // an exact replay (same version) then a strictly newer dismissal.
        (UserTaskSourceKind Kind, string Id, long Version)[] delta =
            [(UserTaskSourceKind.FailureIncident, keyA, 5L)];
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => delta);

        // A's stale durable Skip row (version 5) is available to exact-key bootstrap — the
        // test proves it is never imported for A once A is cleared.
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyA)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyA, 5L)]);

        ShiftPlanTaskSpec bFirst = Spec(keyB, title: "b-first");
        ShiftPlanTaskSpec bSecond = Spec(keyB, title: "b-second");

        // Pass 1 at t0: delta observes A's dismissal (v5) → A suppressed; source emits B twice
        // (persistent collision) and NOT A → A is authoritatively cleared (evidence v5); the
        // B collision keeps the kind unbootstrapped.
        ShiftPlanCompiler pass1 = BuildCompiler(clock,
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [bFirst, bSecond]));
        ShiftPlanCompileResult r1 = await pass1.CompileAsync(state);

        Assert.Equal(0, r1.Created);
        Assert.True(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone e1));
        Assert.Equal(5L, e1.Version);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        // Pass 2 at t0+15s: the delta REPLAYS the same row (v5). A genuinely recurs while B
        // still collides. The equal-version replay must NOT re-suppress A, and cleared
        // evidence excludes A from exact-key bootstrap, so the new A episode materializes.
        clock.Advance(TimeSpan.FromSeconds(15));
        ShiftPlanTaskSpec aSpec = Spec(keyA, title: "a-new");
        ShiftPlanCompiler pass2 = BuildCompiler(clock,
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [aSpec, bFirst, bSecond]));
        ShiftPlanCompileResult r2 = await pass2.CompileAsync(state);

        Assert.Equal(1, r2.Created);
        Assert.Contains(_tracked, t => t.SourceId == keyA);
        Assert.True(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));

        // The overlapped delta was read once per pass at the exact expected watermark: passes 1
        // and 2 both read from t0-15s (the same v5 row was returned on both), proving the replay.
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(wmPass12, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        // Through passes 1–2 (while A is cleared and B collides) exact-key durable bootstrap
        // never runs at all — the colliding B is frozen out of positive resolution and the
        // cleared A is held back — so A's stale durable row is never imported.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Pass 3 at t0+30s: a STRICTLY NEWER dismissal of A (v6) arrives. It must evict the
        // cleared evidence and re-suppress A, so A no longer materializes.
        clock.Advance(TimeSpan.FromSeconds(15));
        delta = [(UserTaskSourceKind.FailureIncident, keyA, 6L)];
        _tracked.Clear();
        ShiftPlanCompiler pass3 = BuildCompiler(clock,
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [aSpec, bFirst, bSecond]));
        ShiftPlanCompileResult r3 = await pass3.CompileAsync(state);

        Assert.Equal(0, r3.Created);
        Assert.DoesNotContain(_tracked, t => t.SourceId == keyA);
        Assert.Contains((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        Assert.False(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        // The strictly-newer dismissal advanced the replay tombstone to v6.
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone e3));
        Assert.Equal(6L, e3.Version);
        // Pass 3 read the delta exactly once at the advanced watermark (t0), distinct from the
        // passes 1/2 watermark — so the total delta read count is exactly three.
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(wmPass3, It.IsAny<CancellationToken>()),
            Times.Once);
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    /// <summary>
    /// Issue #823 (collision-FREE replay — the cycle-2 regression): even on the normal
    /// collision-free path, once a key is authoritatively cleared and its kind bootstraps, an
    /// overlapped delta can replay the SAME durable dismissal row 15s later. The bounded replay
    /// tombstone (which survives <see cref="ShiftPlanSuppressionState.MarkBootstrapped"/>) must
    /// keep that replay idempotent so a genuine recurrence of the cleared key materializes and is
    /// NOT re-suppressed. A strictly-newer dismissal still re-suppresses. This is the exact case
    /// that broke when bootstrap-exclusion and replay-version memory were a single object dropped
    /// on bootstrap.
    /// </summary>
    [Fact]
    public async Task CompileAsync_CollisionFreeBootstrap_OverlappedReplayDoesNotReSuppressRecurringKey()
    {
        const string keyA = "failure:A";
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MutableClock clock = new(t0);
        ShiftPlanSuppressionState state = new();
        // A prior pass ran, so Path 1 (the overlapped delta) is active from pass 1. End-of-pass
        // advances the watermark to (now - 15s), so passes 1 and 2 both read t0-15s and pass 3
        // reads t0 — asserted exactly below.
        DateTime wmPass12 = t0.UtcDateTime.AddSeconds(-15); // 11:59:45 — read by passes 1 and 2
        DateTime wmPass3 = t0.UtcDateTime;                  // 12:00:00 — read by pass 3
        state.LastPassAtUtc = wmPass12;

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // A's durable Skip row is available to exact-key bootstrap if it were ever queried — the
        // test proves it is NEVER queried (the kind bootstraps and A is created directly).
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyA, 5L)]);

        // The overlapped delta the compiler reads each pass, reassigned between passes to model an
        // exact replay (same version) then a strictly-newer dismissal.
        (UserTaskSourceKind Kind, string Id, long Version)[] delta =
            [(UserTaskSourceKind.FailureIncident, keyA, 5L)];
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => delta);

        // Pass 1 at t0: delta observes A's dismissal (v5) -> A suppressed. The source
        // authoritatively observes FailureIncident with NO emitted keys and NO collision, so A is
        // authoritatively cleared and the (collision-free) kind bootstraps. MarkBootstrapped drops
        // A's bootstrap-exclusion evidence but MUST retain the replay tombstone (v5).
        ShiftPlanCompiler pass1 = BuildCompiler(clock,
            AuthoritySource("clean", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: []));
        ShiftPlanCompileResult r1 = await pass1.CompileAsync(state);

        Assert.Equal(0, r1.Created);
        Assert.True(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        Assert.False(state.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        Assert.DoesNotContain((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        // The replay tombstone (v5) survives the bootstrap — this is the fix.
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone kept1));
        Assert.Equal(5L, kept1.Version);

        // Pass 2 at t0+15s: the delta REPLAYS the same row (v5). A genuinely recurs in the specs.
        // Because the kind is bootstrapped, exact-key durable bootstrap does not run; and because
        // the replay tombstone still remembers v5, the equal-version replay is idempotent — A is
        // NOT re-suppressed, so the new episode materializes.
        clock.Advance(TimeSpan.FromSeconds(15));
        _tracked.Clear();
        ShiftPlanTaskSpec aSpec = Spec(keyA, title: "a-new");
        ShiftPlanCompiler pass2 = BuildCompiler(clock,
            AuthoritySource("clean", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [aSpec]));
        ShiftPlanCompileResult r2 = await pass2.CompileAsync(state);

        Assert.Equal(1, r2.Created);
        Assert.Contains(_tracked, t => t.SourceId == keyA);
        Assert.True(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        Assert.DoesNotContain((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone kept2));
        Assert.Equal(5L, kept2.Version);

        // Pass 3 at t0+30s: a STRICTLY-NEWER dismissal of A (v6) arrives. It must re-suppress A,
        // so the recurrence no longer materializes, and advance the tombstone to v6.
        clock.Advance(TimeSpan.FromSeconds(15));
        delta = [(UserTaskSourceKind.FailureIncident, keyA, 6L)];
        _tracked.Clear();
        ShiftPlanCompiler pass3 = BuildCompiler(clock,
            AuthoritySource("clean", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [aSpec]));
        ShiftPlanCompileResult r3 = await pass3.CompileAsync(state);

        Assert.Equal(0, r3.Created);
        Assert.DoesNotContain(_tracked, t => t.SourceId == keyA);
        Assert.Contains((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        Assert.True(state.TryGetReplayTombstone((UserTaskSourceKind.FailureIncident, keyA), out ReplayTombstone kept3));
        Assert.Equal(6L, kept3.Version);

        // Exact per-pass delta watermarks and counts: passes 1 and 2 both read t0-15s (the replay),
        // pass 3 reads t0 (the strictly-newer dismissal); three reads total.
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(wmPass12, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(wmPass3, It.IsAny<CancellationToken>()),
            Times.Once);
        _tasks.Verify(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        // Exact-key durable bootstrap NEVER runs: pass 1 emits no keys, and passes 2-3 are on an
        // already-bootstrapped kind — so A's stale durable row can never be imported.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
    /// exact-key bootstrap re-establishes fail-closed suppression from the persisted row.
    /// </summary>
    [Fact]
    public async Task CompileAsync_RestartDiscardsClearedEvidence_RecoversDurableSuppressionFailClosed()
    {
        const string keyA = "failure:A";
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyA)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyA, 5L)]);

        // Pre-restart: A had been authoritatively cleared in this process's state.
        ShiftPlanSuppressionState preRestart = new();
        preRestart.MarkCleared((UserTaskSourceKind.FailureIncident, keyA), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(preRestart.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));

        // Restart: a fresh state has no in-memory cleared evidence or replay memory. A recurs
        // and its durable Skip/Dismiss row is recovered, so suppression is fail-closed until the
        // source successfully re-evaluates the key.
        ShiftPlanSuppressionState postRestart = new();
        Assert.Equal(0, postRestart.BootstrapExclusionCount);
        Assert.Equal(0, postRestart.ReplayTombstoneCount);
        Assert.False(postRestart.IsExcludedFromBootstrap((UserTaskSourceKind.FailureIncident, keyA)));
        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec(keyA)));
        ShiftPlanCompileResult result = await compiler.CompileAsync(postRestart);

        Assert.Equal(0, result.Created);
        Assert.Empty(_tracked);
        Assert.Contains((UserTaskSourceKind.FailureIncident, keyA), postRestart.SuppressedKeys);
        // Fail-closed recovery queried the durable row for exactly {A}, exactly once.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Count == 1 && keys.Single().SourceKind == UserTaskSourceKind.FailureIncident && keys.Single().SourceId == keyA),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Issue #823 (existing fail-closed continuity): cleared evidence is strictly per-key.
    /// A cleared key A must not block durable exact-key bootstrap of an unrelated,
    /// never-observed key C of the same kind — C stays suppressed while A's new episode
    /// materializes.
    /// </summary>
    [Fact]
    public async Task CompileAsync_ClearedKeyDoesNotBlockDurableBootstrapOfUnrelatedKey()
    {
        const string keyA = "failure:A";
        const string keyC = "failure:C";
        ShiftPlanSuppressionState state = new();
        state.MarkCleared((UserTaskSourceKind.FailureIncident, keyA), new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        // Only C has a durable Skip row; A is held back by its cleared evidence and never queried.
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyC)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, keyC, 5L)]);

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec(keyA, title: "a"), Spec(keyC, title: "c")));
        ShiftPlanCompileResult result = await compiler.CompileAsync(state);

        Assert.Equal(1, result.Created);
        Assert.Contains(_tracked, t => t.SourceId == keyA);
        Assert.DoesNotContain(_tracked, t => t.SourceId == keyC);
        Assert.Contains((UserTaskSourceKind.FailureIncident, keyC), state.SuppressedKeys);
        Assert.DoesNotContain((UserTaskSourceKind.FailureIncident, keyA), state.SuppressedKeys);
        // Exact-key bootstrap queried exactly {C} (A absent because it is cleared), once.
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Count == 1 && keys.Single().SourceKind == UserTaskSourceKind.FailureIncident && keys.Single().SourceId == keyC),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _tasks.Verify(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(k => k.SourceId == keyA)),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #823 (bounded lifecycle — age pruning): under a persistently colliding kind that
    /// never bootstraps, stale cleared evidence must not accumulate forever. Evidence older
    /// than the durable-suppression horizon is pruned each pass while active-window evidence
    /// survives (the kind is still unbootstrapped).
    /// </summary>
    [Fact]
    public async Task CompileAsync_PersistentCollision_PrunesAgedClearedEvidence_KeepsActiveEvidence()
    {
        const string keyB = "failure:B";
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MutableClock clock = new(t0);
        ShiftPlanSuppressionState state = new();

        // Seed one very old cleared key (41 days ago) and one recent cleared key (1 day ago).
        (UserTaskSourceKind, string) oldKey = (UserTaskSourceKind.FailureIncident, "failure:old");
        (UserTaskSourceKind, string) recentKey = (UserTaskSourceKind.FailureIncident, "failure:recent");
        state.MarkCleared(oldKey, version: 1, clearedAtUtc: t0.UtcDateTime.AddDays(-41));
        state.MarkCleared(recentKey, version: 2, clearedAtUtc: t0.UtcDateTime.AddDays(-1));
        Assert.Equal(2, state.BootstrapExclusionCount);
        Assert.Equal(2, state.ReplayTombstoneCount);

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // A persistent collision on B keeps the kind unbootstrapped, so pruning is the only
        // bound on cleared-evidence growth this pass.
        ShiftPlanTaskSpec bFirst = Spec(keyB, title: "b-first");
        ShiftPlanTaskSpec bSecond = Spec(keyB, title: "b-second");
        ShiftPlanCompiler compiler = BuildCompiler(clock,
            AuthoritySource("colliding", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [bFirst, bSecond]));
        ShiftPlanCompileResult result = await compiler.CompileAsync(state);

        Assert.Equal(0, result.Created);
        Assert.False(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        // The 41-day-old evidence is pruned from BOTH collections (older than the 30-day
        // horizon); the 1-day-old evidence survives in both while the kind stays unbootstrapped.
        Assert.False(state.IsExcludedFromBootstrap(oldKey));
        Assert.True(state.IsExcludedFromBootstrap(recentKey));
        Assert.Equal(1, state.BootstrapExclusionCount);
        Assert.False(state.TryGetReplayTombstone(oldKey, out _));
        Assert.True(state.TryGetReplayTombstone(recentKey, out _));
        Assert.Equal(1, state.ReplayTombstoneCount);
    }

    /// <summary>
    /// Issue #823 (bounded lifecycle — bootstrap drop): once a kind successfully bootstraps,
    /// its exact-key durable bootstrap no longer runs, so residual cleared evidence for that
    /// kind is no longer needed and is dropped, bounding growth.
    /// </summary>
    [Fact]
    public async Task CompileAsync_KindBootstraps_DropsResidualClearedEvidenceForThatKind()
    {
        const string keyA = "failure:A";
        DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        MutableClock clock = new(t0);
        ShiftPlanSuppressionState state = new();
        // A residual cleared key of the kind that is about to bootstrap this pass, plus a
        // cleared key of an unrelated kind that must be untouched.
        (UserTaskSourceKind, string) residual = (UserTaskSourceKind.FailureIncident, "failure:residual");
        (UserTaskSourceKind, string) otherKind = (UserTaskSourceKind.Maintenance, "maintenance:x");
        state.MarkCleared(residual, version: 1, clearedAtUtc: t0.UtcDateTime.AddDays(-1));
        state.MarkCleared(otherKind, version: 1, clearedAtUtc: t0.UtcDateTime.AddDays(-1));

        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // A clean (collision-free) authoritative pass over the FailureIncident kind → it
        // bootstraps, dropping residual FailureIncident cleared evidence.
        ShiftPlanCompiler compiler = BuildCompiler(clock,
            AuthoritySource("clean", UserTaskSourceKind.FailureIncident, originWatermark: 10,
                specs: [Spec(keyA)]));
        ShiftPlanCompileResult result = await compiler.CompileAsync(state);

        Assert.True(state.IsBootstrapped(UserTaskSourceKind.FailureIncident));
        // Bootstrap drops the DROPPABLE bootstrap-exclusion evidence for the clean kind...
        Assert.False(state.IsExcludedFromBootstrap(residual));
        // ...but the bounded replay tombstone MUST survive so an overlapped delta replay of
        // the same durable row after bootstrap stays idempotent and cannot re-suppress a
        // genuine recurrence (the #823 collision-free failure mode).
        Assert.True(state.TryGetReplayTombstone(residual, out ReplayTombstone kept));
        Assert.Equal(1L, kept.Version);
        // Bootstrap-exclusion evidence for an unrelated, still-unbootstrapped kind is preserved.
        Assert.True(state.IsExcludedFromBootstrap(otherKind));
    }

    /// <summary>
    /// Issue #823 (version invariants — direct state): version 0 is a valid first observation and
    /// an equal-version replay is idempotent, while a strictly-newer version re-suppresses and
    /// advances the replay tombstone. This is the version algebra the overlapped delta depends on.
    /// </summary>
    [Fact]
    public void SuppressionState_VersionZeroAndEqualReplayAreIdempotent_StrictlyNewerReSuppresses()
    {
        ShiftPlanSuppressionState state = new();
        (UserTaskSourceKind, string) keyA = (UserTaskSourceKind.FailureIncident, "failure:A");
        DateTime t = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        // Version 0 is a genuine first observation (0 > long.MinValue): it suppresses.
        Assert.True(state.ObserveDismissal(keyA, version: 0, observedAtUtc: t));
        Assert.Contains(keyA, state.SuppressedKeys);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone v0));
        Assert.Equal(0L, v0.Version);

        // An equal-version (0) replay is idempotent — no state change, returns false.
        Assert.False(state.ObserveDismissal(keyA, version: 0, observedAtUtc: t));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone v0Replay));
        Assert.Equal(0L, v0Replay.Version);

        // Version 1 is strictly newer than 0 → re-suppresses and advances the tombstone.
        Assert.True(state.ObserveDismissal(keyA, version: 1, observedAtUtc: t));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone v1));
        Assert.Equal(1L, v1.Version);
    }

    /// <summary>
    /// Issue #823 (replay memory survives clear — direct state): after a key is cleared, its
    /// replay tombstone is retained so an equal-version delta replay stays idempotent (does NOT
    /// re-suppress the cleared key), while a strictly-newer dismissal re-suppresses it and evicts
    /// the bootstrap-exclusion evidence.
    /// </summary>
    [Fact]
    public void SuppressionState_MarkClearedRetainsReplayTombstone_EqualReplayIdempotent_NewerReSuppresses()
    {
        ShiftPlanSuppressionState state = new();
        (UserTaskSourceKind, string) keyA = (UserTaskSourceKind.FailureIncident, "failure:A");
        DateTime t = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(state.ObserveDismissal(keyA, version: 5, observedAtUtc: t));
        state.MarkCleared(keyA, t);
        Assert.DoesNotContain(keyA, state.SuppressedKeys);
        Assert.True(state.IsExcludedFromBootstrap(keyA));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterClear));
        Assert.Equal(5L, afterClear.Version);

        // Equal-version replay after clear is idempotent: the cleared key is NOT re-suppressed.
        Assert.False(state.ObserveDismissal(keyA, version: 5, observedAtUtc: t));
        Assert.DoesNotContain(keyA, state.SuppressedKeys);
        Assert.True(state.IsExcludedFromBootstrap(keyA));

        // Strictly-newer dismissal re-suppresses and evicts the bootstrap-exclusion evidence.
        Assert.True(state.ObserveDismissal(keyA, version: 6, observedAtUtc: t));
        Assert.Contains(keyA, state.SuppressedKeys);
        Assert.False(state.IsExcludedFromBootstrap(keyA));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterNewer));
        Assert.Equal(6L, afterNewer.Version);
    }

    /// <summary>
    /// Issue #823 (two separated concepts — direct state): <c>MarkBootstrapped</c> drops the
    /// droppable bootstrap-exclusion evidence for a kind but MUST retain the bounded replay
    /// tombstone (the exact cycle-2 defect).
    /// </summary>
    [Fact]
    public void SuppressionState_MarkBootstrappedDropsExclusionButRetainsReplayTombstone()
    {
        ShiftPlanSuppressionState state = new();
        (UserTaskSourceKind, string) keyA = (UserTaskSourceKind.FailureIncident, "failure:A");
        DateTime t = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        state.MarkCleared(keyA, version: 5, clearedAtUtc: t);
        Assert.True(state.IsExcludedFromBootstrap(keyA));
        Assert.Equal(1, state.BootstrapExclusionCount);
        Assert.Equal(1, state.ReplayTombstoneCount);

        state.MarkBootstrapped(UserTaskSourceKind.FailureIncident);

        Assert.False(state.IsExcludedFromBootstrap(keyA));
        Assert.Equal(0, state.BootstrapExclusionCount);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone kept));
        Assert.Equal(5L, kept.Version);
        Assert.Equal(1, state.ReplayTombstoneCount);
    }

    /// <summary>
    /// Issue #823 (independent bounded pruning — direct state): bootstrap-exclusion evidence and
    /// replay tombstones prune on their own schedules against the injected-clock horizon, so
    /// pruning one collection never disturbs the other.
    /// </summary>
    [Fact]
    public void SuppressionState_PrunesExclusionsAndTombstonesIndependentlyByAge()
    {
        ShiftPlanSuppressionState state = new();
        DateTime now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime horizon = now.AddDays(-30);
        (UserTaskSourceKind, string) oldKey = (UserTaskSourceKind.FailureIncident, "failure:old");
        (UserTaskSourceKind, string) recentKey = (UserTaskSourceKind.FailureIncident, "failure:recent");
        state.MarkCleared(oldKey, version: 1, clearedAtUtc: now.AddDays(-41));
        state.MarkCleared(recentKey, version: 2, clearedAtUtc: now.AddDays(-1));

        // Pruning exclusions removes only the aged exclusion; both tombstones are untouched.
        state.PruneBootstrapExclusions(horizon);
        Assert.False(state.IsExcludedFromBootstrap(oldKey));
        Assert.True(state.IsExcludedFromBootstrap(recentKey));
        Assert.Equal(1, state.BootstrapExclusionCount);
        Assert.Equal(2, state.ReplayTombstoneCount);

        // Pruning tombstones removes only the aged tombstone, independent of the exclusion set.
        state.PruneReplayTombstones(horizon);
        Assert.False(state.TryGetReplayTombstone(oldKey, out _));
        Assert.True(state.TryGetReplayTombstone(recentKey, out _));
        Assert.Equal(1, state.ReplayTombstoneCount);
        Assert.Equal(1, state.BootstrapExclusionCount);
    }

    /// <summary>
    /// Issue #823 (monotonic replay memory — direct state): a lower bootstrap/delta version can
    /// never overwrite a higher replay tombstone, so no stale row can reopen the replay window.
    /// </summary>
    [Fact]
    public void SuppressionState_NoLowerVersionOverwritesHigherReplayTombstone()
    {
        ShiftPlanSuppressionState state = new();
        (UserTaskSourceKind, string) keyA = (UserTaskSourceKind.FailureIncident, "failure:A");
        DateTime t = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(state.ObserveDismissal(keyA, version: 5, observedAtUtc: t));

        // A durable-bootstrap recovery reporting an older version must not lower the tombstone.
        state.RecoverSuppression(keyA, version: 3, observedAtUtc: t);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterRecover));
        Assert.Equal(5L, afterRecover.Version);

        // An older delta row is a replay: idempotent, tombstone unchanged.
        Assert.False(state.ObserveDismissal(keyA, version: 2, observedAtUtc: t));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterOlder));
        Assert.Equal(5L, afterOlder.Version);

        // Clearing then a lower explicit clear version still cannot lower the tombstone.
        state.MarkCleared(keyA, version: 1, clearedAtUtc: t);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterClear));
        Assert.Equal(5L, afterClear.Version);
    }

    /// <summary>
    /// Issue #823 (legacy versionless-seed edge — direct state): a key present in
    /// <see cref="ShiftPlanSuppressionState.SuppressedKeys"/> with NO matching
    /// <see cref="ShiftPlanSuppressionState.SuppressedVersions"/> entry (a versionless/direct-seeded
    /// legacy row) must clear to a replay tombstone floored at the legacy version <c>0</c> — NOT
    /// <see cref="long.MinValue"/>. Otherwise an equal legacy-<c>0</c> durable/delta replay reads as
    /// strictly newer than the tombstone and wrongly re-suppresses a genuine recurrence after the
    /// kind bootstraps, reproducing #823 on the versionless-seed path. Proves the exact
    /// direct-seed → clear → bootstrap → equal-v0 replay sequence stays idempotent while a strictly
    /// newer (v1) dismissal still re-suppresses.
    /// </summary>
    [Fact]
    public void SuppressionState_VersionlessSeedClearedThenEqualZeroReplay_IsIdempotent_NewerReSuppresses()
    {
        ShiftPlanSuppressionState state = new();
        (UserTaskSourceKind, string) keyA = (UserTaskSourceKind.FailureIncident, "failure:A");
        DateTime t = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        // Direct/legacy seed: suppressed with NO recorded mutation version or replay memory.
        _ = state.SuppressedKeys.Add(keyA);
        Assert.False(state.TryGetReplayTombstone(keyA, out _));

        // Clear the versionless key: the tombstone must floor at legacy 0, not long.MinValue.
        state.MarkCleared(keyA, t);
        Assert.DoesNotContain(keyA, state.SuppressedKeys);
        Assert.True(state.IsExcludedFromBootstrap(keyA));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterClear));
        Assert.Equal(ShiftPlanSuppressionState.LegacySuppressionVersion, afterClear.Version);
        Assert.Equal(0L, afterClear.Version);

        // Kind bootstraps: exclusion evidence may drop, but the legacy-0 tombstone survives.
        state.MarkBootstrapped(UserTaskSourceKind.FailureIncident);
        Assert.False(state.IsExcludedFromBootstrap(keyA));
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterBootstrap));
        Assert.Equal(0L, afterBootstrap.Version);

        // Equal legacy-0 replay (durable row / overlapped delta) is idempotent: A is NOT re-suppressed.
        Assert.False(state.ObserveDismissal(keyA, version: 0, observedAtUtc: t));
        Assert.DoesNotContain(keyA, state.SuppressedKeys);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterReplay));
        Assert.Equal(0L, afterReplay.Version);

        // A strictly newer (v1) dismissal is a genuine new episode: it re-suppresses and advances.
        Assert.True(state.ObserveDismissal(keyA, version: 1, observedAtUtc: t));
        Assert.Contains(keyA, state.SuppressedKeys);
        Assert.True(state.TryGetReplayTombstone(keyA, out ReplayTombstone afterNewer));
        Assert.Equal(1L, afterNewer.Version);
    }

    [Fact]
    public async Task CompileAsync_SpecWithoutSourceKindOrSourceId_IsIgnored()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanTaskSpec bad = Spec(sourceId: "") with { SourceKind = UserTaskSourceKind.Unspecified };
        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], bad));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Empty(_tracked);
    }

    /// <summary>Fix F: a source key the user recently skipped/dismissed is not re-created.</summary>
    [Fact]
    public async Task CompileAsync_SuppressedSourceKey_DoesNotRecreateTask()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (UserTaskSourceKind.FailureIncident, "failure:1", 1L) });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Empty(_tracked);
    }

    /// <summary>
    /// Fix R3-6: a source that keeps producing the same key across passes must stay
    /// suppressed for the whole episode via <see cref="ShiftPlanSuppressionState"/>,
    /// even if a flat rolling-window DB query would say "no longer suppressed" on a
    /// later pass (simulated here by the mocked repository only reporting the
    /// skip once, on the pass immediately after it happened).
    /// </summary>
    [Fact]
    public async Task CompileAsync_SuppressionState_SkippedKeyStaysSuppressed_WhileSourceKeepsProducingIt()
    {
        ShiftPlanSuppressionState state = new();
        ShiftPlanTaskSpec spec = Spec("failure:1");
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        int bootstrapCall = 0;
        IReadOnlyCollection<(UserTaskSourceKind, string, long)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.FailureIncident, "failure:1", 1L)], // pass 2: DB reflects the user's skip
            Array.Empty<(UserTaskSourceKind, string, long)>(), // pass 3: DB query for "since last pass" finds nothing new
        ];
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(bootstrapResponses[Math.Min(bootstrapCall++, bootstrapResponses.Length - 1)]));

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], spec));

        ShiftPlanCompileResult r1 = await compiler.CompileAsync(state);
        Assert.Equal(1, r1.Created);

        // Pass 2: source still produces the exact same spec, but it was just skipped.
        ShiftPlanCompileResult r2 = await compiler.CompileAsync(state);
        Assert.Equal(0, r2.Created);

        // Pass 3: the source is still actively producing the key — episode continuity
        // must keep it suppressed even though this pass's incremental DB query alone
        // would no longer report it.
        ShiftPlanCompileResult r3 = await compiler.CompileAsync(state);
        Assert.Equal(0, r3.Created);
    }

    /// <summary>
    /// Fix R3-6: once a source stops producing a suppressed key for a full
    /// successful pass (its underlying condition cleared), suppression for that key
    /// is dropped — so if the source resumes producing it later, it is treated as a
    /// new occurrence and a fresh task materializes.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SuppressionState_SourceStopsThenResumes_NewTaskMaterializes()
    {
        ShiftPlanSuppressionState state = new();
        ShiftPlanTaskSpec spec = Spec("failure:1");
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        int bootstrapCall = 0;
        IReadOnlyCollection<(UserTaskSourceKind, string, long)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.FailureIncident, "failure:1", 1L)], // pass 2: DB reflects the user's skip
            Array.Empty<(UserTaskSourceKind, string, long)>(), // pass 3
            Array.Empty<(UserTaskSourceKind, string, long)>(), // pass 4
        ];
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(bootstrapResponses[Math.Min(bootstrapCall++, bootstrapResponses.Length - 1)]));

        // Pass 1: created.
        ShiftPlanCompiler compilerWithSpec = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], spec));
        ShiftPlanCompileResult r1 = await compilerWithSpec.CompileAsync(state);
        Assert.Equal(1, r1.Created);

        // Pass 2: user skipped it; source still producing the same spec -> suppressed.
        ShiftPlanCompileResult r2 = await compilerWithSpec.CompileAsync(state);
        Assert.Equal(0, r2.Created);

        // Pass 3: the underlying condition clears — the source succeeds but produces
        // NO specs this pass, so suppression for the now-absent key is dropped.
        ShiftPlanCompiler compilerNoSpec = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident]));
        ShiftPlanCompileResult r3 = await compilerNoSpec.CompileAsync(state);
        Assert.Equal(0, r3.Created);

        // Pass 4: the condition recurs — this is a new occurrence, not suppressed.
        ShiftPlanCompiler compilerResumed = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], spec));
        ShiftPlanCompileResult r4 = await compilerResumed.CompileAsync(state);
        Assert.Equal(1, r4.Created);
    }

    /// <summary>
    /// Fix R3-6: same episode-continuity semantics applied to a maintenance/idle-window
    /// task — dismissing it must not resurrect the identical window an hour later, but
    /// once the idle window ends (the source stops producing that key) and a genuinely
    /// new window later starts under the same source id, a new task is allowed.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SuppressionState_MaintenanceWindowEpisode_PersistsThenAllowsNewWindow()
    {
        ShiftPlanSuppressionState state = new();
        ShiftPlanTaskSpec spec = WindowSpec(DateTime.UtcNow.AddMinutes(-30), sourceId: "idle:printer:1");
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        int bootstrapCall = 0;
        IReadOnlyCollection<(UserTaskSourceKind, string, long)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.Maintenance, "idle:printer:1", 1L)], // pass 2: user dismissed it
            Array.Empty<(UserTaskSourceKind, string, long)>(), // pass 3: idle window ends (no specs)
            Array.Empty<(UserTaskSourceKind, string, long)>(), // pass 4: a new idle window starts
        ];
        _tasks.Setup(r => r.GetSuppressedSourceKeysAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(bootstrapResponses[Math.Min(bootstrapCall++, bootstrapResponses.Length - 1)]));

        // Pass 1: idle window task created.
        ShiftPlanCompiler compilerIdle = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance], spec));
        ShiftPlanCompileResult r1 = await compilerIdle.CompileAsync(state);
        Assert.Equal(1, r1.Created);

        // Pass 2: user dismissed it; the printer is still idle (same window persists) -> suppressed.
        ShiftPlanCompileResult r2 = await compilerIdle.CompileAsync(state);
        Assert.Equal(0, r2.Created);

        // Pass 3: the printer becomes busy — the idle window ends, source produces nothing.
        ShiftPlanCompiler compilerBusy = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance]));
        ShiftPlanCompileResult r3 = await compilerBusy.CompileAsync(state);
        Assert.Equal(0, r3.Created);

        // Pass 4: the printer goes idle again — a new window under the same source id
        // is a new episode, not suppressed.
        ShiftPlanCompiler compilerNewWindow = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance], spec));
        ShiftPlanCompileResult r4 = await compilerNewWindow.CompileAsync(state);
        Assert.Equal(1, r4.Created);
    }

    /// <summary>
    /// Fix R3-2: a <see cref="DbUpdateException"/> whose inner exception is a genuine
    /// foreign-key/constraint failure (NOT a unique-index race) must propagate rather
    /// than being silently swallowed — losing that failure would hide real data-
    /// integrity problems from the hosted service and operators.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SaveChangesThrowsForeignKeyViolation_Propagates()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        Microsoft.Data.Sqlite.SqliteException fkViolation = new(
            "FOREIGN KEY constraint failed", 19, 787); // SQLITE_CONSTRAINT_FOREIGNKEY
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("insert failed", fkViolation));

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        await Assert.ThrowsAsync<DbUpdateException>(() => compiler.CompileAsync());
    }

    /// <summary>
    /// Fix R3-2: a generic (non-SQLite-typed) <see cref="DbUpdateException"/> whose
    /// message does not match any known unique-violation pattern for any provider
    /// must also propagate — the classifier must not over-match.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SaveChangesThrowsUnrelatedDbFailure_Propagates()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("connection failed", new Exception("connection was reset by peer")));

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        await Assert.ThrowsAsync<DbUpdateException>(() => compiler.CompileAsync());
    }

    /// <summary>
    /// Fix R3-2: a <see cref="DbUpdateException"/> that IS a unique-index race
    /// (SQLite extended code 2067 here; Npgsql 23505 / SqlClient 2601/2627 in
    /// production) must be swallowed and recovered from — the affected tracked
    /// entities are detached so the next pass reconciles cleanly — instead of
    /// crashing the hosted-service tick.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SaveChangesThrowsUniqueViolation_RecoversWithoutThrowing()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        Microsoft.Data.Sqlite.SqliteException uniqueViolation = new(
            "UNIQUE constraint failed: UserTasks.SourceKind, UserTasks.SourceId", 19, 2067);
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("insert failed", uniqueViolation));

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        _tasks.Verify(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Fix R3-2: same as above but via the message-substring fallback (simulating a
    /// provider without a typed/reflectable exception available), confirming the
    /// Postgres-style message pattern is recognized too.
    /// </summary>
    [Fact]
    public async Task CompileAsync_SaveChangesThrowsPostgresStyleUniqueViolationMessage_RecoversWithoutThrowing()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("insert failed",
                new Exception("23505: duplicate key value violates unique constraint \"IX_UserTasks_SourceKind_SourceId\"")));

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn",
            [UserTaskSourceKind.FailureIncident], Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        _tasks.Verify(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Fix R3-5: if a concurrent user action (Skip/Dismiss) wins the race — reflected
    /// by <see cref="IUserTaskRepository.TryAutoCompleteAsync"/> returning
    /// <see langword="false"/> because the row is no longer Pending/InProgress in the
    /// DB — the compiler must NOT locally overwrite the task to Completed.
    /// </summary>
    [Fact]
    public async Task CompileAsync_AutoComplete_ConcurrentUserAction_DoesNotOverwriteStatus()
    {
        UserTask stale = new()
        {
            Id = Guid.NewGuid(),
            SourceKind = UserTaskSourceKind.FailureIncident,
            SourceId = "failure:gone",
            Status = UserTaskStatus.Pending,
            Title = "resolved elsewhere",
            LastMutationSequence = 1,
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stale });
        _tasks.Setup(r => r.TryAutoCompleteAsync(
                stale.Id,
                stale.LastMutationSequence,
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn", [UserTaskSourceKind.FailureIncident]));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, stale.Status);
        Assert.Null(stale.CompletedAt);
        _tasks.Verify(r => r.DetachTrackedAsync(It.Is<IEnumerable<UserTask>>(e => e.Contains(stale)), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>Fix G: sub-tolerance window-start drift on an otherwise-identical task is not written.</summary>
    [Fact]
    public async Task CompileAsync_WindowStartDriftWithinTolerance_DoesNotUpdate()
    {
        DateTime windowStart = DateTime.UtcNow.AddHours(2);
        ShiftPlanTaskSpec spec = WindowSpec(windowStart);
        // Existing task's window-start lags by 2 minutes (< 5 min tolerance).
        UserTask existing = ExistingFromSpec(spec, windowStart.AddMinutes(-2));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance], spec));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Updated);
        Assert.Empty(_trackedUpdated);
        // The stored window-start is preserved (not rewritten to the drifted value).
        Assert.Equal(windowStart.AddMinutes(-2), existing.WindowStartUtc);
    }

    /// <summary>
    /// Fix R3-7 (supersedes Fix G): <see cref="IdleWindowService"/> always anchors an
    /// incoming window's start to a fresh <c>UtcNow</c>, so ANY amount of wall-clock
    /// drift on an otherwise-unchanged window (same <c>WindowEndUtc</c>) must NOT
    /// rewrite the stored start — otherwise a continuously-idle printer's displayed
    /// episode start resets indefinitely every pass. This replaces the old Fix G
    /// "beyond tolerance → update" assertion, which is no longer correct.
    /// </summary>
    [Fact]
    public async Task CompileAsync_WindowStartDriftAnyAmount_PreservesStoredStart_WhenWindowEndUnchanged()
    {
        DateTime windowStart = DateTime.UtcNow.AddHours(2);
        ShiftPlanTaskSpec spec = WindowSpec(windowStart);
        // Existing task's window-start lags by 10 minutes — well beyond the old 5-min
        // tolerance — but WindowEndUtc is unchanged (null on both sides), so under
        // Fix R3-7 this is wall-clock drift, not a genuine episode boundary change.
        UserTask existing = ExistingFromSpec(spec, windowStart.AddMinutes(-10));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance], spec));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Updated);
        Assert.Empty(_trackedUpdated);
        Assert.Equal(windowStart.AddMinutes(-10), existing.WindowStartUtc);
    }

    /// <summary>
    /// Fix R3-7: a genuine boundary change (the window's end materially changes —
    /// e.g. an open-ended window becomes bounded) IS a real episode transition, so
    /// the window start is rewritten to the incoming value along with the end.
    /// </summary>
    [Fact]
    public async Task CompileAsync_WindowEndBoundaryChanges_RewritesWindowStartAndEnd()
    {
        DateTime windowStart = DateTime.UtcNow.AddHours(2);
        DateTime newWindowEnd = windowStart.AddHours(4);
        ShiftPlanTaskSpec spec = WindowSpec(windowStart) with { WindowEndUtc = newWindowEnd };
        // Existing task has a materially earlier stored start AND a null (open-ended) end —
        // the incoming spec now bounds the window, a genuine transition.
        UserTask existing = ExistingFromSpec(WindowSpec(windowStart.AddMinutes(-10)), windowStart.AddMinutes(-10));
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existing });

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("maint",
            [UserTaskSourceKind.Maintenance], spec));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Updated);
        Assert.Single(_trackedUpdated);
        Assert.Equal(windowStart, existing.WindowStartUtc);
        Assert.Equal(newWindowEnd, existing.WindowEndUtc);
    }

    /// <summary>
    /// Fix R3-7: simulates a continuously-idle printer being compiled every tick
    /// (each producing a fresh <c>WindowStartUtc</c> anchored to "now" at that
    /// instant, same as the real <see cref="IdleWindowService"/>) over many passes.
    /// The persisted window start must never change once the episode has begun.
    /// </summary>
    [Fact]
    public async Task CompileAsync_ContinuouslyIdlePrinter_WindowStartNeverRewritten_AcrossManyPasses()
    {
        DateTime episodeStart = DateTime.UtcNow.AddHours(-1);
        UserTask existing = ExistingFromSpec(WindowSpec(episodeStart), episodeStart);
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { existing });

        for (int tick = 0; tick < 240; tick++)
        {
            // Each tick, IdleWindowService would report a fresh "now" as the start —
            // never the originally-persisted value.
            ShiftPlanTaskSpec tickSpec = WindowSpec(DateTime.UtcNow.AddSeconds(tick));
            ShiftPlanCompiler compiler = BuildCompiler(new StubSource("maint",
                [UserTaskSourceKind.Maintenance], tickSpec));

            await compiler.CompileAsync();
        }

        Assert.Equal(episodeStart, existing.WindowStartUtc);
    }

    private ShiftPlanCompiler BuildCompiler(params IShiftPlanTaskSource[] sources) =>
        new(sources, _tasks.Object, NullLogger<ShiftPlanCompiler>.Instance);

    private ShiftPlanCompiler BuildCompiler(TimeProvider clock, params IShiftPlanTaskSource[] sources) =>
        new(sources, _tasks.Object, NullLogger<ShiftPlanCompiler>.Instance, clock);

    /// <summary>
    /// A hand-advanceable <see cref="TimeProvider"/> so a test can drive the compiler's
    /// pass clock deterministically across passes (Fix R4-3 watermark-overlap test).
    /// </summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static ShiftPlanTaskSpec WindowSpec(DateTime windowStart, string sourceId = "maintenance:1") => new(
        TaskType: UserTaskType.MaintenanceDue,
        SourceKind: UserTaskSourceKind.Maintenance,
        SourceId: sourceId,
        Title: "idle",
        Description: null,
        Priority: UserTaskPriority.Normal,
        AnchorKind: UserTaskAnchorKind.Window,
        AnchorAtUtc: null,
        WindowStartUtc: windowStart,
        WindowEndUtc: null,
        EntityType: "Printer",
        EntityId: PrinterId);

    private static UserTask ExistingFromSpec(ShiftPlanTaskSpec spec, DateTime windowStart) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        Status = UserTaskStatus.Pending,
        TaskType = spec.TaskType,
        SourceKind = spec.SourceKind,
        SourceId = spec.SourceId,
        EntityType = spec.EntityType,
        EntityId = spec.EntityId,
        Title = spec.Title,
        Description = spec.Description,
        Priority = spec.Priority,
        AnchorKind = spec.AnchorKind,
        AnchorAtUtc = spec.AnchorAtUtc,
        WindowStartUtc = windowStart,
        WindowEndUtc = spec.WindowEndUtc,
        DueAt = spec.DueAt,
    };

    private static UserTask OpenTask(
        UserTaskSourceKind sourceKind,
        string sourceId,
        long sequence)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceKind = sourceKind,
            SourceId = sourceId,
            Status = UserTaskStatus.Pending,
            Title = "task",
            LastMutationSequence = sequence,
        };

    private static IShiftPlanTaskSource AuthoritySource(
        string name,
        UserTaskSourceKind kind,
        long? originWatermark,
        IReadOnlySet<string>? preservedSourceIds = null,
        IReadOnlyList<ShiftPlanTaskSpec>? specs = null)
    {
        ShiftPlanSourceResult result = new(specs ?? [], originWatermark)
        {
            Authority = new ShiftPlanSourceAuthority(
            [
                new ShiftPlanKindAuthority(
                    kind,
                    IsAuthoritativeComplete: true,
                    preservedSourceIds ?? new HashSet<string>(StringComparer.Ordinal),
                    IncompleteReasons: []),
            ]),
        };
        return new ControlledSource(name, [kind], specs ?? [], result);
    }

    private static ShiftPlanTaskSpec Spec(string sourceId = "failure:1", string title = "t") => new(
        TaskType: UserTaskType.FailureClear,
        SourceKind: UserTaskSourceKind.FailureIncident,
        SourceId: sourceId,
        Title: title,
        Description: null,
        Priority: UserTaskPriority.High,
        AnchorKind: UserTaskAnchorKind.Now,
        AnchorAtUtc: DateTime.UtcNow,
        WindowStartUtc: null,
        WindowEndUtc: null,
        EntityType: "Printer",
        EntityId: PrinterId);

    private sealed class StubSource(
        string name,
        IReadOnlyCollection<UserTaskSourceKind> ownedKinds,
        params ShiftPlanTaskSpec[] specs) : IShiftPlanTaskSource
    {
        public string SourceName { get; } = name;
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = ownedKinds;
        public Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct)
            => Task.FromResult(new ShiftPlanSourceResult(specs, OriginWatermark: 100)
            {
                Authority = new ShiftPlanSourceAuthority(
                [
                    .. ownedKinds.Select(kind => new ShiftPlanKindAuthority(
                        kind,
                        IsAuthoritativeComplete: true,
                        PreservedSourceIds: new HashSet<string>(StringComparer.Ordinal),
                        IncompleteReasons: [])),
                ]),
            });
    }

    private sealed class ThrowingSource(IReadOnlyCollection<UserTaskSourceKind> ownedKinds) : IShiftPlanTaskSource
    {
        public string SourceName => "boom";
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = ownedKinds;
        public Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated");
    }

    private sealed class ControlledSource(
        string name,
        IReadOnlyCollection<UserTaskSourceKind> ownedKinds,
        IReadOnlyList<ShiftPlanTaskSpec> specs,
        ShiftPlanSourceResult result) : IShiftPlanTaskSource
    {
        public string SourceName { get; } = name;
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = ownedKinds;
        public Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct)
            => Task.FromResult(result with { Specs = specs });
    }
}
