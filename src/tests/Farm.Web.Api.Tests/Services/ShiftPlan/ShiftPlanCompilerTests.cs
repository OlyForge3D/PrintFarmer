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
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string)>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string)>());
        // Fix R3-5: default auto-complete to "won the race" so existing auto-complete
        // tests behave as before unless a test explicitly overrides this.
        _tasks.Setup(r => r.TryAutoCompleteAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
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
    /// Fix R4-1 (end-to-end): a scorer outage that makes the idle-window service report
    /// an alerted printer as indeterminate must cause the REAL maintenance source to
    /// fail closed (throw), so the compiler's per-source isolation preserves the open
    /// maintenance task instead of auto-completing it. This exercises the full wiring
    /// (indeterminate idle-window → source throw → compiler preservation), not just the
    /// source in isolation.
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

        Assert.Equal(1, result.SourceFailures);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Equal(UserTaskStatus.Pending, maintenanceTask.Status);
        Assert.Null(maintenanceTask.CompletedAt);
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
                IReadOnlyCollection<(UserTaskSourceKind, string)> observed = since <= skipUpdatedAt
                    ? [(UserTaskSourceKind.FailureIncident, "failure:1")]
                    : Array.Empty<(UserTaskSourceKind, string)>();
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
            .ReturnsAsync(Array.Empty<(UserTaskSourceKind, string)>());
        _tasks.Setup(r => r.GetOpenSuppressedByKeysAsync(
                It.Is<IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId)>>(keys =>
                    keys.Any(key => key.SourceKind == UserTaskSourceKind.FailureIncident
                        && key.SourceId == "failure:pre-restart")),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([(UserTaskSourceKind.FailureIncident, "failure:pre-restart")]);

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
            .ReturnsAsync(new[] { (UserTaskSourceKind.FailureIncident, "failure:1") });

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
        IReadOnlyCollection<(UserTaskSourceKind, string)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.FailureIncident, "failure:1")], // pass 2: DB reflects the user's skip
            Array.Empty<(UserTaskSourceKind, string)>(), // pass 3: DB query for "since last pass" finds nothing new
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
        IReadOnlyCollection<(UserTaskSourceKind, string)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.FailureIncident, "failure:1")], // pass 2: DB reflects the user's skip
            Array.Empty<(UserTaskSourceKind, string)>(), // pass 3
            Array.Empty<(UserTaskSourceKind, string)>(), // pass 4
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
        IReadOnlyCollection<(UserTaskSourceKind, string)>[] bootstrapResponses =
        [
            [(UserTaskSourceKind.Maintenance, "idle:printer:1")], // pass 2: user dismissed it
            Array.Empty<(UserTaskSourceKind, string)>(), // pass 3: idle window ends (no specs)
            Array.Empty<(UserTaskSourceKind, string)>(), // pass 4: a new idle window starts
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
        };
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stale });
        _tasks.Setup(r => r.TryAutoCompleteAsync(stale.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
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
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ShiftPlanTaskSpec>>(specs);
    }

    private sealed class ThrowingSource(IReadOnlyCollection<UserTaskSourceKind> ownedKinds) : IShiftPlanTaskSource
    {
        public string SourceName => "boom";
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = ownedKinds;
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated");
    }
}
