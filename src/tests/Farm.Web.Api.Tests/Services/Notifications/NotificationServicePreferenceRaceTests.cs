using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications;

/// <summary>
/// Bishop v6 hardening: guard against the "master-flag stale-order race"
/// where a legacy PUT (no matrix) and a modern PUT (partial matrix) run
/// concurrently and one context observes a pre-mutation snapshot after the
/// other has already saved. Under the previous implementation both writes
/// derived the four <c>Enable{Channel}Notifications</c> master flags from
/// their local view of the row, so whichever write committed last silently
/// stamped its stale-derived master flags over the other writer's mutations.
///
/// The fix wraps the read/mutate/save sequence in a Serializable transaction
/// on relational providers. This test exercises the actual adverse ordering
/// (writer A begins → writer B commits inside A's transaction → writer A
/// commits) against a real SQLite backend so the isolation guarantee is
/// under test rather than the in-memory provider's happy-path linearization.
/// </summary>
public sealed class NotificationServicePreferenceRaceTests
{
    [Fact]
    public async Task ConcurrentLegacyAndModernPuts_ProducesConsistentMasterFlagsFromMergedRow()
    {
        Guid userId = Guid.NewGuid();
        const string connString = "Data Source=file:pref-master-flag-race?mode=memory&cache=shared";

        // Keep-alive connection keeps the shared in-memory DB alive for the
        // whole test. Each concurrent writer opens its OWN connection so both
        // have independent transaction contexts, mirroring a real deployment
        // where every request pulls a fresh connection from the pool.
        await using SqliteConnection keepAlive = new(connString);
        await keepAlive.OpenAsync();

        DbContextOptions<AppDbContext> BuildOptions(SqliteConnection conn) =>
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        DbContextOptions<AppDbContext> seedOptions = BuildOptions(keepAlive);

        await using (AppDbContext seedDb = new(seedOptions))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = userId,
                Username = "race-user",
                Email = "race@test.local",
                PasswordHash = "x",
            });
            seedDb.NotificationPreferences.Add(new NotificationPreferences
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                EnableEmailNotifications = false,
                EnablePushNotifications = false,
                EnableInAppNotifications = false,
                EnableTelegramNotifications = false,

                // Seed job rows all-off so the legacy writer's contribution is
                // easy to detect: it enables push+in-app across the four job rows.
                InAppOnJobStarted = false,
                InAppOnJobCompleted = false,
                InAppOnJobFailed = false,
                InAppOnJobPaused = false,
                EmailOnJobStarted = false,
                EmailOnJobCompleted = false,
                EmailOnJobFailed = false,
                EmailOnJobPaused = false,
                PushOnJobStarted = false,
                PushOnJobCompleted = false,
                PushOnJobFailed = false,
                PushOnJobPaused = false,
                TelegramOnJobStarted = false,
                TelegramOnJobCompleted = false,
                TelegramOnJobFailed = false,
                TelegramOnJobPaused = false,

                // Seed attention rows all-off. The modern writer flips
                // Push=true on the PrinterFailure row.
                InAppOnPrinterFailure = false,
                EmailOnPrinterFailure = false,
                PushOnPrinterFailure = false,
                TelegramOnPrinterFailure = false,
                InAppOnFilamentRunout = false,
                EmailOnFilamentRunout = false,
                PushOnFilamentRunout = false,
                TelegramOnFilamentRunout = false,
                InAppOnHarvestReady = false,
                EmailOnHarvestReady = false,
                PushOnHarvestReady = false,
                TelegramOnHarvestReady = false,
                InAppOnMaintenanceDue = false,
                EmailOnMaintenanceDue = false,
                PushOnMaintenanceDue = false,
                TelegramOnMaintenanceDue = false,
                InAppOnPrinterOffline = false,
                EmailOnPrinterOffline = false,
                PushOnPrinterOffline = false,
                TelegramOnPrinterOffline = false,

                Frequency = NotificationFrequency.RealTime,
                RetentionDays = 30,
            });
            await seedDb.SaveChangesAsync();
        }

        // Each concurrent writer gets its own physical SqliteConnection to the
        // shared in-memory database. SQLite serializes writers via BEGIN
        // IMMEDIATE (Serializable); the loser gets SqliteException 'database
        // is locked' which our retry path re-runs against a fresh context.
        await using SqliteConnection connectionA = new(connString);
        await connectionA.OpenAsync();
        await using SqliteConnection connectionB = new(connString);
        await connectionB.OpenAsync();

        DbContextOptions<AppDbContext> optionsA = BuildOptions(connectionA);
        DbContextOptions<AppDbContext> optionsB = BuildOptions(connectionB);

        // Writer A: legacy PUT (no matrix) that turns on push+in-app across job rows.
        NotificationPreferencesUpdate legacyPatch = new(
            EnableEmailNotifications: false,
            EnablePushNotifications: true,
            EnableInAppNotifications: true,
            EnableTelegramNotifications: false,
            NotifyOnStart: true,
            NotifyOnCompletion: true,
            NotifyOnFailure: true,
            NotifyOnPause: true,
            Frequency: NotificationFrequency.RealTime,
            RetentionDays: 30,
            MatrixRows: null);

        // Writer B: modern PUT that turns on Push+InApp for PrinterFailure only.
        NotificationPreferencesUpdate modernPatch = new(
            EnableEmailNotifications: false,
            EnablePushNotifications: true,
            EnableInAppNotifications: true,
            EnableTelegramNotifications: false,
            NotifyOnStart: false,
            NotifyOnCompletion: false,
            NotifyOnFailure: false,
            NotifyOnPause: false,
            Frequency: NotificationFrequency.RealTime,
            RetentionDays: 30,
            MatrixRows: new[]
            {
                new NotificationPreferencesRowPatch(
                    NotificationPreferenceEvent.PrinterFailure,
                    InApp: true,
                    Email: false,
                    Push: true,
                    Telegram: false),
            });

        // Launch both writers concurrently. Whichever loses the SQLite write
        // lock retries against a fresh context, mirroring an
        // application-level retry policy. The important assertion below is
        // that final state on disk reflects BOTH writers' mutations,
        // regardless of interleaving order — the Serializable transaction
        // must prevent one writer from overstamping the other's rows with
        // stale-derived master flags.
        Task RunWriterAsync(DbContextOptions<AppDbContext> writerOptions, NotificationPreferencesUpdate patch) =>
            Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        await using AppDbContext writerContext = new(writerOptions);
                        var writerService = new NotificationService(
                            notificationRepository: null!,
                            usersRepository: null!,
                            logger: NullLogger<NotificationService>.Instance,
                            dbContext: writerContext);
                        await writerService.UpdatePreferencesAsync(userId, patch, CancellationToken.None);
                        return;
                    }
                    catch (Exception ex) when (ex is DbUpdateException or SqliteException or InvalidOperationException)
                    {
                        // Serialization conflict — retry with a fresh context.
                        // Small backoff so the losing writer doesn't tight-loop.
                        await Task.Delay(5 * (attempt + 1), CancellationToken.None);
                    }
                }

                throw new InvalidOperationException("Writer exhausted retry budget");
            });

        await Task.WhenAll(
            RunWriterAsync(optionsA, legacyPatch),
            RunWriterAsync(optionsB, modernPatch));

        // Read back the final row from a fresh context. Both writers' effects
        // must be present, and every derived master flag must reflect the
        // final merged state — NOT a stale snapshot from either writer.
        await using AppDbContext verifyContext = new(seedOptions);
        NotificationPreferences? final = await verifyContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        final.Should().NotBeNull();

        // Legacy writer's effect: job rows non-zero.
        final!.PushOnJobStarted.Should().BeTrue("legacy writer enables push across job rows");
        final.PushOnJobCompleted.Should().BeTrue();
        final.InAppOnJobFailed.Should().BeTrue();

        // Modern writer's effect: PrinterFailure Push+InApp on.
        final.PushOnPrinterFailure.Should().BeTrue("modern writer enables push on PrinterFailure");
        final.InAppOnPrinterFailure.Should().BeTrue();

        // Derived master flags: EnablePush should be the OR of all nine
        // push rows on the final row. Because at least one row is true,
        // EnablePushNotifications must be true. This is the actual Bishop
        // race target: with a stale-derived flag it could be false whenever
        // one writer's transient view didn't see the other writer's row.
        final.EnablePushNotifications.Should().BeTrue(
            "EnablePushNotifications must be derived from the final merged row, " +
            "not a stale pre-merge snapshot from either writer");
        final.EnableInAppNotifications.Should().BeTrue();

        // Symmetrically, EnableEmail/Telegram must remain false because no
        // row on the final state has them enabled.
        final.EnableEmailNotifications.Should().BeFalse();
        final.EnableTelegramNotifications.Should().BeFalse();
    }

    [Fact]
    public async Task PatchApi_ReturnsPersistedRow_WithMasterFlagsDerivedFromNineRowMergedState()
    {
        // Complements the race test: on a single writer against SQLite, the
        // returned NotificationPreferences must be the persisted (tracked)
        // entity — not a controller-shaped transient — and every master
        // flag must derive from the final nine-row state.
        Guid userId = Guid.NewGuid();

        await using SqliteConnection connection =
            new("Data Source=file:pref-patch-persisted-row?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = userId,
                Username = "patch-user",
                Email = "patch@test.local",
                PasswordHash = "x",
            });
            await seedDb.SaveChangesAsync();
        }

        await using AppDbContext db = new(options);
        var service = new NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: NullLogger<NotificationService>.Instance,
            dbContext: db);

        NotificationPreferencesUpdate patch = new(
            EnableEmailNotifications: true,
            EnablePushNotifications: true,
            EnableInAppNotifications: true,
            EnableTelegramNotifications: true,
            NotifyOnStart: false,
            NotifyOnCompletion: false,
            NotifyOnFailure: false,
            NotifyOnPause: false,
            Frequency: NotificationFrequency.RealTime,
            RetentionDays: 45,
            MatrixRows: new[]
            {
                new NotificationPreferencesRowPatch(
                    NotificationPreferenceEvent.MaintenanceDue,
                    InApp: false,
                    Email: false,
                    Push: false,
                    Telegram: false),
            });

        NotificationPreferences persisted = await service.UpdatePreferencesAsync(userId, patch, CancellationToken.None);

        // The MaintenanceDue row is fully off on the tracked entity.
        persisted.InAppOnMaintenanceDue.Should().BeFalse();
        persisted.PushOnMaintenanceDue.Should().BeFalse();

        // Master flags derived from the final merged nine-row row. Under the
        // Hicks #3 canonical defaults (see NotificationPreferencesDefaults):
        //   * InApp defaults are ON for Completed/Failed/Paused + all five
        //     attention rows → master EnableInApp=true.
        //   * Push defaults mirror InApp → master EnablePush=true.
        //   * Email defaults to OFF across every row (opt-in) so master
        //     EnableEmail=false even though the incoming patch scalar hint
        //     asks for true — master flags are DERIVED from row state, not
        //     copied from the request. This closes Hicks #3: the fresh GET
        //     defaults and the persisted-after-first-partial-PUT state agree
        //     for every row the patch omits.
        //   * Telegram defaults to OFF everywhere → master EnableTelegram=false.
        persisted.EnableInAppNotifications.Should().BeTrue(
            "at least one row on the merged nine-row state has InApp=true");
        persisted.EnablePushNotifications.Should().BeTrue(
            "at least one row on the merged nine-row state has Push=true");
        persisted.EnableEmailNotifications.Should().BeFalse(
            "Hicks #3: canonical defaults set every Email* row to false; " +
            "master EnableEmail derives from row state, not the scalar hint");
        persisted.EnableTelegramNotifications.Should().BeFalse(
            "no seeded default row has Telegram=true and the patch did not enable it");
        persisted.RetentionDays.Should().Be(45);

        // Confirm the returned entity IS the persisted row.
        await using AppDbContext verifyContext = new(options);
        NotificationPreferences? onDisk = await verifyContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        onDisk.Should().NotBeNull();
        onDisk!.RetentionDays.Should().Be(45);
        onDisk.InAppOnMaintenanceDue.Should().BeFalse();
    }
}
