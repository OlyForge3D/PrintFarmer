using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Idempotency;

/// <summary>
/// Relational tests for <see cref="IdempotencyStore"/> executed against
/// SQLite-in-memory. The SQLite provider is deliberate: it honours the composite
/// unique index we depend on for the concurrent first-request race, uses real
/// transactions, and exercises <c>ExecuteDeleteAsync</c> so prune / abandon
/// codepaths are covered end-to-end. See #715 for the contract.
/// </summary>
public class IdempotencyStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IdempotencyStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext db = new(_options);
        _ = db.Database.EnsureCreated();

        Mock<IDbContextFactory<AppDbContext>> factoryMock = new();
        _ = factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        _factory = factoryMock.Object;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private IdempotencyStore CreateSut() => new(_factory, NullLogger<IdempotencyStore>.Instance);

    [Fact]
    public async Task TryBegin_FirstRequest_Inserts()
    {
        IdempotencyStore sut = CreateSut();

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);
        _ = result.Record.Should().NotBeNull();
        _ = result.Record!.Status.Should().Be(IdempotencyRecordStatus.Processing);
    }

    [Fact]
    public async Task TryBegin_ExactReplay_ReturnsReplayCompleted_WithStoredResponse()
    {
        IdempotencyStore sut = CreateSut();

        IdempotencyLookupResult begin = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);
        await sut.CompleteAsync(begin.Record!.Id, 200, "application/json", System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"), CancellationToken.None);

        IdempotencyLookupResult replay = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        _ = replay.Outcome.Should().Be(IdempotencyLookupOutcome.ReplayCompleted);
        _ = replay.Record!.ResponseStatusCode.Should().Be(200);
        _ = System.Text.Encoding.UTF8.GetString(replay.Record.ResponseBody!).Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task TryBegin_SameKeyDifferentHash_ReturnsHashConflict()
    {
        IdempotencyStore sut = CreateSut();
        _ = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        IdempotencyLookupResult conflict = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-DIFFERENT", CancellationToken.None);

        _ = conflict.Outcome.Should().Be(IdempotencyLookupOutcome.HashConflict);
    }

    [Fact]
    public async Task TryBegin_InProgress_ReportsInProgress()
    {
        IdempotencyStore sut = CreateSut();
        _ = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        IdempotencyLookupResult inFlight = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        _ = inFlight.Outcome.Should().Be(IdempotencyLookupOutcome.InProgress);
    }

    [Fact]
    public async Task TryBegin_DifferentUsers_DoNotCollide()
    {
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult a = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);
        IdempotencyLookupResult b = await sut.TryBeginAsync(
            "user-B", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        _ = a.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);
        _ = b.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);
    }

    [Fact]
    public async Task TryBegin_DifferentRoutes_DoNotCollide()
    {
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult a = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);
        IdempotencyLookupResult b = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.PartsInventoryAdjust, "key-1", "hash-A", CancellationToken.None);

        _ = a.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);
        _ = b.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);
    }

    [Fact]
    public async Task TryBegin_ExpiredRow_IsTreatedAsAbsent_AndPurgedInline()
    {
        IdempotencyStore sut = CreateSut();

        using (AppDbContext seed = new(_options))
        {
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "old-key",
                RequestHash = "hash-STALE",
                Status = IdempotencyRecordStatus.Completed,
                ResponseStatusCode = 200,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromDays(8),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromDays(8),
            });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "old-key", "hash-NEW", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted,
            "expired rows must never mask a fresh request; the store must delete-then-insert");

        using AppDbContext verify = new(_options);
        int rowCount = await verify.IdempotencyRecords
            .CountAsync(r => r.UserId == "user-A" && r.IdempotencyKey == "old-key", CancellationToken.None);
        _ = rowCount.Should().Be(1, "the expired row must have been removed before the fresh insert");
    }

    [Fact]
    public async Task Complete_TransitionsProcessingToCompleted_AndStoresBody()
    {
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult begin = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"x\":1}");
        await sut.CompleteAsync(begin.Record!.Id, 201, "application/json", body, CancellationToken.None);

        using AppDbContext verify = new(_options);
        IdempotencyRecord? saved = await verify.IdempotencyRecords.FindAsync(new object[] { begin.Record.Id }, CancellationToken.None);
        _ = saved.Should().NotBeNull();
        _ = saved!.Status.Should().Be(IdempotencyRecordStatus.Completed);
        _ = saved.ResponseStatusCode.Should().Be(201);
        _ = saved.ResponseContentType.Should().Be("application/json");
        _ = saved.ResponseBody.Should().BeEquivalentTo(body);
    }

    [Fact]
    public async Task Complete_IsIdempotent_DoesNotOverwriteFirstWriter()
    {
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult begin = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-1", "hash-A", CancellationToken.None);

        byte[] first = System.Text.Encoding.UTF8.GetBytes("first");
        byte[] second = System.Text.Encoding.UTF8.GetBytes("second");
        await sut.CompleteAsync(begin.Record!.Id, 200, "text/plain", first, CancellationToken.None);
        await sut.CompleteAsync(begin.Record.Id, 500, "text/plain", second, CancellationToken.None);

        using AppDbContext verify = new(_options);
        IdempotencyRecord? saved = await verify.IdempotencyRecords.FindAsync(new object[] { begin.Record.Id }, CancellationToken.None);
        _ = saved!.ResponseStatusCode.Should().Be(200, "the first-writer response must be preserved");
        _ = saved.ResponseBody.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Abandon_RemovesProcessingRow_LeavesCompletedIntact()
    {
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult inFlight = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-abandon", "h", CancellationToken.None);
        IdempotencyLookupResult done = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "key-keep", "h", CancellationToken.None);
        await sut.CompleteAsync(done.Record!.Id, 200, null, Array.Empty<byte>(), CancellationToken.None);

        await sut.AbandonProcessingAsync(inFlight.Record!.Id, CancellationToken.None);
        await sut.AbandonProcessingAsync(done.Record.Id, CancellationToken.None);

        using AppDbContext verify = new(_options);
        _ = (await verify.IdempotencyRecords.FindAsync(new object[] { inFlight.Record.Id }, CancellationToken.None))
            .Should().BeNull("processing rows can be safely purged so retries succeed");
        _ = (await verify.IdempotencyRecords.FindAsync(new object[] { done.Record.Id }, CancellationToken.None))
            .Should().NotBeNull("completed rows must never be pruned by Abandon");
    }

    [Fact]
    public async Task PruneExpired_DeletesOnlyBeyondWindow()
    {
        IdempotencyStore sut = CreateSut();
        DateTime now = DateTime.UtcNow;

        using (AppDbContext seed = new(_options))
        {
            seed.IdempotencyRecords.AddRange(
                new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "old",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - TimeSpan.FromDays(8),
                    UpdatedAt = now - TimeSpan.FromDays(8),
                },
                new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "onEdge",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1),
                    UpdatedAt = now,
                },
                new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "fresh",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - TimeSpan.FromDays(1),
                    UpdatedAt = now - TimeSpan.FromDays(1),
                });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        int removed = await sut.PruneExpiredAsync(now, CancellationToken.None);
        _ = removed.Should().Be(1);

        using AppDbContext verify = new(_options);
        int remaining = await verify.IdempotencyRecords.CountAsync(CancellationToken.None);
        _ = remaining.Should().Be(2);
    }

    [Fact]
    public async Task PruneExpired_IsSafeUnderConcurrentInvocation()
    {
        IdempotencyStore sut = CreateSut();
        DateTime now = DateTime.UtcNow;
        using (AppDbContext seed = new(_options))
        {
            for (int i = 0; i < 25; i++)
            {
                _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = $"u{i}",
                    RouteKey = "r",
                    IdempotencyKey = $"k{i}",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - TimeSpan.FromDays(10),
                    UpdatedAt = now - TimeSpan.FromDays(10),
                });
            }

            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        Task<int> a = sut.PruneExpiredAsync(now, CancellationToken.None);
        Task<int> b = sut.PruneExpiredAsync(now, CancellationToken.None);
        int[] results = await Task.WhenAll(a, b);
        _ = (results[0] + results[1]).Should().Be(25);

        using AppDbContext verify = new(_options);
        _ = (await verify.IdempotencyRecords.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TryBegin_ConcurrentRacers_OnlyOneInsertsRestObserveInProgress()
    {
        IdempotencyStore sut = CreateSut();

        // Fire five parallel first-request attempts against the same key. Exactly
        // one must observe Inserted; the rest must fall to a defined non-Insert outcome.
        Task<IdempotencyLookupResult>[] racers = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => sut.TryBeginAsync(
                "user-A", IdempotencyRouteKeys.TaskComplete, "race-key", "hash-A", CancellationToken.None)))
            .ToArray();

        IdempotencyLookupResult[] results = await Task.WhenAll(racers);
        int inserted = results.Count(r => r.Outcome == IdempotencyLookupOutcome.Inserted);
        _ = inserted.Should().Be(1, "the composite unique index must serialize first-request winners");

        _ = results.Where(r => r.Outcome != IdempotencyLookupOutcome.Inserted)
            .All(r => r.Outcome is IdempotencyLookupOutcome.InProgress
                or IdempotencyLookupOutcome.ReplayCompleted
                or IdempotencyLookupOutcome.Bypassed)
            .Should().BeTrue("losers must fall through to a defined non-Inserted state");
    }
}
