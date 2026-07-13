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
    /// Fix R4-2: the profile-import path patches only its own columns via
    /// UpdateFieldsAsync. A user Skip that commits after the import loaded the (detached,
    /// no-tracking) task must NOT be clobbered back to Pending, yet the import's
    /// RelatedEntityIdsJson/Description changes must still persist — because
    /// UpdateFieldsAsync marks only those columns modified and never touches Status.
    /// </summary>
    [Fact]
    public async Task UpdateFieldsAsync_ProfileImportRacesUserSkip_StatusSurvives_ImportedFieldsPersist()
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

        // Import path patches only its own columns (never Status).
        string importedJson = $"[\"{firstPrinter}\",\"{importedPrinter}\"]";
        const string importedDescription = "2 printers waiting for slicer profiles";
        detached.RelatedEntityIdsJson = importedJson;
        detached.Description = importedDescription;

        await using (AppDbContext importCtx = TestInfrastructure.TestHelpers.CreateContext(connection))
        {
            EfUserTaskRepository importRepo = new(importCtx);
            await importRepo.UpdateFieldsAsync(
                detached,
                [nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)]);
        }

        await using AppDbContext verifyCtx = TestInfrastructure.TestHelpers.CreateContext(connection);
        UserTask finalTask = await verifyCtx.UserTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);

        finalTask.Status.Should().Be(UserTaskStatus.Skipped);          // user action wins
        finalTask.RelatedEntityIdsJson.Should().Be(importedJson);      // import fields persisted
        finalTask.Description.Should().Be(importedDescription);
    }

    /// <summary>
    /// Fix R4-4: on process restart the bootstrap suppression lookback (widened from 24h
    /// to 7d) must rediscover a still-active episode a user Skipped several days ago, so
    /// the compiler does not resurrect a task the user dismissed 25h+ ago. A Skip 3 days
    /// old is inside the window; one 30 days old is beyond it, proving the bound still
    /// exists.
    /// </summary>
    [Fact]
    public async Task CompileAsync_Bootstrap_SuppressionLookbackIncludesSkipsWithinSevenDays()
    {
        using SqliteConnection connection = TestInfrastructure.TestHelpers.CreateOpenSqliteConnection();
        DateTime now = DateTime.UtcNow;

        await using (AppDbContext seed = TestInfrastructure.TestHelpers.CreateContext(connection, ensureCreated: true))
        {
            UserTask recent = NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:recent", UserTaskStatus.Skipped);
            recent.UpdatedAt = now.AddDays(-3);
            UserTask old = NewOpenTask(UserTaskSourceKind.Maintenance, "maintenancealert:old", UserTaskStatus.Skipped);
            old.UpdatedAt = now.AddDays(-30);
            _ = seed.UserTasks.Add(recent);
            _ = seed.UserTasks.Add(old);
            _ = await seed.SaveChangesAsync();
        }

        await using AppDbContext ctx = TestInfrastructure.TestHelpers.CreateContext(connection);
        EfUserTaskRepository repo = new(ctx);

        // The source owns a DIFFERENT kind and produces nothing, so the compiler's
        // end-of-pass RemoveWhere (which only drops keys for successfully-evaluated
        // kinds) cannot discard the bootstrapped Maintenance keys under assertion.
        ShiftPlanCompiler compiler = new(
            new[] { new NoSpecSource([UserTaskSourceKind.FailureIncident]) },
            repo,
            NullLogger<ShiftPlanCompiler>.Instance);

        ShiftPlanSuppressionState state = new(); // fresh: LastPassAtUtc == null → bootstrap path
        _ = await compiler.CompileAsync(state);

        state.SuppressedKeys.Should().Contain((UserTaskSourceKind.Maintenance, "maintenancealert:recent"));
        state.SuppressedKeys.Should().NotContain((UserTaskSourceKind.Maintenance, "maintenancealert:old"));
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
