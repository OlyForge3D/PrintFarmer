using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Attention;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Attention;

/// <summary>
/// Relational (SQLite) regression tests for <see cref="EfAttentionSnoozeRepository.UpsertAsync"/>
/// concurrency safety (issue #707, review item D). These use a real relational provider so the
/// <c>IX_AttentionSnoozes_UserId_AttentionItemId</c> unique index is actually enforced — the
/// EF in-memory provider does NOT enforce unique indexes, so a mocked repository cannot prove
/// this behaviour. A <see cref="DbContext.SavingChanges"/> hook injects a genuine concurrent
/// insert to force the unique violation on the primary insert.
/// </summary>
public class EfAttentionSnoozeRepositoryRaceTests
{
    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    [Fact]
    public async Task UpsertAsync_UniqueInsertRace_RecoversToSingleRowWithLatestValue()
    {
        var userId = Guid.NewGuid();
        const string itemId = "failure:11111111-1111-1111-1111-111111111111";
        DateTime concurrentUntil = new(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        DateTime primaryUntil = new(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc);

        await using SqliteConnection connection =
            new("Data Source=file:attention-snooze-unique-race?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
        }

        await using AppDbContext db = new(options);
        var repository = new EfAttentionSnoozeRepository(db);

        bool injected = false;
        db.SavingChanges += (_, _) =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            // A concurrent request wins the (userId, itemId) unique index first.
            using AppDbContext concurrentDb = new(options);
            _ = concurrentDb.AttentionSnoozes.Add(new AttentionSnooze
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AttentionItemId = itemId,
                SnoozedUntilUtc = concurrentUntil,
                CreatedAtUtc = DateTime.UtcNow,
            });
            _ = concurrentDb.SaveChanges();
        };

        AttentionSnooze result = await repository.UpsertAsync(
            userId, itemId, primaryUntil, DateTime.UtcNow, attentionItemAnchorAtUtc: null, CancellationToken.None);

        // The retry recovered by updating the winning row to the caller's requested value.
        result.SnoozedUntilUtc.Should().Be(primaryUntil);

        await using AppDbContext assertDb = new(options);
        int count = await assertDb.AttentionSnoozes.CountAsync(s => s.UserId == userId && s.AttentionItemId == itemId);
        count.Should().Be(1, "the unique-violation retry must collapse the race to exactly one snooze row");
        AttentionSnooze surviving = await assertDb.AttentionSnoozes.SingleAsync(
            s => s.UserId == userId && s.AttentionItemId == itemId);
        surviving.SnoozedUntilUtc.Should().Be(primaryUntil);
    }

    [Fact]
    public async Task UpsertAsync_NonUniqueDbUpdateException_Propagates()
    {
        var userId = Guid.NewGuid();
        const string itemId = "failure:22222222-2222-2222-2222-222222222222";

        await using SqliteConnection connection =
            new("Data Source=file:attention-snooze-nonunique-propagate?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
        }

        await using AppDbContext db = new(options);
        var repository = new EfAttentionSnoozeRepository(db);

        bool corrupted = false;
        db.SavingChanges += (_, _) =>
        {
            if (corrupted)
            {
                return;
            }

            corrupted = true;
            // Null out a required (NOT NULL) column on the tracked insert to force a
            // non-unique DbUpdateException that MUST propagate rather than being retried.
            foreach (var entry in db.ChangeTracker.Entries<AttentionSnooze>())
            {
                entry.Entity.AttentionItemId = null!;
            }
        };

        Func<Task> act = async () => await repository.UpsertAsync(
            userId, itemId, DateTime.UtcNow.AddHours(1), DateTime.UtcNow, attentionItemAnchorAtUtc: null, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task RemoveAsync_ConcurrentDeleteRace_IsIdempotentlySuccessful()
    {
        var userId = Guid.NewGuid();
        const string itemId = "failure:33333333-3333-3333-3333-333333333333";

        await using SqliteConnection connection =
            new("Data Source=file:attention-snooze-delete-race?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            _ = seedDb.AttentionSnoozes.Add(new AttentionSnooze
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AttentionItemId = itemId,
                SnoozedUntilUtc = new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc),
                CreatedAtUtc = DateTime.UtcNow,
            });
            _ = await seedDb.SaveChangesAsync();
        }

        await using AppDbContext db = new(options);
        var repository = new EfAttentionSnoozeRepository(db);

        bool deleted = false;
        db.SavingChanges += (_, _) =>
        {
            if (deleted)
            {
                return;
            }

            deleted = true;
            // A concurrent request deletes the same row between our read and our save,
            // so the tracked DELETE affects zero rows and EF raises a concurrency
            // exception. The desired end state (no snooze) is nonetheless achieved.
            using AppDbContext concurrentDb = new(options);
            AttentionSnooze? row = concurrentDb.AttentionSnoozes
                .Single(s => s.UserId == userId && s.AttentionItemId == itemId);
            _ = concurrentDb.AttentionSnoozes.Remove(row);
            _ = concurrentDb.SaveChanges();
        };

        bool result = await repository.RemoveAsync(userId, itemId, CancellationToken.None);

        result.Should().BeTrue("a concurrent delete leaves the desired end state, so RemoveAsync is idempotently successful");

        await using AppDbContext assertDb = new(options);
        int count = await assertDb.AttentionSnoozes.CountAsync(s => s.UserId == userId && s.AttentionItemId == itemId);
        count.Should().Be(0, "the row must be gone regardless of which request won the race");
    }
}
