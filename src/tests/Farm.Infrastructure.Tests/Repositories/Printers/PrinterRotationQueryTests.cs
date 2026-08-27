using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Printers;

/// <summary>
/// Regression coverage for issue #2061: <c>PrintStatsSyncHostedService</c> and
/// <c>MaintenanceAlertHostedService</c> used to call <see cref="IPrintersRepository.GetAllAsync"/>
/// (loading and decrypting the ENTIRE <c>Printers</c> table every tick) and then process only a
/// fixed <c>[0..MaxPrintersPerIteration)</c> prefix, with no rotation cursor. On any farm larger
/// than <c>MaxPrintersPerIteration</c>, printers beyond the first page were never processed by any
/// tick, ever. These tests exercise the real <see cref="EfPrintersRepository"/> against a real
/// relational provider (SQLite) — not the EF Core InMemory provider — because the fix depends on
/// keyset pagination being pushed into SQL (<c>ORDER BY ... LIMIT</c>), which InMemory does not
/// meaningfully validate.
///
/// Prior to the fix, the equivalent of
/// <see cref="RotatesAcrossThreeIntervals_AllPrintersProcessedExactlyOnce_NoStarvation"/> would have
/// failed: seeding 3x the per-iteration cap and driving 3 iterations of "call
/// GetAllAsync().Take(MaxPrintersPerIteration)" would return the SAME first-N printers on every
/// iteration (GetAllAsync has no ordering guarantee tied to processing state), so two-thirds of the
/// fleet would never advance. That is the starvation this suite guards against.
/// </summary>
public sealed class PrinterRotationQueryTests
{
    private const int MaxPrintersPerIteration = 5;

    [Fact]
    public async Task GetForStatsSyncRotationAsync_MaterializesAtMostMaxCountRows_ViaSqlLimit()
    {
        await using SqliteConnection connection =
            new("Data Source=file:stats-rotation-rowcount?mode=memory&cache=shared");
        await connection.OpenAsync();

        List<string> loggedSql = new();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .LogTo(msg => loggedSql.Add(msg), Microsoft.Extensions.Logging.LogLevel.Information)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            SeedPrinters(seed, count: MaxPrintersPerIteration * 3, withStatistics: true);
            _ = await seed.SaveChangesAsync();
        }

        loggedSql.Clear();

        await using AppDbContext act = new(options);
        var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
        List<Printer> page = await repository.GetForStatsSyncRotationAsync(MaxPrintersPerIteration, CancellationToken.None);

        page.Should().HaveCount(MaxPrintersPerIteration,
            "the per-tick rotation query must return at most MaxPrintersPerIteration rows even though 3x that many printers exist");

        string commandText = string.Join('\n', loggedSql);
        commandText.Should().ContainEquivalentOf("LIMIT",
            "the cap must be enforced in SQL (keyset pagination) rather than by loading the full table and slicing in memory — this is the literal issue #2061 defect");
    }

    [Fact]
    public async Task GetForMaintenanceAlertRotationAsync_MaterializesAtMostMaxCountRows_ViaSqlLimit()
    {
        await using SqliteConnection connection =
            new("Data Source=file:alert-rotation-rowcount?mode=memory&cache=shared");
        await connection.OpenAsync();

        List<string> loggedSql = new();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .LogTo(msg => loggedSql.Add(msg), Microsoft.Extensions.Logging.LogLevel.Information)
            .Options;

        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            SeedPrinters(seed, count: MaxPrintersPerIteration * 3, withStatistics: false);
            _ = await seed.SaveChangesAsync();
        }

        loggedSql.Clear();

        await using AppDbContext act = new(options);
        var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
        List<PrinterRotationCandidate> page =
            await repository.GetForMaintenanceAlertRotationAsync(MaxPrintersPerIteration, CancellationToken.None);

        page.Should().HaveCount(MaxPrintersPerIteration,
            "the per-tick rotation query must return at most MaxPrintersPerIteration rows even though 3x that many printers exist");

        string commandText = string.Join('\n', loggedSql);
        commandText.Should().ContainEquivalentOf("LIMIT",
            "the cap must be enforced in SQL (keyset pagination) rather than by loading the full table and slicing in memory — this is the literal issue #2061 defect");
    }

    [Fact]
    public async Task GetForStatsSyncRotationAsync_RotatesAcrossThreeIntervals_AllPrintersSyncedExactlyOnce_NoStarvation()
    {
        await using SqliteConnection connection =
            new("Data Source=file:stats-rotation-coverage?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        const int printerCount = MaxPrintersPerIteration * 3;
        List<Guid> printerIds;
        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            printerIds = SeedPrinters(seed, count: printerCount, withStatistics: true);
            _ = await seed.SaveChangesAsync();
        }

        List<Guid> processedThisRun = new();
        HashSet<Guid> processedEver = new();

        // Simulate 3 hosted-service ticks. Each tick fetches the rotation page and then advances
        // the rotation cursor for every printer it "attempted" — mirroring
        // PrintStatsSyncHostedService's finally block, which calls MarkStatsSyncAttemptedAsync
        // unconditionally (success or failure) so the cursor is decoupled from whether the sync
        // actually succeeded (issue #2061 review finding: PrinterStatistics.LastSyncTime only
        // advances on real success, so using it as the rotation key would re-starve a
        // persistently-failing printer's neighbors).
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using AppDbContext act = new(options);
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            List<Printer> page = await repository.GetForStatsSyncRotationAsync(MaxPrintersPerIteration, CancellationToken.None);

            page.Should().HaveCount(MaxPrintersPerIteration, $"iteration {iteration} must fetch a full page while unsynced printers remain");

            processedThisRun.Clear();
            foreach (Printer printer in page)
            {
                processedThisRun.Add(printer.Id);
                processedEver.Add(printer.Id).Should().BeTrue(
                    $"printer {printer.Id} must not be processed twice before every printer has been processed once (iteration {iteration})");
            }

            foreach (Guid printerId in processedThisRun)
            {
                await repository.MarkStatsSyncAttemptedAsync(
                    printerId,
                    DateTime.UtcNow.AddMilliseconds(iteration),
                    CancellationToken.None);
            }
        }

        processedEver.Should().BeEquivalentTo(printerIds,
            "every seeded printer must be synced within 3 intervals (3x the per-iteration cap) — this is exactly what the pre-fix code (GetAllAsync + fixed-prefix Take) could never do, since it always returned the same first page");
    }

    [Fact]
    public async Task GetForStatsSyncRotationAsync_RotatesAcrossThreeIntervals_EvenWhenEveryAttemptFails_NoStarvation()
    {
        // Regression coverage for a reviewer-found asymmetry (issue #2061): if the rotation cursor
        // were PrinterStatistics.LastSyncTime (which only advances on an ACTUAL successful backend
        // sync), a printer whose sync keeps throwing would never advance its cursor and would
        // permanently monopolize the front of the queue, starving every printer behind it —
        // reintroducing the exact starvation bug #2061 was filed for, just through cursor-not-DB
        // rather than DB-not-cursor. This test never creates or advances any PrinterStatistics row
        // at all (simulating every sync attempt failing before persisting anything) and asserts
        // rotation still fully covers the fleet within 3 intervals purely via
        // MarkStatsSyncAttemptedAsync, proving the cursor is decoupled from sync success.
        await using SqliteConnection connection =
            new("Data Source=file:stats-rotation-failure-coverage?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        const int printerCount = MaxPrintersPerIteration * 3;
        List<Guid> printerIds;
        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            printerIds = SeedPrinters(seed, count: printerCount, withStatistics: false);
            _ = await seed.SaveChangesAsync();
        }

        HashSet<Guid> attemptedEver = new();

        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using AppDbContext act = new(options);
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            List<Printer> page = await repository.GetForStatsSyncRotationAsync(MaxPrintersPerIteration, CancellationToken.None);

            page.Should().HaveCount(MaxPrintersPerIteration, $"iteration {iteration} must fetch a full page while unattempted printers remain");

            foreach (Printer printer in page)
            {
                attemptedEver.Add(printer.Id).Should().BeTrue(
                    $"printer {printer.Id} must not be attempted twice before every printer has been attempted once (iteration {iteration})");

                // Simulate the sync attempt "failing" — never touch PrinterStatistics at all —
                // but still advance the rotation cursor, exactly as PrintStatsSyncHostedService's
                // finally block does regardless of outcome.
                await repository.MarkStatsSyncAttemptedAsync(
                    printer.Id,
                    DateTime.UtcNow.AddMilliseconds(iteration),
                    CancellationToken.None);
            }
        }

        attemptedEver.Should().BeEquivalentTo(printerIds,
            "every seeded printer must rotate to the front within 3 intervals even when every sync attempt fails and PrinterStatistics is never touched — the rotation cursor must be decoupled from sync success");
    }

    [Fact]
    public async Task GetForMaintenanceAlertRotationAsync_RotatesAcrossThreeIntervals_AllPrintersEvaluatedExactlyOnce_NoStarvation()
    {
        await using SqliteConnection connection =
            new("Data Source=file:alert-rotation-coverage?mode=memory&cache=shared");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        const int printerCount = MaxPrintersPerIteration * 3;
        List<Guid> printerIds;
        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            printerIds = SeedPrinters(seed, count: printerCount, withStatistics: false);
            _ = await seed.SaveChangesAsync();
        }

        HashSet<Guid> processedEver = new();

        // Simulate 3 hosted-service ticks, mirroring MaintenanceAlertHostedService's
        // fetch-then-MarkMaintenanceAlertEvaluatedAsync loop (issue #2061).
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using AppDbContext act = new(options);
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            List<PrinterRotationCandidate> page =
                await repository.GetForMaintenanceAlertRotationAsync(MaxPrintersPerIteration, CancellationToken.None);

            page.Should().HaveCount(MaxPrintersPerIteration, $"iteration {iteration} must fetch a full page while unevaluated printers remain");

            foreach (PrinterRotationCandidate candidate in page)
            {
                processedEver.Add(candidate.Id).Should().BeTrue(
                    $"printer {candidate.Id} must not be evaluated twice before every printer has been evaluated once (iteration {iteration})");
                await repository.MarkMaintenanceAlertEvaluatedAsync(
                    candidate.Id,
                    DateTime.UtcNow.AddMilliseconds(iteration),
                    CancellationToken.None);
            }
        }

        processedEver.Should().BeEquivalentTo(printerIds,
            "every seeded printer must be evaluated within 3 intervals (3x the per-iteration cap) — this is exactly what the pre-fix code (GetAllAsync + fixed-prefix Take) could never do, since it always returned the same first page");
    }

    private static List<Guid> SeedPrinters(AppDbContext db, int count, bool withStatistics)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestCo" });
        _ = db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "Model", ManufacturerId = manufacturerId });

        List<Guid> printerIds = new(count);
        for (int i = 0; i < count; i++)
        {
            Guid printerId = Guid.NewGuid();
            printerIds.Add(printerId);
            _ = db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = $"Printer {i}",
                ServerUrl = $"http://printer-{i}.local",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });

            if (withStatistics)
            {
                // Rotation ordering no longer depends on PrinterStatistics.LastSyncTime (issue
                // #2061 review finding — the rotation cursor is now the dedicated
                // PrinterServiceState.LastStatsSyncAttemptedAt field). This row only exists so
                // tests that seed statistics can assert the repository still returns the correct
                // navigation-populated Printer entities; its LastSyncTime value is otherwise inert
                // for rotation purposes.
                _ = db.PrinterStatisticsSet.Add(new PrinterStatistics
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    LastSyncTime = DateTime.UtcNow.AddDays(-count + i),
                });
            }
        }

        return printerIds;
    }

    /// <summary>
    /// Null-op protector — these tests only exercise rotation-query ordering/pagination, not
    /// encryption/decryption of printer credentials.
    /// </summary>
    private sealed class NullSensitiveDataProtector : ISensitiveDataProtector
    {
        public static NullSensitiveDataProtector Instance { get; } = new();
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }
}
