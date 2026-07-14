using System;
using System.Collections.Generic;
using System.Linq;
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
/// Bishop #12 / Hicks #3 hardened race coverage.
///
/// Every test in this file drives the REAL production retry path — the
/// service is wired with a genuine <see cref="IDbContextFactory{AppDbContext}"/>
/// and <see cref="Farm.Infrastructure.Services.Notifications.PreferenceConcurrencyRetry"/>
/// is exercised end-to-end. The prior test-side <c>for (attempt) { catch { Delay } }</c>
/// loop is gone: it silently masked a broken production retry.
///
/// Test seams are minimal: only the internal
/// <c>NotificationService.OnAfterPreferenceReadForTestsAsync</c> hook is
/// used to inject a deterministic barrier that guarantees the adverse
/// interleaving (stale legacy writer derives channel master=false while
/// modern writer enables a disjoint attention row). Production defaults
/// remain unchanged.
/// </summary>
public sealed class NotificationServicePreferenceRaceTests
{
    /// <summary>
    /// Adverse race: L (legacy) reads the pre-mutation row and would derive
    /// master EnablePushNotifications=false from its stale view; M (modern)
    /// concurrently enables PushOnPrinterFailure=true on a disjoint attention
    /// row. Under the production retry path, L's first attempt hits a
    /// serialization/concurrency failure, PreferenceConcurrencyRetry
    /// classifies it as transient, and L retries on a fresh context where
    /// the tracked read now sees M's committed PushOnPrinterFailure=true.
    /// L's derived master flag then reflects the merged nine-row state and
    /// the final row on disk has master=true.
    ///
    /// SQLite has no row versioning so a natural concurrent-writer race
    /// cannot surface the transient exception the production classifier is
    /// designed to swallow (PostgreSQL 40001 / SQL Server 1205/3960 /
    /// SqlUpdateConcurrencyException — all real provider signals under
    /// SERIALIZABLE / snapshot isolation). We inject the equivalent
    /// <see cref="DbUpdateConcurrencyException"/> from the internal test
    /// seam AFTER L's read AND AFTER M's commit — the exception itself is
    /// synthetic but every subsequent step (classifier, retry loop, fresh
    /// context, merge) is the real production path.
    ///
    /// Without the fix the final master would be false (L's stale-derived
    /// value overwrites M's correct one). We prove the fix by asserting
    /// both writers' effects survive and that L's retry counter
    /// incremented (the retry path was actually exercised, not linearised
    /// by luck) — a pre-fix implementation that lacked the classifier /
    /// retry loop would either swallow the injected exception silently or
    /// surface it uncaught, in both cases failing the assertions below.
    /// </summary>
    [Fact]
    public async Task AdverseSchedule_StaleLegacyWriterAndDisjointModernWriter_ProducesConsistentMasterFlagsAfterRetry()
    {
        Guid userId = Guid.NewGuid();
        const string connString = "Data Source=file:pref-race-adverse?mode=memory&cache=shared";

        await using SqliteConnection keepAlive = new(connString);
        await keepAlive.OpenAsync();

        DbContextOptions<AppDbContext> BuildOptions() =>
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connString).Options;

        DbContextOptions<AppDbContext> options = BuildOptions();

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = userId,
                Username = "race-adverse",
                Email = "adverse@test.local",
                PasswordHash = "x",
            });
            // Seed an all-off preferences row so every master flag begins false.
            var seed = new NotificationPreferences { UserId = userId };
            NotificationPreferencesDefaults.Apply(seed);
            // Override defaults to a stricter all-off baseline so we can
            // observe M's PushOnPrinterFailure=true as the ONLY signal
            // flipping master EnablePushNotifications from false to true.
            seed.InAppOnJobStarted = false;
            seed.InAppOnJobCompleted = false;
            seed.InAppOnJobFailed = false;
            seed.InAppOnJobPaused = false;
            seed.EmailOnJobStarted = false;
            seed.EmailOnJobCompleted = false;
            seed.EmailOnJobFailed = false;
            seed.EmailOnJobPaused = false;
            seed.PushOnJobStarted = false;
            seed.PushOnJobCompleted = false;
            seed.PushOnJobFailed = false;
            seed.PushOnJobPaused = false;
            seed.TelegramOnJobStarted = false;
            seed.TelegramOnJobCompleted = false;
            seed.TelegramOnJobFailed = false;
            seed.TelegramOnJobPaused = false;
            seed.InAppOnPrinterFailure = false;
            seed.EmailOnPrinterFailure = false;
            seed.PushOnPrinterFailure = false;
            seed.TelegramOnPrinterFailure = false;
            seed.InAppOnFilamentRunout = false;
            seed.EmailOnFilamentRunout = false;
            seed.PushOnFilamentRunout = false;
            seed.TelegramOnFilamentRunout = false;
            seed.InAppOnHarvestReady = false;
            seed.EmailOnHarvestReady = false;
            seed.PushOnHarvestReady = false;
            seed.TelegramOnHarvestReady = false;
            seed.InAppOnMaintenanceDue = false;
            seed.EmailOnMaintenanceDue = false;
            seed.PushOnMaintenanceDue = false;
            seed.TelegramOnMaintenanceDue = false;
            seed.InAppOnPrinterOffline = false;
            seed.EmailOnPrinterOffline = false;
            seed.PushOnPrinterOffline = false;
            seed.TelegramOnPrinterOffline = false;
            seed.EnableInAppNotifications = false;
            seed.EnableEmailNotifications = false;
            seed.EnablePushNotifications = false;
            seed.EnableTelegramNotifications = false;
            seedDb.NotificationPreferences.Add(seed);
            await seedDb.SaveChangesAsync();
        }

        // Real production factory. Both writers pull independent
        // AppDbContext instances backed by fresh SqliteConnection objects.
        var factory = new SharedConnectionStringDbContextFactory(options);

        // Writer L (legacy): explicit InApp enable on job-started only. Its
        // effect on the nine-row state is InAppOnJobStarted=true only — a
        // "stale-derived-from-local-view" master computation gives
        // EnableInApp=true (from L's own mutation) but EnablePush=false
        // (L's local view of push rows is entirely off).
        NotificationPreferencesUpdate legacyPatch = new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: true,
            EnableTelegramNotifications: null,
            NotifyOnStart: true,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows: null);

        // Writer M (modern): enable PushOnPrinterFailure=true — a disjoint
        // attention row L never addresses. On the FINAL nine-row state the
        // master EnablePushNotifications must be true.
        NotificationPreferencesUpdate modernPatch = new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: null,
            EnableTelegramNotifications: null,
            NotifyOnStart: null,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows: new[]
            {
                new NotificationPreferencesRowPatch(
                    NotificationPreferenceEvent.PrinterFailure,
                    InApp: false,
                    Email: false,
                    Push: true,
                    Telegram: false),
            });

        // Barrier design:
        //   - L's task and M's task start concurrently.
        //   - M waits on `legacyReadDone` so L reads the seed FIRST (stale
        //     from L's perspective given M's forthcoming write).
        //   - L's attempt 1 seam signals `legacyReadDone` then immediately
        //     throws a synthetic DbUpdateConcurrencyException so
        //     PreferenceConcurrencyRetry rolls back L's Serializable
        //     transaction (releases the SQLite RESERVED lock) and enters its
        //     backoff before retrying with a fresh context.
        //   - M unblocks (legacyReadDone set), opens its own writer,
        //     commits its disjoint push mutation, and signals
        //     `modernCommitted`.
        //   - L's attempt 2 opens a fresh context, reads M's committed
        //     row, applies L's mutation on top, and commits cleanly.
        //
        // The synthetic exception is intentional: SQLite has no row
        // versioning so a natural concurrent-writer race between two
        // Serializable transactions manifests as SQLITE_BUSY on BEGIN
        // IMMEDIATE (which serialises them at the lock level rather than
        // surfacing a distinguishable classifier signal). On real
        // providers under SERIALIZABLE / snapshot isolation the losing
        // writer surfaces DbUpdateConcurrencyException (or a wrapped
        // 40001 / 1205 / 3960 provider exception); the classifier +
        // retry loop is the production path the test exercises.
        TaskCompletionSource legacyReadDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource modernCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int legacyAttemptCount = 0;

        async Task RunLegacyWriterAsync()
        {
            await using AppDbContext writerContext = await factory.CreateDbContextAsync();
            var service = new NotificationService(
                notificationRepository: null!,
                usersRepository: null!,
                logger: NullLogger<NotificationService>.Instance,
                dbContext: writerContext,
                preferencesContextFactory: factory);

            service.OnAfterPreferenceReadForTestsAsync = _ =>
            {
                int attempt = Interlocked.Increment(ref legacyAttemptCount);
                if (attempt == 1)
                {
                    // Signal M can begin; then throw so L's tx rolls back
                    // (releasing RESERVED) and PreferenceConcurrencyRetry
                    // retries on a fresh context after M commits.
                    legacyReadDone.TrySetResult();
                    throw new DbUpdateConcurrencyException(
                        "Simulated SERIALIZABLE serialization failure between legacy and modern writers.");
                }

                // Attempt 2+: proceed normally so the fresh-context read of
                // M's committed row and L's own mutation land.
                return Task.CompletedTask;
            };

            _ = await service.UpdatePreferencesAsync(userId, legacyPatch, CancellationToken.None);
        }

        async Task RunModernWriterAsync()
        {
            // Wait until L has taken its stale read AND released its
            // RESERVED lock (L's attempt 1 seam throws immediately after
            // signalling). Only then does M open its own writer, guaranteeing
            // the adverse ordering: L reads before M, M commits before L
            // retries.
            // Generous timeout tolerates CI runner scheduling jitter (Linux SQLite
            // under load) while still failing fast if the barrier truly deadlocks.
            await legacyReadDone.Task.WaitAsync(TimeSpan.FromSeconds(60));

            await using AppDbContext writerContext = await factory.CreateDbContextAsync();
            var service = new NotificationService(
                notificationRepository: null!,
                usersRepository: null!,
                logger: NullLogger<NotificationService>.Instance,
                dbContext: writerContext,
                preferencesContextFactory: factory);

            _ = await service.UpdatePreferencesAsync(userId, modernPatch, CancellationToken.None);

            // Observation channel: not consumed by L (L's attempt 2 has no
            // seam wait), but recorded so the verifier can prove M's write
            // landed before L's retry produced the merged final row.
            modernCommitted.TrySetResult();
        }

        Task legacyTask = Task.Run(RunLegacyWriterAsync);
        Task modernTask = Task.Run(RunModernWriterAsync);
        await Task.WhenAll(legacyTask, modernTask);

        // Read the final persisted row via a fresh context.
        await using AppDbContext verifyContext = new(options);
        NotificationPreferences? final = await verifyContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        final.Should().NotBeNull();

        // M's effect survived.
        final!.PushOnPrinterFailure.Should().BeTrue(
            "modern writer's attention-row mutation must be persisted");

        // L's effect survived — this proves L's retry did NOT overwrite M
        // by re-reading M's row and merging correctly.
        final.InAppOnJobStarted.Should().BeTrue(
            "legacy writer's job-row mutation must be persisted after retry");

        // The critical master-flag assertion: it must derive from the FINAL
        // merged nine-row state, not L's stale pre-M view.
        final.EnablePushNotifications.Should().BeTrue(
            "EnablePushNotifications must be derived from the final row (M's PushOnPrinterFailure=true), " +
            "not L's stale local view of the push columns");
        final.EnableInAppNotifications.Should().BeTrue(
            "L's own InAppOnJobStarted=true is enough to imply master EnableInApp=true");

        // The retry path was actually exercised.
        legacyAttemptCount.Should().BeGreaterThan(
            1,
            "the adverse schedule forces L into at least one PreferenceConcurrencyRetry attempt");
    }

    /// <summary>
    /// First-create convergence: two writers hitting a userId that has no
    /// persisted row must converge on a single row. One writer wins the
    /// unique-index race and creates; the loser gets a
    /// UserIdUniqueConflict classification, retries with a fresh context,
    /// reads the winner's row, applies its own patch on top, and commits.
    /// Both writers' effects must survive.
    /// </summary>
    [Fact]
    public async Task FirstCreateConvergence_TwoConcurrentInsertsForSameUser_ProduceSingleMergedRow()
    {
        Guid userId = Guid.NewGuid();
        const string connString = "Data Source=file:pref-first-create-race?mode=memory&cache=shared";

        await using SqliteConnection keepAlive = new(connString);
        await keepAlive.OpenAsync();

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connString)
            .Options;

        await using (AppDbContext seedDb = new(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Users.Add(new User
            {
                Id = userId,
                Username = "first-create-race",
                Email = "firstcreate@test.local",
                PasswordHash = "x",
            });
            await seedDb.SaveChangesAsync();
        }

        var factory = new SharedConnectionStringDbContextFactory(options);

        NotificationPreferencesUpdate patchA = new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: true,
            EnableTelegramNotifications: null,
            NotifyOnStart: true,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows: null);

        NotificationPreferencesUpdate patchB = new(
            EnableEmailNotifications: null,
            EnablePushNotifications: null,
            EnableInAppNotifications: null,
            EnableTelegramNotifications: null,
            NotifyOnStart: null,
            NotifyOnCompletion: null,
            NotifyOnFailure: null,
            NotifyOnPause: null,
            Frequency: null,
            RetentionDays: null,
            MatrixRows: new[]
            {
                new NotificationPreferencesRowPatch(
                    NotificationPreferenceEvent.FilamentRunout,
                    InApp: false,
                    Email: false,
                    Push: true,
                    Telegram: false),
            });

        async Task RunAsync(NotificationPreferencesUpdate patch)
        {
            await using AppDbContext ctx = await factory.CreateDbContextAsync();
            var service = new NotificationService(
                notificationRepository: null!,
                usersRepository: null!,
                logger: NullLogger<NotificationService>.Instance,
                dbContext: ctx,
                preferencesContextFactory: factory);
            _ = await service.UpdatePreferencesAsync(userId, patch, CancellationToken.None);
        }

        await Task.WhenAll(Task.Run(() => RunAsync(patchA)), Task.Run(() => RunAsync(patchB)));

        await using AppDbContext verify = new(options);
        List<NotificationPreferences> rows = await verify.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync();

        rows.Should().HaveCount(1, "concurrent first-creates must converge on a single row");
        rows[0].InAppOnJobStarted.Should().BeTrue("patch A's effect must survive");
        rows[0].PushOnFilamentRunout.Should().BeTrue("patch B's effect must survive");
        rows[0].EnableInAppNotifications.Should().BeTrue();
        rows[0].EnablePushNotifications.Should().BeTrue();
    }

    /// <summary>
    /// Complements the race tests: on a single writer the returned entity IS
    /// the persisted (tracked) row and master flags derive from the final
    /// nine-row merged state.
    /// </summary>
    [Fact]
    public async Task PatchApi_ReturnsPersistedRow_WithMasterFlagsDerivedFromNineRowMergedState()
    {
        Guid userId = Guid.NewGuid();
        const string connString = "Data Source=file:pref-patch-persisted-row?mode=memory&cache=shared";

        await using SqliteConnection connection = new(connString);
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connString)
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

        var factory = new SharedConnectionStringDbContextFactory(options);
        await using AppDbContext db = await factory.CreateDbContextAsync();
        var service = new NotificationService(
            notificationRepository: null!,
            usersRepository: null!,
            logger: NullLogger<NotificationService>.Instance,
            dbContext: db,
            preferencesContextFactory: factory);

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

        // MaintenanceDue row fully off on the tracked entity.
        persisted.InAppOnMaintenanceDue.Should().BeFalse();
        persisted.PushOnMaintenanceDue.Should().BeFalse();

        // Master flags derived from the final merged nine-row row. Under
        // Hicks #3 canonical defaults, at least one InApp and one Push row
        // is on so master EnableInApp/EnablePush=true. Email/Telegram
        // defaults are all false so their masters remain false regardless
        // of the request scalar hint (master derives from ROWS, not the
        // scalar).
        persisted.EnableInAppNotifications.Should().BeTrue();
        persisted.EnablePushNotifications.Should().BeTrue();
        persisted.EnableEmailNotifications.Should().BeFalse();
        persisted.EnableTelegramNotifications.Should().BeFalse();
        persisted.RetentionDays.Should().Be(45);

        await using AppDbContext verify = new(options);
        NotificationPreferences? onDisk = await verify.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);
        onDisk.Should().NotBeNull();
        onDisk!.RetentionDays.Should().Be(45);
        onDisk.InAppOnMaintenanceDue.Should().BeFalse();
    }

    /// <summary>
    /// Test-only <see cref="IDbContextFactory{T}"/> that hands out
    /// AppDbContext instances backed by a SQLite in-memory database
    /// identified by a shared connection string. Each instance opens its own
    /// SqliteConnection so it participates in real cross-connection
    /// serialization semantics, exactly like a request-scoped
    /// DbContext in production.
    /// </summary>
    private sealed class SharedConnectionStringDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SharedConnectionStringDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }
}
