using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Repositories.Settings;

/// <summary>
/// Relational (SQLite) regression tests for <see cref="EfAppSettingsRepository.TryInsertIfAbsentAsync"/>
/// concurrency safety (issue #1407, 3-way review finding). These use a real relational provider
/// so the unique index on <see cref="AppSettingsEntity.Key"/> is actually enforced — the EF
/// in-memory provider does NOT enforce unique indexes, so a test against it cannot prove this
/// behaviour (see <c>EfAttentionSnoozeRepositoryRaceTests</c> for the established pattern this
/// follows). A <see cref="DbContext.SavingChanges"/> hook injects a genuine concurrent insert to
/// force the unique violation on the primary insert.
/// </summary>
public class EfAppSettingsRepositoryRaceTests
{
    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    [Fact]
    public async Task TryInsertIfAbsentAsync_ConcurrentInsertRace_LoserReturnsFalseAndWinnerRowSurvivesUnchanged()
    {
        const string key = "race-test:server-identity";
        const string winnerValue = "{\"serverId\":\"11111111-1111-1111-1111-111111111111\"}";
        const string loserValue = "{\"serverId\":\"22222222-2222-2222-2222-222222222222\"}";

        await using SqliteConnection connection =
            new("Data Source=file:app-settings-unique-race?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
        }

        await using AppDbContext db = new(options);
        var repository = new EfAppSettingsRepository(db);

        bool injected = false;
        db.SavingChanges += (_, _) =>
        {
            if (injected)
            {
                return;
            }

            injected = true;

            // A concurrent caller wins the unique-key race first, committing its own row for
            // the same key before our primary insert reaches the database.
            using AppDbContext concurrentDb = new(options);
            _ = concurrentDb.AppSettingsEntities.Add(new AppSettingsEntity
            {
                Key = key,
                SettingsJson = winnerValue,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = concurrentDb.SaveChanges();
        };

        bool inserted = await repository.TryInsertIfAbsentAsync(key, loserValue, CancellationToken.None);

        inserted.Should().BeFalse("a concurrent caller already committed a row for this key");

        await using AppDbContext assertDb = new(options);
        int count = await assertDb.AppSettingsEntities.CountAsync(s => s.Key == key);
        count.Should().Be(1, "the unique-violation loser must not create a second row");

        AppSettingsEntity surviving = await assertDb.AppSettingsEntities.SingleAsync(s => s.Key == key);
        surviving.SettingsJson.Should().Be(winnerValue, "the winner's already-committed value must never be silently overwritten by the loser");
    }

    [Fact]
    public async Task TryInsertIfAbsentAsync_NonUniqueKeyConflict_PropagatesExceptionRatherThanReportingRaceLoss()
    {
        // A DbUpdateException that is NOT caused by a duplicate Key value (e.g. a primary-key
        // collision from some other cause) must not be misreported as "lost the insert race" -
        // TryInsertIfAbsentAsync must independently confirm a row for the target key actually
        // exists before treating a DbUpdateException as benign, and rethrow otherwise.
        const string seedKey = "race-test:pk-conflict-seed";
        const string targetKey = "race-test:pk-conflict-target";
        const string targetValue = "{\"serverId\":\"44444444-4444-4444-4444-444444444444\"}";

        await using SqliteConnection connection =
            new("Data Source=file:app-settings-pk-conflict?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        int seededId;
        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var seed = new AppSettingsEntity
            {
                Key = seedKey,
                SettingsJson = "seed",
                UpdatedAt = DateTime.UtcNow,
            };
            _ = seedDb.AppSettingsEntities.Add(seed);
            _ = await seedDb.SaveChangesAsync();
            seededId = seed.Id;
        }

        await using AppDbContext db = new(options);
        var repository = new EfAppSettingsRepository(db);

        db.SavingChanges += (sender, _) =>
        {
            var context = (AppDbContext)sender!;
            Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<AppSettingsEntity>? pending = context.ChangeTracker
                .Entries<AppSettingsEntity>()
                .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.Key == targetKey);

            // Force a primary-key collision unrelated to the unique index on Key, simulating a
            // DbUpdateException whose root cause is NOT "someone else already inserted this key".
            if (pending is not null)
            {
                pending.Entity.Id = seededId;
            }
        };

        Func<Task> act = () => repository.TryInsertIfAbsentAsync(targetKey, targetValue, CancellationToken.None);

        _ = await act.Should().ThrowAsync<DbUpdateException>(
            "a non-duplicate-key DbUpdateException must propagate rather than being swallowed as a benign race loss");

        await using AppDbContext assertDb = new(options);
        bool targetRowExists = await assertDb.AppSettingsEntities.AnyAsync(s => s.Key == targetKey);
        targetRowExists.Should().BeFalse("the failed insert must not have created a row for the target key");
    }

    [Fact]
    public async Task TryInsertIfAbsentAsync_NoConflict_InsertsAndReturnsTrue()
    {
        const string key = "race-test:no-conflict";
        const string value = "{\"serverId\":\"33333333-3333-3333-3333-333333333333\"}";

        await using SqliteConnection connection =
            new("Data Source=file:app-settings-no-conflict?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
        }

        await using AppDbContext db = new(options);
        var repository = new EfAppSettingsRepository(db);

        bool inserted = await repository.TryInsertIfAbsentAsync(key, value, CancellationToken.None);

        inserted.Should().BeTrue();

        await using AppDbContext assertDb = new(options);
        AppSettingsEntity surviving = await assertDb.AppSettingsEntities.SingleAsync(s => s.Key == key);
        surviving.SettingsJson.Should().Be(value);
    }
}
