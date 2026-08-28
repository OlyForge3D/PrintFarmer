using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Idempotency;

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

    private IdempotencyStore CreateSut(IdempotencyOptions options) => new(_factory, NullLogger<IdempotencyStore>.Instance, options);

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
        TimeSpan window = IIdempotencyStore.RetentionWindow;

        using (AppDbContext seed = new(_options))
        {
            seed.IdempotencyRecords.AddRange(
                new IdempotencyRecord
                {
                    // One tick BEYOND the window → strictly older than cutoff → pruned.
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "beyond",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - window - TimeSpan.FromTicks(1),
                    UpdatedAt = now - window - TimeSpan.FromTicks(1),
                },
                new IdempotencyRecord
                {
                    // EXACTLY at the cutoff. The boundary is exclusive
                    // (CreatedAt < cutoff), so this row is RETAINED.
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "atCutoff",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - window,
                    UpdatedAt = now - window,
                },
                new IdempotencyRecord
                {
                    // One tick INSIDE the window → retained.
                    Id = Guid.NewGuid(),
                    UserId = "u",
                    RouteKey = "r",
                    IdempotencyKey = "inside",
                    RequestHash = "h",
                    Status = IdempotencyRecordStatus.Completed,
                    CreatedAt = now - window + TimeSpan.FromTicks(1),
                    UpdatedAt = now - window + TimeSpan.FromTicks(1),
                });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        int removed = await sut.PruneExpiredAsync(now, CancellationToken.None);
        _ = removed.Should().Be(1, "only the row strictly older than the cutoff is expired");

        using AppDbContext verify = new(_options);
        List<string> remaining = await verify.IdempotencyRecords
            .Select(r => r.IdempotencyKey)
            .OrderBy(k => k)
            .ToListAsync(CancellationToken.None);
        _ = remaining.Should().BeEquivalentTo(new[] { "atCutoff", "inside" },
            "the exact-cutoff row is retained under the exclusive boundary and the inside row is well within the window");
    }

    [Fact]
    public void IsExpired_BoundaryIsExclusive_AtExactTicks()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan window = IIdempotencyStore.RetentionWindow;

        // Exactly at the cutoff → NOT expired (exclusive boundary): read, begin, and
        // prune must all agree that a row whose age equals the window is still valid.
        _ = IdempotencyStore.IsExpired(now - window, now)
            .Should().BeFalse("a record exactly at the retention cutoff is retained (exclusive boundary)");

        // One tick beyond the window → expired.
        _ = IdempotencyStore.IsExpired(now - window - TimeSpan.FromTicks(1), now)
            .Should().BeTrue("a record one tick past the cutoff is expired");

        // One tick inside the window → retained.
        _ = IdempotencyStore.IsExpired(now - window + TimeSpan.FromTicks(1), now)
            .Should().BeFalse("a record one tick inside the cutoff is retained");
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
        // Determinism: the previous version raced Task.Run over a single shared
        // SQLite connection, which serialized ADO commands and made the race
        // artificial (~25% flaky per Bishop). Here each racer gets its OWN
        // connection to a shared-cache in-memory database and all racers are
        // released simultaneously by a Barrier, so they genuinely contend on the
        // composite unique index at the database.
        const int racerCount = 8;
        string dbName = $"idemp-race-{Guid.NewGuid():N}";
        string connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        // Keep-alive connection: a shared-cache in-memory database is destroyed when
        // its LAST connection closes, so we hold one open for the whole test.
        await using SqliteConnection keepAlive = new(connectionString);
        await keepAlive.OpenAsync();

        await using (AppDbContext create = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(keepAlive).Options))
        {
            _ = await create.Database.EnsureCreatedAsync();
        }

        SharedCacheContextFactory factory = new(connectionString);
        IdempotencyStore sut = new(factory, NullLogger<IdempotencyStore>.Instance);

        using Barrier gate = new(racerCount);
        async Task<IdempotencyLookupResult> RaceAsync()
        {
            // Hop onto a pool thread first, then rendezvous so every racer calls
            // TryBeginAsync at the same instant.
            await Task.Yield();
            gate.SignalAndWait();
            return await sut.TryBeginAsync(
                "user-A", IdempotencyRouteKeys.TaskComplete, "race-key", "hash-A", CancellationToken.None);
        }

        // IDISP013 false positive: every task is awaited via Task.WhenAll on the next line,
        // before `gate` leaves its using scope.
#pragma warning disable IDISP013 // Await in using
        Task<IdempotencyLookupResult>[] racers = Enumerable.Range(0, racerCount)
            .Select(_ => Task.Run(RaceAsync))
            .ToArray();
#pragma warning restore IDISP013

        IdempotencyLookupResult[] results = await Task.WhenAll(racers);

        int inserted = results.Count(r => r.Outcome == IdempotencyLookupOutcome.Inserted);
        _ = inserted.Should().Be(1, "the composite unique index must serialize first-request winners");

        _ = results.Where(r => r.Outcome != IdempotencyLookupOutcome.Inserted)
            .Should().OnlyContain(
                r => r.Outcome == IdempotencyLookupOutcome.InProgress
                    || r.Outcome == IdempotencyLookupOutcome.ReplayCompleted,
                "losers must observe the winner's row as in-progress or completed — never Bypassed and never raising");
    }

    [Fact]
    public async Task TryBegin_StaleProcessingRow_IsReclaimed()
    {
        // A Processing row whose owning request died is reclaimed once it is older
        // than ProcessingStaleness, so a crashed request cannot wedge the key until
        // it ages out of the 7-day retention window.
        IdempotencyOptions options = new() { ProcessingStaleness = TimeSpan.FromMinutes(5) };
        IdempotencyStore sut = CreateSut(options);

        Guid staleId = Guid.NewGuid();
        using (AppDbContext seed = new(_options))
        {
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = staleId,
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "stuck-key",
                RequestHash = "hash-A",
                Status = IdempotencyRecordStatus.Processing,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
            });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "stuck-key", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted,
            "a Processing row older than ProcessingStaleness is abandoned and reclaimed");
        _ = result.Record!.Id.Should().NotBe(staleId, "the stale row is deleted and a brand-new row inserted");

        using AppDbContext verify = new(_options);
        int rows = await verify.IdempotencyRecords
            .CountAsync(r => r.UserId == "user-A" && r.IdempotencyKey == "stuck-key", CancellationToken.None);
        _ = rows.Should().Be(1, "the stale row must be replaced, not duplicated");
        _ = (await verify.IdempotencyRecords.AnyAsync(r => r.Id == staleId, CancellationToken.None))
            .Should().BeFalse("the stale row must have been deleted");
    }

    [Fact]
    public async Task TryBegin_FreshProcessingRow_StillInProgress()
    {
        // A Processing row younger than ProcessingStaleness is a genuine in-flight
        // request and must be reported as InProgress, not reclaimed.
        IdempotencyOptions options = new() { ProcessingStaleness = TimeSpan.FromMinutes(5) };
        IdempotencyStore sut = CreateSut(options);

        using (AppDbContext seed = new(_options))
        {
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "live-key",
                RequestHash = "hash-A",
                Status = IdempotencyRecordStatus.Processing,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(1),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(1),
            });
            _ = await seed.SaveChangesAsync(CancellationToken.None);
        }

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "live-key", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.InProgress,
            "a fresh Processing row represents a genuine in-flight request");
    }

    [Theory]
    // SQLite primary code SQLITE_CONSTRAINT and its PK/UNIQUE extended codes.
    [InlineData(null, null, 19, 19, true)]
    [InlineData(null, null, 19, 1555, true)]
    [InlineData(null, null, 19, 2067, true)]
    // PostgreSQL unique_violation SQLSTATE.
    [InlineData("23505", null, null, null, true)]
    // SQL Server duplicate-key / unique-constraint engine error numbers.
    [InlineData(null, 2601, null, null, true)]
    [InlineData(null, 2627, null, null, true)]
    // Non-unique failures for each provider must NOT match.
    [InlineData("23503", null, null, null, false)] // PG foreign_key_violation
    [InlineData(null, 547, null, null, false)]      // SQL Server FK/CHECK violation
    [InlineData(null, null, 20, 20, false)]         // SQLITE_MISMATCH
    [InlineData(null, null, null, null, false)]     // no signal at all
    public void MatchesUniqueViolation_ClassifiesEachProviderSignature(
        string? sqlState,
        int? sqlServerErrorNumber,
        int? sqliteErrorCode,
        int? sqliteExtendedErrorCode,
        bool expected)
    {
        _ = IdempotencyStore.MatchesUniqueViolation(
                sqlState, sqlServerErrorNumber, sqliteErrorCode, sqliteExtendedErrorCode)
            .Should().Be(expected);
    }

    [Fact]
    public async Task IsUniqueViolation_FiresForRealSqliteDuplicateInsert()
    {
        // Drive a genuine unique-index violation through EF's DbUpdateException so
        // the typed SQLite detection (SqliteException code 19 / extended 2067) is
        // exercised end-to-end — not just the pure classifier.
        IdempotencyStore sut = CreateSut();
        IdempotencyLookupResult first = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "dup-key", "hash-A", CancellationToken.None);
        _ = first.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted);

        DbUpdateException captured = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await using AppDbContext db = new(_options);
            _ = db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "dup-key", // same triple → violates the composite unique index
                RequestHash = "hash-B",
                Status = IdempotencyRecordStatus.Processing,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync(CancellationToken.None);
        });

        _ = IdempotencyStore.IsUniqueViolation(captured)
            .Should().BeTrue("a real SQLite composite-unique-index violation must be recognised by typed detection");
    }

    [Fact]
    public void IsUniqueViolation_UnwrapsInnerPostgresException_ViaSqlState()
    {
        // Npgsql surfaces the SQLSTATE on the base DbException, so the guard can
        // recognise a Postgres unique_violation without an Npgsql type reference.
        DbUpdateException uniqueViolation = new(
            "update failed", new FakeSqlStateException("23505"));
        _ = IdempotencyStore.IsUniqueViolation(uniqueViolation).Should().BeTrue();

        // A different SQLSTATE (foreign_key_violation) must NOT be mistaken for it.
        DbUpdateException fkViolation = new(
            "update failed", new FakeSqlStateException("23503"));
        _ = IdempotencyStore.IsUniqueViolation(fkViolation).Should().BeFalse();
    }

    /// <summary>
    /// Minimal <see cref="DbException"/> stand-in that surfaces a chosen SQLSTATE on
    /// the base <see cref="DbException.SqlState"/> property, mirroring how Npgsql
    /// exposes PostgreSQL error codes without requiring an Npgsql dependency in the
    /// test assembly.
    /// </summary>
    private sealed class FakeSqlStateException : DbException
    {
        public FakeSqlStateException(string sqlState)
            : base("simulated provider failure")
        {
            SqlState = sqlState;
        }

        public FakeSqlStateException()
            : this(string.Empty)
        {
        }

        public FakeSqlStateException(string message, Exception innerException)
            : base(message, innerException)
        {
            SqlState = string.Empty;
        }

        public override string SqlState { get; }
    }

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> that hands each caller its own
    /// connection to a shared-cache in-memory SQLite database with a generous
    /// <c>busy_timeout</c>, so concurrent racers genuinely contend at the database
    /// instead of being serialized onto one shared connection. The returned context
    /// owns and disposes its connection.
    /// </summary>
    private sealed class SharedCacheContextFactory(string connectionString) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            SqliteConnection connection = new(connectionString);
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=10000;";
                _ = pragma.ExecuteNonQuery();
            }

            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            return new OwnedConnectionAppDbContext(options, connection);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// <see cref="AppDbContext"/> that disposes the externally supplied connection
    /// it was constructed with, so the per-context connection created by
    /// <see cref="SharedCacheContextFactory"/> does not leak.
    /// </summary>
    private sealed class OwnedConnectionAppDbContext(
        DbContextOptions<AppDbContext> options,
        SqliteConnection connection) : AppDbContext(options)
    {
        public override void Dispose()
        {
            base.Dispose();
            connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Contention_LoserAfterWinnerAbandoned_RetriesInsertNotBypass()
    {
        // Hicks H-2: a caller that loses the insert race and then reloads to find the
        // winning row already gone (a concurrent caller abandoned its Processing row in
        // between) must NOT return Bypassed — that would execute the mutation with no
        // replay protection. It must retry the insert and win protection itself.
        (string connectionString, SqliteConnection keepAlive) = await CreateSharedDbAsync();
        await using SqliteConnection keepAliveConn = keepAlive;

        StrongBox<int> insertFailures = new(1); // fail only the first insert
        ConflictInjectingContextFactory factory = new(connectionString, insertFailures, onConflict: null);
        IdempotencyStore sut = new(factory, NullLogger<IdempotencyStore>.Instance);

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "vanish-key", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted,
            "a loser whose winner vanished must retry the insert and win protection, never Bypass");
        _ = result.Outcome.Should().NotBe(IdempotencyLookupOutcome.Bypassed);

        await using SqliteConnection verifyConn = new(connectionString);
        await verifyConn.OpenAsync();
        await using AppDbContext verify = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options);
        _ = (await verify.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "the retry must persist exactly one protected row");
    }

    [Fact]
    public async Task Contention_WinnerKeepsVanishing_ExhaustsRetriesAndReturnsInProgress()
    {
        // If every attempt loses the race to a winner that then vanishes, the bounded
        // retry loop must give up with InProgress (409) so the client backs off — never
        // Bypassed, and never an unbounded livelock.
        (string connectionString, SqliteConnection keepAlive) = await CreateSharedDbAsync();
        await using SqliteConnection keepAliveConn = keepAlive;

        // More failures than the loop can ever attempt, so every insert loses.
        StrongBox<int> insertFailures = new(16);
        ConflictInjectingContextFactory factory = new(connectionString, insertFailures, onConflict: null);
        IdempotencyStore sut = new(factory, NullLogger<IdempotencyStore>.Instance);

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "always-vanish-key", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.InProgress,
            "exhausting the bounded retries must surface InProgress so the client backs off");
        _ = result.Outcome.Should().NotBe(IdempotencyLookupOutcome.Bypassed);

        await using SqliteConnection verifyConn = new(connectionString);
        await verifyConn.OpenAsync();
        await using AppDbContext verify = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options);
        _ = (await verify.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(0, "a caller that never wins the race must not persist a row");
    }

    [Fact]
    public async Task TryBegin_WinnerReloadStaleProcessingRow_IsReclaimedAndRetried()
    {
        // Bishop NB3: the winner-reload path must apply the SAME staleness reclaim as the
        // initial read. If the reloaded winner is itself a stale Processing row, delete it
        // and retry the insert rather than reporting a dead request as InProgress.
        (string connectionString, SqliteConnection keepAlive) = await CreateSharedDbAsync();
        await using SqliteConnection keepAliveConn = keepAlive;

        Guid staleId = Guid.NewGuid();
        IdempotencyOptions options = new() { ProcessingStaleness = TimeSpan.FromMinutes(5) };

        // On the first (forced) insert conflict, seed a STALE Processing winner so the
        // winner-reload branch observes a reclaimable row.
        void SeedStaleWinner()
        {
            using SqliteConnection seedConn = new(connectionString);
            seedConn.Open();
            using AppDbContext seed = new(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(seedConn).Options);
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = staleId,
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "stale-winner-key",
                RequestHash = "hash-A",
                Status = IdempotencyRecordStatus.Processing,
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
            });
            _ = seed.SaveChanges();
        }

        StrongBox<int> insertFailures = new(1);
        ConflictInjectingContextFactory factory = new(connectionString, insertFailures, SeedStaleWinner);
        IdempotencyStore sut = new(factory, NullLogger<IdempotencyStore>.Instance, options);

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "stale-winner-key", "hash-A", CancellationToken.None);

        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.Inserted,
            "the winner-reload path must reclaim a stale Processing winner and retry the insert (Bishop NB3)");
        _ = result.Record!.Id.Should().NotBe(staleId, "the stale winner is deleted and a fresh row inserted");

        await using SqliteConnection verifyConn = new(connectionString);
        await verifyConn.OpenAsync();
        await using AppDbContext verify = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options);
        _ = (await verify.IdempotencyRecords.CountAsync(CancellationToken.None))
            .Should().Be(1, "the stale winner must be replaced, not duplicated");
        _ = (await verify.IdempotencyRecords.AnyAsync(r => r.Id == staleId, CancellationToken.None))
            .Should().BeFalse("the stale winner must have been reclaimed");
    }

    [Fact]
    public async Task TryBegin_ReclaimRacesConcurrentCompletion_DoesNotEraseCompletedRecord()
    {
        // Hicks r2 blocker 3 (reclaim TOCTOU): the initial-read reclaim path reads a stale
        // Processing row (AsNoTracking snapshot), then deletes it. If a concurrent
        // CompleteAsync commits BETWEEN that read and the delete, an unconditional
        // delete-by-id would erase the just-completed record — the next replay attempt would
        // then miss it and re-execute the already-applied mutation. The conditional delete
        // (WHERE still-reclaimable) must instead match zero rows, leaving the completed
        // record intact so this caller replays it.
        //
        // The race is made deterministic with a command interceptor that completes the row
        // on the SAME connection/transaction the reclaim DELETE is about to use — modelling a
        // CompleteAsync that commits an instant before the delete executes.
        (string connectionString, SqliteConnection keepAlive) = await CreateSharedDbAsync();
        await using SqliteConnection keepAliveConn = keepAlive;

        Guid staleId = Guid.NewGuid();
        byte[] winnerBody = Encoding.UTF8.GetBytes("{\"winner\":true}");
        using (SqliteConnection seedConn = new(connectionString))
        {
            seedConn.Open();
            using AppDbContext seed = new(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(seedConn).Options);
            _ = seed.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = staleId,
                UserId = "user-A",
                RouteKey = IdempotencyRouteKeys.TaskComplete,
                IdempotencyKey = "toctou-key",
                RequestHash = "hash-A",
                Status = IdempotencyRecordStatus.Processing,
                // Older than ProcessingStaleness so the reclaim path engages.
                CreatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
                UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(6),
            });
            _ = seed.SaveChanges();
        }

        CompleteOnReclaimDeleteInterceptor interceptor = new("toctou-key", winnerBody);
        InterceptingContextFactory factory = new(connectionString, interceptor);
        IdempotencyOptions options = new() { ProcessingStaleness = TimeSpan.FromMinutes(5) };
        IdempotencyStore sut = new(factory, NullLogger<IdempotencyStore>.Instance, options);

        IdempotencyLookupResult result = await sut.TryBeginAsync(
            "user-A", IdempotencyRouteKeys.TaskComplete, "toctou-key", "hash-A", CancellationToken.None);

        _ = interceptor.DidFire.Should().BeTrue(
            "the concurrent completion must have raced the reclaim delete for the test to be meaningful");
        _ = result.Outcome.Should().Be(IdempotencyLookupOutcome.ReplayCompleted,
            "a record completed between the reclaim read and delete must be replayed, not erased and re-executed");
        _ = result.Record!.Id.Should().Be(staleId, "the surviving record is the completed winner, not a fresh insert");
        _ = result.Record!.ResponseBody.Should().Equal(winnerBody,
            "the replay must expose the concurrently-committed winner's response bytes");

        await using SqliteConnection verifyConn = new(connectionString);
        await verifyConn.OpenAsync();
        await using AppDbContext verify = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(verifyConn).Options);
        IdempotencyRecord survivor = await verify.IdempotencyRecords.SingleAsync(CancellationToken.None);
        _ = survivor.Id.Should().Be(staleId, "the completed record must survive the reclaim race (no TOCTOU erasure)");
        _ = survivor.Status.Should().Be(IdempotencyRecordStatus.Completed);
    }

    /// <summary>
    /// Creates a shared-cache in-memory SQLite database and returns its connection
    /// string plus a keep-alive connection (a shared-cache database is destroyed when
    /// its last connection closes). The caller owns and must dispose the keep-alive.
    /// </summary>
    private static async Task<(string ConnectionString, SqliteConnection KeepAlive)> CreateSharedDbAsync()
    {
        string dbName = $"idemp-fixdg-{Guid.NewGuid():N}";
        string connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        SqliteConnection keepAlive = new(connectionString);
        await keepAlive.OpenAsync();
        await using (AppDbContext create = new(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(keepAlive).Options))
        {
            _ = await create.Database.EnsureCreatedAsync();
        }

        return (connectionString, keepAlive);
    }

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> that hands out
    /// <see cref="ConflictInjectingContext"/> instances against a shared-cache in-memory
    /// database, so the store's insert can be forced to lose the unique-index race a
    /// controlled number of times (and optionally run a hook at the moment of conflict).
    /// </summary>
    private sealed class ConflictInjectingContextFactory(
        string connectionString,
        StrongBox<int> insertFailures,
        Action? onConflict) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            SqliteConnection connection = new(connectionString);
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=10000;";
                _ = pragma.ExecuteNonQuery();
            }

            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            return new ConflictInjectingContext(options, connection, insertFailures, onConflict);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// <see cref="AppDbContext"/> whose insert (<see cref="SaveChangesAsync(bool, CancellationToken)"/>)
    /// throws a synthetic unique-violation <see cref="DbUpdateException"/> for the first
    /// N calls (tracked via a shared counter), optionally invoking a hook first. Reads and
    /// <c>ExecuteDeleteAsync</c> are unaffected because they do not funnel through
    /// <c>SaveChangesAsync</c>, so only the TryBegin insert is intercepted.
    /// </summary>
    private sealed class ConflictInjectingContext(
        DbContextOptions<AppDbContext> options,
        SqliteConnection connection,
        StrongBox<int> insertFailures,
        Action? onConflict) : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref insertFailures.Value) >= 0)
            {
                onConflict?.Invoke();
                throw new DbUpdateException("simulated race loss", new FakeSqlStateException("23505"));
            }

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        public override void Dispose()
        {
            base.Dispose();
            connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// <see cref="IDbContextFactory{TContext}"/> that hands out contexts against a
    /// shared-cache in-memory database with a command interceptor attached, so a raw SQL
    /// completion can be spliced into the reclaim DELETE's own connection/transaction to
    /// reproduce the reclaim-vs-completion TOCTOU race deterministically.
    /// </summary>
    private sealed class InterceptingContextFactory(
        string connectionString,
        IInterceptor interceptor) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            SqliteConnection connection = new(connectionString);
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=10000;";
                _ = pragma.ExecuteNonQuery();
            }

            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            return new OwningConnectionContext(options, connection);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// <see cref="AppDbContext"/> that owns and disposes the shared-cache connection handed
    /// to it, mirroring <see cref="ConflictInjectingContext"/>'s lifetime management.
    /// </summary>
    private sealed class OwningConnectionContext(
        DbContextOptions<AppDbContext> options,
        SqliteConnection connection) : AppDbContext(options)
    {
        public override void Dispose()
        {
            base.Dispose();
            connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Command interceptor that, exactly once, completes the target idempotency row on the
    /// same connection and transaction the reclaim DELETE is about to run — simulating a
    /// <c>CompleteAsync</c> that commits between the reclaim's snapshot read and its delete.
    /// The row is matched by <see cref="IdempotencyRecord.IdempotencyKey"/> (TEXT) to avoid
    /// SQLite GUID-format ambiguity, and CreatedAt/UpdatedAt are left untouched so EF reads
    /// them back cleanly.
    /// </summary>
    private sealed class CompleteOnReclaimDeleteInterceptor(
        string idempotencyKey,
        byte[] responseBody) : DbCommandInterceptor
    {
        private int _fired;

        public bool DidFire => Volatile.Read(ref _fired) != 0;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsReclaimDelete(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await CompleteConcurrentlyAsync(command, cancellationToken);
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            if (IsReclaimDelete(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                CompleteConcurrently(command);
            }

            return base.NonQueryExecuting(command, eventData, result);
        }

        private static bool IsReclaimDelete(string sql)
            => sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("IdempotencyRecords", StringComparison.Ordinal);

        private DbCommand BuildCompletion(DbCommand deleteCommand)
        {
            DbCommand complete = deleteCommand.Connection!.CreateCommand();
            complete.Transaction = deleteCommand.Transaction;
            complete.CommandText =
                "UPDATE \"IdempotencyRecords\" SET \"Status\" = 'Completed', " +
                "\"ResponseStatusCode\" = 200, \"ResponseContentType\" = 'application/json', " +
                "\"ResponseBody\" = $body " +
                "WHERE \"IdempotencyKey\" = $key AND \"Status\" = 'Processing';";
            AddParam(complete, "$key", idempotencyKey);
            AddParam(complete, "$body", responseBody);
            return complete;
        }

        private void CompleteConcurrently(DbCommand deleteCommand)
        {
            using DbCommand complete = BuildCompletion(deleteCommand);
            _ = complete.ExecuteNonQuery();
        }

        private async Task CompleteConcurrentlyAsync(DbCommand deleteCommand, CancellationToken ct)
        {
            await using DbCommand complete = BuildCompletion(deleteCommand);
            _ = await complete.ExecuteNonQueryAsync(ct);
        }

        private static void AddParam(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
        }
    }
}
