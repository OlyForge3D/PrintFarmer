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
/// resolution, and per-source failure isolation.
/// </summary>
public class ShiftPlanCompilerTests
{
    private static readonly Guid PrinterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Mock<IUserTaskRepository> _tasks = new();
    private readonly List<UserTask> _added = new();
    private readonly List<UserTask> _updated = new();

    public ShiftPlanCompilerTests()
    {
        _tasks.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((t, _) => _added.Add(t))
            .Returns(Task.CompletedTask);
        _tasks.Setup(r => r.UpdateAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((t, _) => _updated.Add(t))
            .Returns(Task.CompletedTask);
        _tasks.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task CompileAsync_CreatesTaskForNewSpec()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn", Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Single(_added);
        Assert.Equal("failure:1", _added[0].SourceId);
        Assert.Equal(UserTaskSourceKind.FailureIncident, _added[0].SourceKind);
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

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn", Spec("failure:1", title: "new")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Same(existing, _updated.Single());
        Assert.Equal("new", existing.Title);
        Assert.Equal(UserTaskStatus.InProgress, existing.Status);
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

        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn"));

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
            new ThrowingSource(),
            new StubSource("attn", Spec("failure:1")));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.SourceFailures);
    }

    [Fact]
    public async Task CompileAsync_SpecWithoutSourceKindOrSourceId_IsIgnored()
    {
        _tasks.Setup(r => r.GetOpenCompilerTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        ShiftPlanTaskSpec bad = Spec(sourceId: "") with { SourceKind = UserTaskSourceKind.Unspecified };
        ShiftPlanCompiler compiler = BuildCompiler(new StubSource("attn", bad));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Empty(_added);
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

    private sealed class StubSource : IShiftPlanTaskSource
    {
        private readonly ShiftPlanTaskSpec[] _specs;
        public StubSource(string name, params ShiftPlanTaskSpec[] specs) { SourceName = name; _specs = specs; }
        public string SourceName { get; }
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ShiftPlanTaskSpec>>(_specs);
    }

    private sealed class ThrowingSource : IShiftPlanTaskSource
    {
        public string SourceName => "boom";
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated");
    }
}
