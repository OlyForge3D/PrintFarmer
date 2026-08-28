using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.ShiftPlan;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestDbHelpers = Farm.Testing.Shared.AppDbTestHelpers;

namespace Farm.Infrastructure.Tests.Infrastructure;

public sealed class SpoolRestockShiftPlanPersistenceTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await using AppDbContext db = CreateContext();
        _ = await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task CompileAsync_ConcurrentPasses_LeaveOneOpenRestockOccurrence()
    {
        ShiftPlanTaskSpec spec = Spec("spoolrestock:v1:42:central");
        await using AppDbContext firstDb = CreateContext();
        await using AppDbContext secondDb = CreateContext();
        ShiftPlanCompiler first = CreateCompiler(firstDb, [spec]);
        ShiftPlanCompiler second = CreateCompiler(secondDb, [spec]);

        _ = await Task.WhenAll(
            first.CompileAsync(),
            second.CompileAsync());

        await using AppDbContext verificationDb = CreateContext();
        List<UserTask> tasks = await verificationDb.UserTasks
            .AsNoTracking()
            .Where(task =>
                task.SourceKind == UserTaskSourceKind.SpoolReorder
                && task.SourceId == spec.SourceId
                && (task.Status == UserTaskStatus.Pending
                    || task.Status == UserTaskStatus.InProgress))
            .ToListAsync();
        _ = Assert.Single(tasks);
    }

    [Fact]
    public async Task CompileAsync_SameNumericSpoolAcrossSources_PersistsTwoOccurrences()
    {
        ShiftPlanTaskSpec central = Spec("spoolrestock:v1:42:central");
        ShiftPlanTaskSpec native = Spec("spoolrestock:v1:42:native");
        await using AppDbContext db = CreateContext();
        ShiftPlanCompiler compiler = CreateCompiler(db, [central, native]);

        ShiftPlanCompileResult result = await compiler.CompileAsync();

        Assert.Equal(2, result.Created);
        await using AppDbContext verificationDb = CreateContext();
        List<UserTask> tasks = await verificationDb.UserTasks
            .AsNoTracking()
            .Where(task => task.SourceKind == UserTaskSourceKind.SpoolReorder)
            .ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.Equal(
            2,
            tasks.Select(task => task.SourceId).Distinct(StringComparer.Ordinal).Count());
    }

    private AppDbContext CreateContext() => TestDbHelpers.CreateContext(_connection);

    private static ShiftPlanCompiler CreateCompiler(
        AppDbContext db,
        IReadOnlyList<ShiftPlanTaskSpec> specs) =>
        new(
            [new RestockSource(specs)],
            new EfUserTaskRepository(db),
            NullLogger<ShiftPlanCompiler>.Instance);

    private static ShiftPlanTaskSpec Spec(string sourceId) =>
        new(
            UserTaskType.SpoolRestock,
            UserTaskSourceKind.SpoolReorder,
            sourceId,
            "Restock spool",
            "description",
            UserTaskPriority.Normal,
            UserTaskAnchorKind.At,
            DateTime.UtcNow.AddHours(1),
            null,
            null,
            "Spool",
            Guid.NewGuid(),
            DateTime.UtcNow.AddHours(2));

    private sealed class RestockSource(IReadOnlyList<ShiftPlanTaskSpec> specs)
        : IShiftPlanTaskSource
    {
        public string SourceName => "spool-restock-persistence-test";

        public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
            [UserTaskSourceKind.SpoolReorder];

        public Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct) =>
            Task.FromResult(new ShiftPlanSourceResult(specs, OriginWatermark: 0)
            {
                Authority = new ShiftPlanSourceAuthority(
                [
                    new ShiftPlanKindAuthority(
                        UserTaskSourceKind.SpoolReorder,
                        IsAuthoritativeComplete: true,
                        PreservedSourceIds: new HashSet<string>(StringComparer.Ordinal),
                        IncompleteReasons: []),
                ]),
            });
    }
}
