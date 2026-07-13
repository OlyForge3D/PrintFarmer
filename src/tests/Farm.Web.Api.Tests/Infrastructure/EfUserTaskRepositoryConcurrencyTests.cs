using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.ShiftPlan;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Concurrency guards for the shift-plan compiler persistence (issue #713 round 2):
/// Fix D (no lost-update when a user mutates a task mid-compile), Fix E (the unique
/// filtered index prevents duplicate open compiler tasks and the in-process gate
/// serializes passes). These use two <see cref="AppDbContext"/> instances over one
/// open SQLite connection so both see the same rows.
/// </summary>
public class EfUserTaskRepositoryConcurrencyTests
{
    /// <summary>
    /// Fix D: the compiler holds a tracked task and writes only anchor fields. A user
    /// who completes the task during the pass must not have their Status clobbered, and
    /// the compiler's anchor change must still persist.
    /// </summary>
    [Fact]
    public async Task TrackUpdateAsync_ConcurrentUserStatusChange_IsNotClobbered()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        Guid taskId = Guid.NewGuid();

        await using (AppDbContext seed = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true))
        {
            _ = seed.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:1", UserTaskStatus.Pending, taskId));
            _ = await seed.SaveChangesAsync();
        }

        // Compiler context loads the tracked entity (mirrors GetOpenCompilerTasksAsync).
        await using AppDbContext compilerCtx = TestInfrastructure.TestHelpers.CreateContext(connection);
        EfUserTaskRepository compilerRepo = new(compilerCtx);
        UserTask compilerTask = Assert.Single(await compilerRepo.GetOpenCompilerTasksAsync());

        // Meanwhile, a user completes the task in a separate context and saves.
        await using (AppDbContext userCtx = TestInfrastructure.TestHelpers.CreateContext(connection))
        {
            UserTask userTask = await userCtx.UserTasks.SingleAsync(t => t.Id == taskId);
            userTask.Status = UserTaskStatus.Completed;
            userTask.CompletedAt = DateTime.UtcNow;
            _ = await userCtx.SaveChangesAsync();
        }

        // Compiler mutates only anchor fields, then saves via the fixed TrackUpdateAsync.
        DateTime newWindowStart = DateTime.UtcNow.AddHours(3);
        compilerTask.WindowStartUtc = newWindowStart;
        await compilerRepo.TrackUpdateAsync(compilerTask);
        await compilerRepo.SaveChangesAsync();

        await using AppDbContext verifyCtx = TestInfrastructure.TestHelpers.CreateContext(connection);
        UserTask finalTask = await verifyCtx.UserTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);

        // No lost update either direction: user's status survives AND compiler's anchor persists.
        finalTask.Status.Should().Be(UserTaskStatus.Completed);
        finalTask.WindowStartUtc.Should().BeCloseTo(newWindowStart, TimeSpan.FromSeconds(1));
    }

    /// <summary>Fix E: two open tasks with the same (SourceKind, SourceId) violate the unique filtered index.</summary>
    [Fact]
    public async Task UniqueSourceIndex_TwoOpenTasksSameSource_ThrowsOnInsert()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true);

        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:dup", UserTaskStatus.Pending));
        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:dup", UserTaskStatus.Pending));

        Func<Task> act = async () => await ctx.SaveChangesAsync();
        _ = await act.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Fix E: the filter excludes terminal statuses, so an open + completed pair for one source is allowed.</summary>
    [Fact]
    public async Task UniqueSourceIndex_OpenPlusTerminalSameSource_IsAllowed()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true);

        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:s", UserTaskStatus.Pending));
        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:s", UserTaskStatus.Completed));

        Func<Task> act = async () => await ctx.SaveChangesAsync();
        _ = await act.Should().NotThrowAsync();
    }

    /// <summary>Fix E: legacy tasks with a null SourceId are excluded from the filtered unique index.</summary>
    [Fact]
    public async Task UniqueSourceIndex_NullSourceId_IsAllowed()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true);

        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Unspecified, null, UserTaskStatus.Pending));
        _ = ctx.UserTasks.Add(NewOpenTask(UserTaskSourceKind.Unspecified, null, UserTaskStatus.Pending));

        Func<Task> act = async () => await ctx.SaveChangesAsync();
        _ = await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Fix E: two compile passes for the same source spec produce exactly one open row.
    /// The in-process gate serializes them, and the unique index is the cross-process backstop.
    /// </summary>
    [Fact]
    public async Task CompileAsync_TwoConcurrentPasses_SameSource_CreatesExactlyOneOpenRow()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true);
        EfUserTaskRepository repo = new(ctx);

        ShiftPlanTaskSpec spec = new(
            TaskType: UserTaskType.MaintenanceDue,
            SourceKind: UserTaskSourceKind.Maintenance,
            SourceId: "maintenancealert:race",
            Title: "idle",
            Description: null,
            Priority: UserTaskPriority.Normal,
            AnchorKind: UserTaskAnchorKind.Window,
            AnchorAtUtc: null,
            WindowStartUtc: DateTime.UtcNow.AddHours(1),
            WindowEndUtc: null,
            EntityType: "Printer",
            EntityId: Guid.NewGuid());

        ShiftPlanCompiler compiler = new(
            new[] { new SingleSpecSource(spec) },
            repo,
            NullLogger<ShiftPlanCompiler>.Instance);

        await Task.WhenAll(compiler.CompileAsync(), compiler.CompileAsync());

        int open = await ctx.UserTasks.CountAsync(t =>
            t.SourceKind == UserTaskSourceKind.Maintenance &&
            t.SourceId == "maintenancealert:race" &&
            (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress));

        open.Should().Be(1);
    }

    /// <summary>
    /// Fix R5-C: the profile-import append path writes only if the row is still open.
    /// A user Skip that commits after the import loaded the detached task must win
    /// completely: Status remains Skipped and the terminal row does not accrete the
    /// import's RelatedEntityIdsJson/Description changes.
    /// </summary>
    [Fact]
    public async Task TryUpdateFieldsIfOpenAsync_ProfileImportRacesUserSkip_TerminalRowUnchanged()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        Guid taskId = Guid.NewGuid();
        Guid firstPrinter = Guid.NewGuid();
        Guid importedPrinter = Guid.NewGuid();

        await using (AppDbContext seed = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true))
        {
            UserTask task = NewOpenTask(UserTaskSourceKind.Unspecified, null, UserTaskStatus.Pending, taskId);
            task.TaskType = UserTaskType.ProfileImport;
            task.RelatedEntityIdsJson = $"[\"{firstPrinter}\"]";
            task.Description = "1 printer waiting for slicer profiles";
            _ = seed.UserTasks.Add(task);
            _ = await seed.SaveChangesAsync();
        }

        // Import path loads the task detached (mirrors GetByEntityAsync's no-tracking read).
        UserTask detached;
        await using (AppDbContext readCtx = TestInfrastructure.TestHelpers.CreateContext(connection))
        {
            detached = await readCtx.UserTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        }

        // Meanwhile a user skips the task in a separate context.
        await using (AppDbContext userCtx = TestInfrastructure.TestHelpers.CreateContext(connection))
        {
            UserTask userTask = await userCtx.UserTasks.SingleAsync(t => t.Id == taskId);
            userTask.Status = UserTaskStatus.Skipped;
            userTask.UpdatedAt = DateTime.UtcNow;
            _ = await userCtx.SaveChangesAsync();
        }

        // Import path tries to patch only its own columns, but the row is no longer open.
        string importedJson = $"[\"{firstPrinter}\",\"{importedPrinter}\"]";
        const string importedDescription = "2 printers waiting for slicer profiles";
        detached.RelatedEntityIdsJson = importedJson;
        detached.Description = importedDescription;

        await using (AppDbContext importCtx = TestInfrastructure.TestHelpers.CreateContext(connection))
        {
            EfUserTaskRepository importRepo = new(importCtx);
            bool updated = await importRepo.TryUpdateFieldsIfOpenAsync(
                detached,
                [nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)]);
            updated.Should().BeFalse();
        }

        await using AppDbContext verifyCtx = TestInfrastructure.TestHelpers.CreateContext(connection);
        UserTask finalTask = await verifyCtx.UserTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);

        finalTask.Status.Should().Be(UserTaskStatus.Skipped);
        finalTask.RelatedEntityIdsJson.Should().Be($"[\"{firstPrinter}\"]");
        finalTask.Description.Should().Be("1 printer waiting for slicer profiles");
    }

    /// <summary>
    /// Fix R5-E: on process restart, bootstrap suppression is episode-aware instead of
    /// time-window based. A source key the user skipped 30 days ago is still suppressed
    /// when the first live pass proves that exact source key is currently active.
    /// </summary>
    [Fact]
    public async Task CompileAsync_Bootstrap_ActiveSuppressedSourceOlderThanSevenDays_DoesNotRecreate()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        DateTime now = DateTime.UtcNow;

        await using (AppDbContext seed = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true))
        {
            UserTask old = NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:old", UserTaskStatus.Skipped);
            old.UpdatedAt = now.AddDays(-30);
            _ = seed.UserTasks.Add(old);
            _ = await seed.SaveChangesAsync();
        }

        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection);
        EfUserTaskRepository repo = new(ctx);

        ShiftPlanTaskSpec activeSpec = new(
            TaskType: UserTaskType.MaintenanceDue,
            SourceKind: UserTaskSourceKind.Maintenance,
            SourceId: "maintenancealert:old",
            Title: "still active",
            Description: null,
            Priority: UserTaskPriority.Normal,
            AnchorKind: UserTaskAnchorKind.Window,
            AnchorAtUtc: null,
            WindowStartUtc: now.AddHours(1),
            WindowEndUtc: null,
            EntityType: "Printer",
            EntityId: Guid.NewGuid());

        ShiftPlanCompiler compiler = new(
            new[] { new SingleSpecSource(activeSpec) },
            repo,
            NullLogger<ShiftPlanCompiler>.Instance);

        ShiftPlanSuppressionState state = new(); // fresh: LastPassAtUtc == null → bootstrap path
        ShiftPlanCompileResult first = await compiler.CompileAsync(state);
        ShiftPlanCompileResult second = await compiler.CompileAsync(state);

        first.Created.Should().Be(0);
        second.Created.Should().Be(0);
        state.SuppressedKeys.Should().Contain((UserTaskSourceKind.Maintenance, "maintenancealert:old"));
        int openRows = await ctx.UserTasks.CountAsync(t =>
            t.SourceKind == UserTaskSourceKind.Maintenance &&
            t.SourceId == "maintenancealert:old" &&
            (t.Status == UserTaskStatus.Pending || t.Status == UserTaskStatus.InProgress));
        openRows.Should().Be(0);
    }

    private static UserTask NewOpenTask(
        UserTaskSourceKind sourceKind,
        string? sourceId,
        UserTaskStatus status,
        Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Title = "task",
            TaskType = UserTaskType.MaintenanceDue,
            Status = status,
            Priority = UserTaskPriority.Normal,
            SourceKind = sourceKind,
            SourceId = sourceId,
            AnchorKind = UserTaskAnchorKind.Window,
            WindowStartUtc = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private sealed class SingleSpecSource(ShiftPlanTaskSpec spec) : IShiftPlanTaskSource
    {
        private readonly ShiftPlanTaskSpec _spec = spec;

        public string SourceName => "single";
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = [UserTaskSourceKind.Maintenance];
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ShiftPlanTaskSpec>>([_spec]);
    }

    private sealed class NoSpecSource(IReadOnlyCollection<UserTaskSourceKind> ownedKinds) : IShiftPlanTaskSource
    {
        public string SourceName => "nospec";
        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } = ownedKinds;
        public Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ShiftPlanTaskSpec>>([]);
    }
}
