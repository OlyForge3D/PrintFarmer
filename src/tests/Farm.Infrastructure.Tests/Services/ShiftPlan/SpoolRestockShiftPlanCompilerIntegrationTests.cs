using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.ShiftPlan;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.ShiftPlan;

public sealed class SpoolRestockShiftPlanCompilerIntegrationTests
{
    private const string SourceId = "spoolrestock:v1:42:abc";
    private readonly Mock<IUserTaskRepository> _tasks = new();

    public SpoolRestockShiftPlanCompilerIntegrationTests()
    {
        _tasks
            .Setup(repository => repository.GetSuppressedSourceKeysAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks
            .Setup(repository => repository.GetOpenSuppressedByKeysAsync(
                It.IsAny<IReadOnlyCollection<(UserTaskSourceKind, string)>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _tasks
            .Setup(repository => repository.DetachTrackedAsync(
                It.IsAny<IEnumerable<UserTask>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tasks
            .Setup(repository => repository.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task CompileAsync_PreservedRestockKey_DoesNotUpdateOrComplete()
    {
        UserTask existing = OpenRestockTask(sequence: 4);
        _tasks
            .Setup(repository => repository.GetOpenCompilerTasksAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        ShiftPlanCompiler compiler = CreateCompiler(
            Result(
                origin: 10,
                preservedSourceIds: new HashSet<string>([SourceId], StringComparer.Ordinal)));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        _tasks.Verify(
            repository => repository.TrackUpdateAsync(
                It.IsAny<UserTask>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _tasks.Verify(
            repository => repository.TryAutoCompleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompileAsync_DesiredAfterPreserved_UpdatesSameOpenOccurrence()
    {
        UserTask existing = OpenRestockTask(sequence: 4);
        existing.Title = "Old title";
        _tasks
            .Setup(repository => repository.GetOpenCompilerTasksAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        UserTask? updated = null;
        _tasks
            .Setup(repository => repository.TrackUpdateAsync(
                It.IsAny<UserTask>(),
                It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((task, _) => updated = task)
            .Returns(Task.CompletedTask);
        ShiftPlanCompiler compiler = CreateCompiler(
            Result(origin: 10, specs: [Spec(title: "Refreshed title")]));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.AutoCompleted);
        Assert.Same(existing, updated);
        Assert.Equal("Refreshed title", existing.Title);
        Assert.Equal(SourceId, existing.SourceId);
    }

    [Fact]
    public async Task CompileAsync_AuthoritativeOccurrenceRemoval_CompletesOnce()
    {
        UserTask existing = OpenRestockTask(sequence: 4);
        _tasks
            .Setup(repository => repository.GetOpenCompilerTasksAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        _tasks
            .Setup(repository => repository.TryAutoCompleteAsync(
                existing.Id,
                existing.LastMutationSequence,
                10,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        ShiftPlanCompiler compiler = CreateCompiler(Result(origin: 10));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(1, result.AutoCompleted);
        _tasks.Verify(
            repository => repository.TryAutoCompleteAsync(
                existing.Id,
                existing.LastMutationSequence,
                10,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(11, 10)]
    public async Task CompileAsync_UncoveredMutationSequence_PreservesRestockOccurrence(
        long sequence,
        long origin)
    {
        UserTask existing = OpenRestockTask(sequence);
        _tasks
            .Setup(repository => repository.GetOpenCompilerTasksAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        ShiftPlanCompiler compiler = CreateCompiler(Result(origin));

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(0, result.AutoCompleted);
        _tasks.Verify(
            repository => repository.TryAutoCompleteAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private ShiftPlanCompiler CreateCompiler(ShiftPlanSourceResult result) =>
        new(
            [new RestockSource(result)],
            _tasks.Object,
            NullLogger<ShiftPlanCompiler>.Instance);

    private static ShiftPlanSourceResult Result(
        long? origin,
        IReadOnlySet<string>? preservedSourceIds = null,
        IReadOnlyList<ShiftPlanTaskSpec>? specs = null) =>
        new(specs ?? [], origin)
        {
            Authority = new ShiftPlanSourceAuthority(
            [
                new ShiftPlanKindAuthority(
                    UserTaskSourceKind.SpoolReorder,
                    IsAuthoritativeComplete: true,
                    PreservedSourceIds: preservedSourceIds
                        ?? new HashSet<string>(StringComparer.Ordinal),
                    IncompleteReasons: []),
            ]),
        };

    private static ShiftPlanTaskSpec Spec(string title) =>
        new(
            UserTaskType.SpoolRestock,
            UserTaskSourceKind.SpoolReorder,
            SourceId,
            title,
            "description",
            UserTaskPriority.Normal,
            UserTaskAnchorKind.At,
            DateTime.UtcNow.AddHours(1),
            null,
            null,
            "Spool",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DateTime.UtcNow.AddHours(2));

    private static UserTask OpenRestockTask(long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            TaskType = UserTaskType.SpoolRestock,
            SourceKind = UserTaskSourceKind.SpoolReorder,
            SourceId = SourceId,
            Status = UserTaskStatus.Pending,
            Title = "Restock spool",
            Description = "description",
            Priority = UserTaskPriority.Normal,
            AnchorKind = UserTaskAnchorKind.At,
            AnchorAtUtc = DateTime.UtcNow.AddHours(1),
            EntityType = "Spool",
            EntityId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DueAt = DateTime.UtcNow.AddHours(2),
            LastMutationSequence = sequence,
        };

    private sealed class RestockSource(ShiftPlanSourceResult result) : IShiftPlanTaskSource
    {
        public string SourceName => "spool-restock-test";

        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
            [UserTaskSourceKind.SpoolReorder];

        public Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct) =>
            Task.FromResult(result);
    }
}
