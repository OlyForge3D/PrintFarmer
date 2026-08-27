using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Mutations;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Infrastructure;

public sealed class MutationWatermarkTests
{
    [Fact]
    public async Task ProductionRepositoryWriters_AdvanceAndStampGlobalSequence()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext db = AppDbTestHelpers.CreateContext(connection, ensureCreated: true);
        EfUserTaskRepository repository = new(db);
        UserTask first = NewTask("first");

        await repository.AddAsync(first);
        await AssertSequenceAsync(db, first.Id, expected: 1);

        first.Title = "updated";
        await repository.UpdateAsync(first);
        await AssertSequenceAsync(db, first.Id, expected: 2);

        db.ChangeTracker.Clear();
        UserTask fields = (await repository.GetByIdAsync(first.Id))!;
        fields.Status = UserTaskStatus.InProgress;
        await repository.UpdateFieldsAsync(fields, [nameof(UserTask.Status)]);
        await AssertSequenceAsync(db, first.Id, expected: 3);

        db.ChangeTracker.Clear();
        UserTask conditional = (await repository.GetByIdAsync(first.Id))!;
        conditional.RelatedEntityIdsJson = "[]";
        conditional.Description = "waiting";
        (await repository.TryUpdateFieldsIfOpenAsync(
            conditional,
            [nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)]))
            .Should().BeTrue();
        await AssertSequenceAsync(db, first.Id, expected: 4);

        (await repository.TryAutoCompleteAsync(
            first.Id,
            expectedLastMutationSequence: 4,
            originWatermark: 4,
            DateTime.UtcNow)).Should().BeTrue();
        await AssertSequenceAsync(db, first.Id, expected: 5);

        db.ChangeTracker.Clear();
        UserTask second = NewTask("second");
        await repository.TrackAddAsync(second);
        await repository.SaveChangesAsync();
        await AssertSequenceAsync(db, second.Id, expected: 6);

        second.Title = "tracked update";
        await repository.TrackUpdateAsync(second);
        await repository.SaveChangesAsync();
        await AssertSequenceAsync(db, second.Id, expected: 7);

        db.ChangeTracker.Clear();
        UserTask delete = await db.UserTasks.SingleAsync(task => task.Id == second.Id);
        await repository.DeleteAsync(delete);

        (await ReadCounterAsync(db)).Should().Be(8);
        (await db.UserTasks.AsNoTracking().AnyAsync(task => task.Id == second.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task FailedInsert_RollsBackCounterAndTaskTogether()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        await using (AppDbContext seed = AppDbTestHelpers.CreateContext(connection, ensureCreated: true))
        {
            _ = seed.UserTasks.Add(NewTask("existing", "duplicate"));
            _ = await seed.SaveChangesAsync();
        }

        await using (AppDbContext write = AppDbTestHelpers.CreateContext(connection))
        {
            EfUserTaskRepository repository = new(write);
            Func<Task> act = async () => await repository.AddAsync(NewTask("duplicate", "duplicate"));
            _ = await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using AppDbContext verify = AppDbTestHelpers.CreateContext(connection);
        (await ReadCounterAsync(verify)).Should().Be(0);
        (await verify.UserTasks.CountAsync()).Should().Be(1);
        (await verify.UserTasks.SingleAsync()).LastMutationSequence.Should().Be(0);
    }

    [Fact]
    public async Task ZeroRowConditionalMutation_DoesNotReserveSequence()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext db = AppDbTestHelpers.CreateContext(connection, ensureCreated: true);
        EfUserTaskRepository repository = new(db);

        (await repository.TryAutoCompleteAsync(
            Guid.NewGuid(),
            expectedLastMutationSequence: 1,
            originWatermark: 1,
            DateTime.UtcNow)).Should().BeFalse();

        (await ReadCounterAsync(db)).Should().Be(0);
    }

    [Fact]
    public async Task TryAutoCompleteAsync_StaleOriginCannotCompleteNewerTask()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        Guid taskId = Guid.NewGuid();
        long sourceOrigin;
        long loadedSequence;

        await using (AppDbContext seed = AppDbTestHelpers.CreateContext(connection, ensureCreated: true))
        {
            EfUserTaskRepository repository = new(seed);
            UserTask task = NewTask("observed", "failure:stale", taskId);
            await repository.AddAsync(task);
            sourceOrigin = task.LastMutationSequence;
            loadedSequence = task.LastMutationSequence;
        }

        await using (AppDbContext newer = AppDbTestHelpers.CreateContext(connection))
        {
            EfUserTaskRepository repository = new(newer);
            UserTask task = await newer.UserTasks.SingleAsync(row => row.Id == taskId);
            task.Title = "newer source refresh";
            await repository.UpdateAsync(task);
        }

        await using (AppDbContext stale = AppDbTestHelpers.CreateContext(connection))
        {
            EfUserTaskRepository repository = new(stale);
            bool completed = await repository.TryAutoCompleteAsync(
                taskId,
                loadedSequence,
                sourceOrigin,
                DateTime.UtcNow);
            completed.Should().BeFalse();
        }

        await using AppDbContext verify = AppDbTestHelpers.CreateContext(connection);
        UserTask persisted = await verify.UserTasks.AsNoTracking().SingleAsync(row => row.Id == taskId);
        persisted.Status.Should().Be(UserTaskStatus.Pending);
        persisted.LastMutationSequence.Should().Be(2);
        (await ReadCounterAsync(verify)).Should().Be(2);
    }

    [Fact]
    public async Task TryAutoCompleteAsync_AuthoritativeOriginCompletesInFenceTask()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext db = AppDbTestHelpers.CreateContext(connection, ensureCreated: true);
        EfUserTaskRepository repository = new(db);
        UserTask task = NewTask("observed", "failure:clear");
        await repository.AddAsync(task);

        bool completed = await repository.TryAutoCompleteAsync(
            task.Id,
            task.LastMutationSequence,
            task.LastMutationSequence,
            DateTime.UtcNow);

        completed.Should().BeTrue();
        db.ChangeTracker.Clear();
        UserTask persisted = await db.UserTasks.AsNoTracking().SingleAsync(row => row.Id == task.Id);
        persisted.Status.Should().Be(UserTaskStatus.Completed);
        persisted.LastMutationSequence.Should().Be(2);
        (await ReadCounterAsync(db)).Should().Be(2);
    }

    [Fact]
    public async Task TryAutoCompleteAsync_RolloutZeroSequenceFailsClosedWithoutReservation()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        Guid taskId = Guid.NewGuid();
        await using (AppDbContext seed = AppDbTestHelpers.CreateContext(connection, ensureCreated: true))
        {
            _ = seed.UserTasks.Add(NewTask("rollout", "failure:rollout", taskId));
            _ = await seed.SaveChangesAsync();
        }

        await using AppDbContext write = AppDbTestHelpers.CreateContext(connection);
        EfUserTaskRepository repository = new(write);
        bool completed = await repository.TryAutoCompleteAsync(
            taskId,
            expectedLastMutationSequence: 0,
            originWatermark: 100,
            DateTime.UtcNow);

        completed.Should().BeFalse();
        (await ReadCounterAsync(write)).Should().Be(0);
    }

    [Fact]
    public async Task CallerOwnedRollback_RevertsCounterAndTask()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        await using AppDbContext db = AppDbTestHelpers.CreateContext(connection, ensureCreated: true);
        await using var transaction = await db.Database.BeginTransactionAsync();
        EfUserTaskRepository repository = new(db);

        await repository.AddAsync(NewTask("rolled back"));
        await transaction.RollbackAsync();

        db.ChangeTracker.Clear();
        (await ReadCounterAsync(db)).Should().Be(0);
        (await db.UserTasks.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SeparateReader_CannotObserveUncommittedCounterOrTask()
    {
        string databasePath = Path.Join(
            Path.GetTempPath(),
            $"mutation-watermark-visibility-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (AppDbContext setup = new(options))
            {
                _ = await setup.Database.EnsureCreatedAsync();
                _ = await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }

            await using AppDbContext writer = new(options);
            await using var transaction = await writer.Database.BeginTransactionAsync();
            EfUserTaskRepository repository = new(writer);
            UserTask task = NewTask("uncommitted");
            await repository.AddAsync(task);

            await using (AppDbContext reader = new(options))
            {
                (await ReadCounterAsync(reader)).Should().Be(0);
                (await reader.UserTasks.AsNoTracking().AnyAsync(row => row.Id == task.Id)).Should().BeFalse();
            }

            await transaction.RollbackAsync();
        }
        finally
        {
            // Connection string already has Pooling=False, so no pool entries need
            // releasing here. Avoid the process-wide ClearAllPools(), which would
            // disrupt unrelated tests' pooled SQLite connections running concurrently
            // now that this assembly is no longer fully serialized.
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task LastMutationSequence_RejectsStaleEfWriterAndRollsBackReservation()
    {
        using SqliteConnection connection = AppDbTestHelpers.CreateOpenSqliteConnection();
        Guid taskId = Guid.NewGuid();
        await using (AppDbContext seed = AppDbTestHelpers.CreateContext(connection, ensureCreated: true))
        {
            _ = seed.UserTasks.Add(NewTask("seed", id: taskId));
            _ = await seed.SaveChangesAsync();
        }

        await using AppDbContext firstContext = AppDbTestHelpers.CreateContext(connection);
        await using AppDbContext staleContext = AppDbTestHelpers.CreateContext(connection);
        EfUserTaskRepository firstRepository = new(firstContext);
        EfUserTaskRepository staleRepository = new(staleContext);
        UserTask first = await firstContext.UserTasks.SingleAsync(task => task.Id == taskId);
        UserTask stale = await staleContext.UserTasks.SingleAsync(task => task.Id == taskId);

        first.Title = "first writer";
        await firstRepository.UpdateAsync(first);

        stale.Title = "stale writer";
        Func<Task> act = async () => await staleRepository.UpdateAsync(stale);
        _ = await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using AppDbContext verify = AppDbTestHelpers.CreateContext(connection);
        UserTask persisted = await verify.UserTasks.AsNoTracking().SingleAsync(task => task.Id == taskId);
        persisted.Title.Should().Be("first writer");
        persisted.LastMutationSequence.Should().Be(1);
        (await ReadCounterAsync(verify)).Should().Be(1);
    }

    [Theory]
    [InlineData(0L, null, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, 100L, false)]
    [InlineData(1L, null, false)]
    [InlineData(2L, 1L, false)]
    [InlineData(1L, 1L, true)]
    [InlineData(1L, 2L, true)]
    public void CanAuthorizeAbsence_ZeroAndMissingProvenanceFailClosed(
        long sequence,
        long? originWatermark,
        bool expected)
    {
        MutationWatermarkCausality.CanAuthorizeAbsence(sequence, originWatermark)
            .Should().Be(expected);
    }

    [Fact]
    public void UserTaskSerialization_DoesNotExposeMutationSequence()
    {
        UserTask task = NewTask("serialize");
        task.LastMutationSequence = 42;

        string json = JsonSerializer.Serialize(task);

        json.Should().NotContain(nameof(UserTask.LastMutationSequence));
    }

    private static async Task AssertSequenceAsync(AppDbContext db, Guid taskId, long expected)
    {
        db.ChangeTracker.Clear();
        UserTask task = await db.UserTasks.AsNoTracking().SingleAsync(row => row.Id == taskId);
        task.LastMutationSequence.Should().Be(expected);
        (await ReadCounterAsync(db)).Should().Be(expected);
    }

    private static Task<long> ReadCounterAsync(AppDbContext db)
        => db.MutationCounters
            .AsNoTracking()
            .Where(counter => counter.Id == MutationCounter.GlobalId)
            .Select(counter => counter.Value)
            .SingleAsync();

    private static UserTask NewTask(
        string title,
        string? sourceId = null,
        Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            TaskType = UserTaskType.ProfileImport,
            Status = UserTaskStatus.Pending,
            Priority = UserTaskPriority.Normal,
            SourceKind = sourceId is null
                ? UserTaskSourceKind.Unspecified
                : UserTaskSourceKind.Maintenance,
            SourceId = sourceId,
            AnchorKind = UserTaskAnchorKind.Now,
            EntityType = "Printer",
            EntityId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
