using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.ShiftPlan;
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

    private ShiftPlanCompiler BuildCompiler(params IShiftPlanTaskSource[] sources) =>
        new(sources, _tasks.Object, NullLogger<ShiftPlanCompiler>.Instance);

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

