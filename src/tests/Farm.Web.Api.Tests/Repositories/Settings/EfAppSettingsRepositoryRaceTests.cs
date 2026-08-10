using System;
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
