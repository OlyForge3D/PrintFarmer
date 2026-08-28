using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Tests for the periodic firmware re-probe/refresh producer (#1618 / #1613 PR-5):
/// <see cref="PrintersService.RefreshDetectedFirmwareIdentityAsync"/>.
/// </summary>
public sealed class PrintersServiceFirmwareRefreshTests
{
    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_NoFirmwareDetected_IsNoOp()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer, out Mock<IPrintersRepository> repository);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new() { FirmwareFamily = null };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareDetectedAtUtc.Should().BeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_PrinterNotFound_IsNoOp()
    {
        await using AppDbContext db = CreateDbContext();
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);
        // #1656 round 5 (Vasquez): the printer id used below is never registered, so the new
        // existence pre-check (added to stop lock-table growth for nonexistent ids) must also
        // report it as absent.
        repository
            .Setup(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new() { FirmwareFamily = PrinterFirmwareFamily.Klipper };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(Guid.NewGuid(), discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_PrinterDeletedAfterPreLockExistsCheck_DoesNotWriteAndEvictsLock()
    {
        // #1656 / PR #1660 review round 6 (Vasquez, blocking): the round-5 pre-lock existence
        // check only proves the printer was real at that instant — a concurrent delete (from a
        // different scope/request) between that check and this call actually acquiring the
        // per-printer lock must still be caught. The old code trusted FindByIdAsync's own
        // null-check for this, but that call is backed by DbSet.FindAsync (identity-map lookup),
        // which can silently return an already-tracked, non-null instance without ever
        // re-querying the database. Simulating that here: ExistsAsync answers "yes" on its first
        // (pre-lock) call and "no" on its second (post-lock) call, exactly as it would if the row
        // were deleted in that window. The fix must trust the second ExistsAsync call — not
        // FindByIdAsync — and must never even reach FindByIdAsync/SaveChangesAsync once the
        // printer is confirmed gone, and must evict the now-stale lock-table entry rather than
        // retaining it forever.
        await using AppDbContext db = CreateDbContext();
        Guid printerId = Guid.NewGuid();

        var repository = new Mock<IPrintersRepository>();
        // IDISP013 false positive: Moq's SetupSequence lambda is an expression tree
        // describing which member to intercept — it is never invoked directly.
#pragma warning disable IDISP013 // Await in using
        repository
            .SetupSequence(r => r.ExistsAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)  // pre-lock check: printer is still real
            .ReturnsAsync(false); // post-lock check: printer vanished while waiting for/after the lock
#pragma warning restore IDISP013
        repository
            .Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "FindByIdAsync must never be called once the post-lock existence recheck reports the printer gone."));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v-should-never-be-written",
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printerId, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        repository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        PrintersService.HasFirmwareIdentityWriteLockForTests(printerId).Should().BeFalse(
            "the lock-table entry allocated for the pre-lock existence check must be evicted once the printer is confirmed deleted, not retained forever");
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_SaveChangesThrowsConcurrencyExceptionForDeletedRow_ReturnsFalseAndEvictsLock()
    {
        // #1656 / PR #1660 review round 7 (Bishop, blocking): round 6 only closed the window up
        // to and including the post-lock existence recheck -- a delete landing after that
        // recheck but before/during the write itself was never caught by anything, because
        // every fix so far was another check-then-act layer with its own gap after it. The
        // structural fix makes the write (SaveChangesAsync) itself the atomic existence
        // boundary: EF's generated UPDATE is always scoped by primary key and its affected-row
        // count is always verified, so a delete landing at any point up to and including this
        // exact call surfaces as DbUpdateConcurrencyException regardless of how many pre-write
        // checks already passed. This simulates exactly that shape: the pre-lock and post-lock
        // existence rechecks both still report the printer present (the delete has not
        // "happened yet" from either recheck's point of view), and only SaveChangesAsync itself
        // discovers the row is gone -- via a genuine DbUpdateConcurrencyException wrapping an
        // EntityEntry whose current database values are null, exactly what
        // WasFirmwareIdentityPrinterDeletedAsync looks for.
        string dbName = $"PrintersServiceFirmwareRefreshTests_ConcurrencyDeleted_{Guid.NewGuid():N}";
        Guid printerId = Guid.NewGuid();

        await using (AppDbContext seedDb = CreateDbContext(dbName))
        {
            Printer seed = CreatePrinter();
            seed.Id = printerId;
            seedDb.Printers.Add(seed);
            await seedDb.SaveChangesAsync();
        }

        await using AppDbContext db = CreateDbContext(dbName);

        // Load the printer through this scope's own context, exactly as production code would --
        // this is the tracked instance/EntityEntry whose database values will be re-queried by
        // WasFirmwareIdentityPrinterDeletedAsync.
        Printer printer = (await db.Printers.SingleAsync(p => p.Id == printerId))!;
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry = db.Entry(printer);
        var updateEntry = (Microsoft.EntityFrameworkCore.Update.IUpdateEntry)
            ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<Microsoft.EntityFrameworkCore.ChangeTracking.Internal.InternalEntityEntry>)entry).Instance;

        // A wholly separate scope deletes the row for real, out from under this scope's own
        // tracked entity -- this scope's own context is not aware the row is gone until it
        // re-queries the (shared, same-name) underlying store, which is exactly what
        // GetDatabaseValuesAsync (used by WasFirmwareIdentityPrinterDeletedAsync) does.
        await using (AppDbContext concurrentWriterDb = CreateDbContext(dbName))
        {
            Printer toDelete = (await concurrentWriterDb.Printers.SingleAsync(p => p.Id == printerId))!;
            concurrentWriterDb.Printers.Remove(toDelete);
            await concurrentWriterDb.SaveChangesAsync();
        }

        var repository = new Mock<IPrintersRepository>();
        // The pre-lock and post-lock existence rechecks (round 5/6) are mocked to still report
        // the printer present -- from their point of view, nothing has happened yet. Only the
        // write itself (below) discovers the row is genuinely gone.
        repository.Setup(r => r.ExistsAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(r => r.FindByIdAsync(printerId, It.IsAny<CancellationToken>())).ReturnsAsync(printer);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        // IDISP013 false positive: this Returns callback lambda is not invoked here — it
        // runs later when the mocked SaveChangesAsync is actually called during the save.
#pragma warning disable IDISP013 // Await in using
        unitOfWork
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                // Mocking SaveChangesAsync directly bypasses EF's real save pipeline, which
                // normally calls ChangeTracker.DetectChanges() before evaluating entity states --
                // without it, `entry` would still read as Unchanged (production's field mutations
                // just above are not yet detected), and DbUpdateException.Entries filters out
                // anything that isn't Added/Modified/Deleted at the moment it's first accessed.
                // Detecting changes here, at the same point the real SaveChangesAsync would, is
                // what makes the constructed exception genuinely equivalent to one EF would throw.
                db.ChangeTracker.DetectChanges();
                return Task.FromException<int>(new DbUpdateConcurrencyException(
                    "simulated concurrency conflict: row deleted mid-write",
                    new[] { updateEntry }));
            });
#pragma warning restore IDISP013

        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareVersion = "v-should-never-be-observed-as-committed",
            FirmwareDetectionVersion = "moonraker-printer-info-v2",
            FirmwareDetectionConfidence = 0.5m,
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printerId, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        PrintersService.HasFirmwareIdentityWriteLockForTests(printerId).Should().BeFalse(
            "a delete discovered only when the write itself fails must still evict this printer's lock-table entry, not just the pre-write recheck path");

        // #1656 / PR #1660 review round 8 (Bishop, blocking): confirming the delete is not
        // enough on its own -- this scope's `db` must no longer be tracking the deleted `printer`
        // instance, or a subsequent same-scope repository lookup (e.g.
        // PrinterVersionCache.GetMoonrakerVersionAsync's post-refresh FindByIdAsync re-read,
        // which is backed by DbSet.FindAsync) would be satisfied from the identity map with this
        // stale, since-deleted instance instead of genuinely re-querying the database and
        // observing the deletion.
        entry.State.Should().Be(Microsoft.EntityFrameworkCore.EntityState.Detached,
            "the confirmed-deleted printer's tracked entity must be detached, otherwise a same-scope FindAsync re-read after this call would still be satisfied from the identity map with the stale pre-delete instance instead of observing the deletion");
        (await db.Printers.FindAsync(new object?[] { printerId }, CancellationToken.None)).Should().BeNull(
            "with the tracked entity detached, a same-scope FindAsync re-read (exactly what PrinterVersionCache.GetMoonrakerVersionAsync performs immediately after this call returns) must genuinely re-query the database and observe the deletion, not silently return the stale identity-mapped instance");
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_NeverDetectedBefore_AppliesFreshDetection()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareDetectionConfidence = 1.0m,
            FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = DateTime.UtcNow
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeTrue();
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
        printer.FirmwareDetectionSource.Should().Be(FirmwareDetectionSource.Printer);
        printer.FirmwareDetectionConfidence.Should().Be(1.0m);
        printer.FirmwareDetectionVersion.Should().Be(MoonrakerOnboardingResolver.FirmwareProbeVersion);
        printer.FirmwareVersion.Should().Be("v0.12.0");
        printer.FirmwareDetectedAtUtc.Should().NotBeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_WithinReprobeInterval_IsThrottled()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // well within the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = DateTime.UtcNow
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareVersion.Should().Be("v0.11.0"); // unchanged - throttled
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_PastReprobeInterval_RefreshesDetection()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DateTime newDetectedAt = DateTime.UtcNow;
        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareDetectionConfidence = 0.9m,
            FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = newDetectedAt
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeTrue();
        printer.FirmwareVersion.Should().Be("v0.12.0");
        printer.FirmwareDetectionConfidence.Should().Be(0.9m);
        printer.FirmwareDetectedAtUtc.Should().Be(newDetectedAt);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_NeverProbed_ReturnsTrue()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = null;
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeTrue();
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_WithinCadenceWindow_ReturnsFalse()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // well within the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeFalse();
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_PastCadenceWindow_ReturnsTrue()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeTrue();
    }

    [Fact]
    public void IsFirmwareReprobeDue_NullPrinter_Throws()
    {
        using AppDbContext db = CreateDbContext();
        var unitOfWork = new Mock<IUnitOfWork>();
        PrintersService service = CreateService(db, unitOfWork.Object);

        Action act = () => service.IsFirmwareReprobeDue(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_SaveFails_RestoresOriginalFirmwareValuesBeforeRethrow()
    {
        // Bishop round-3 finding: the original fix (reload the entity from the database on
        // SaveChangesAsync failure) still left a gap — if the reload itself also fails to find or
        // restore the row, the tracked entity could keep the failed mutation. The fix restores the
        // pre-mutation snapshot on the tracked entity directly (no I/O dependency) before
        // attempting the best-effort reload, so this must hold regardless of what the reload does.
        //
        // Hicks, PR #1660 review round 5 (blocking): the previous version of this test never
        // added `printer` to `db`, so `FindByIdAsync` (mocked directly) just handed back a plain
        // CLR object EF had never seen — proving only that reassigning C# properties on an
        // arbitrary object works, not that the restore is correct against a *genuinely
        // EF-tracked* entity (where the change tracker has already recorded the mutated values
        // as "Modified" against its own original-value snapshot). This version seeds the printer
        // into a real `AppDbContext`/table first, loads it through the real
        // `EfPrintersRepository` (so `printer` is the actual tracked instance EF's identity map
        // hands back — exactly what `PrinterVersionCache` holds a reference to in production),
        // and only mocks `IUnitOfWork.SaveChangesAsync` itself to fail — never touching the real
        // `AppDbContext.SaveChangesAsync`, so nothing the mutation touched is ever actually
        // persisted, and a fresh read from a separate context can independently confirm that.
        string dbName = $"PrintersServiceFirmwareRefreshTests_SaveFails_{Guid.NewGuid():N}";
        Guid printerId = Guid.NewGuid();
        DateTime originalDetectedAt = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence

        await using (AppDbContext seedDb = CreateDbContext(dbName))
        {
            Printer seed = CreatePrinter();
            seed.Id = printerId;
            seed.FirmwareFamily = PrinterFirmwareFamily.Klipper;
            seed.GcodeDialect = PrinterGcodeDialect.Klipper;
            seed.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
            seed.FirmwareVersion = "v0.11.0";
            seed.FirmwareDetectionVersion = "moonraker-printer-info-v1";
            seed.FirmwareDetectionConfidence = 1.0m;
            seed.FirmwareDetectedAtUtc = originalDetectedAt;
            seedDb.Printers.Add(seed);
            await seedDb.SaveChangesAsync();
        }

        await using AppDbContext requestDb = CreateDbContext(dbName);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers)
            .Returns(new EfPrintersRepository(requestDb, Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>()));
        unitOfWork
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated database outage"));
        PrintersService service = CreateService(requestDb, unitOfWork.Object);

        // Load the printer through the real repository first, exactly as
        // RefreshDetectedFirmwareIdentityAsync itself will — this is the genuinely tracked
        // instance whose change-tracker state (not just its CLR property values) must end up
        // consistent after the restore.
        Printer trackedPrinter = (await unitOfWork.Object.Printers.FindByIdAsync(printerId, CancellationToken.None))!;
        trackedPrinter.FirmwareVersion.Should().Be("v0.11.0");

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareVersion = "v99.99.99", // must never leak through on a failed save
            FirmwareDetectionVersion = "moonraker-printer-info-v2",
            FirmwareDetectionConfidence = 0.5m,
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };

        Func<Task> act = async () => await service.RefreshDetectedFirmwareIdentityAsync(printerId, discovered, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The tracked entity — the same instance a caller such as PrinterVersionCache would be
        // holding a reference to — must show the pre-mutation values, not the discovered probe's
        // rejected values, even though EF's change tracker had already recorded the mutation
        // in-place on this exact object.
        trackedPrinter.FirmwareVersion.Should().Be("v0.11.0");
        trackedPrinter.FirmwareDetectionVersion.Should().Be("moonraker-printer-info-v1");
        trackedPrinter.FirmwareDetectionConfidence.Should().Be(1.0m);
        trackedPrinter.FirmwareDetectedAtUtc.Should().Be(originalDetectedAt);
        trackedPrinter.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        trackedPrinter.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);

        // Independent confirmation from a fresh context/change-tracker: the failed save was
        // truly never persisted (IUnitOfWork.SaveChangesAsync was mocked to throw before ever
        // reaching the real AppDbContext.SaveChangesAsync), so the database itself still holds
        // exactly the original values.
        await using AppDbContext verifyDb = CreateDbContext(dbName);
        Printer? persisted = await verifyDb.Printers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == printerId);
        persisted.Should().NotBeNull();
        persisted!.FirmwareVersion.Should().Be("v0.11.0");
        persisted.FirmwareDetectionVersion.Should().Be("moonraker-printer-info-v1");
        persisted.FirmwareDetectedAtUtc.Should().Be(originalDetectedAt);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_DeclinedButConcurrentWriterAlreadyCommittedNewerIdentity_ReloadsInsteadOfServingStaleValue()
    {
        // Vasquez, PR #1660 review round 2 (blocking): "concurrent cache misses can both pass
        // the reprobe guard, write different firmware identities, and then have the slower
        // request return/cache its older tracked value instead of the row that actually won in
        // the database." This reproduces exactly that shape for the "declined — lost the
        // cadence race" branch: this scope's own `printer` was loaded (and its cadence decision
        // made) against firmware data that a *different* scope has since superseded in the
        // database. Without the fix, this scope's own tracked entity — which is what a caller
        // like PrinterVersionCache reads afterward — would still show the stale value it was
        // loaded with, even though the row it names no longer contains that value.
        string dbName = $"PrintersServiceFirmwareRefreshTests_ConcurrentDeclined_{Guid.NewGuid():N}";
        Guid printerId = Guid.NewGuid();

        await using (AppDbContext seedDb = CreateDbContext(dbName))
        {
            Printer seed = CreatePrinter();
            seed.Id = printerId;
            seed.FirmwareFamily = PrinterFirmwareFamily.Klipper;
            seed.GcodeDialect = PrinterGcodeDialect.Klipper;
            seed.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
            seed.FirmwareVersion = "v-request-scope-original";
            seed.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // within the default 6h cadence
            seedDb.Printers.Add(seed);
            await seedDb.SaveChangesAsync();
        }

        // This scope ("the slower request"): loads its own tracked copy of the printer. Its
        // FirmwareDetectedAtUtc (1h old) is within the cadence window, so this scope will decide
        // *not* to reprobe — matching the "lost the cadence race" branch of
        // RefreshDetectedFirmwareIdentityAsync.
        await using AppDbContext requestDb = CreateDbContext(dbName);
        var requestUnitOfWork = new Mock<IUnitOfWork>();
        requestUnitOfWork.Setup(work => work.Printers)
            .Returns(new EfPrintersRepository(requestDb, Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>()));
        requestUnitOfWork
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
#pragma warning disable IDISP013 // Await in using — Returns callback runs later, not here
            .Returns((CancellationToken token) => requestDb.SaveChangesAsync(token));
#pragma warning restore IDISP013
        PrintersService requestService = CreateService(requestDb, requestUnitOfWork.Object);

        Printer trackedInRequestScope = (await requestUnitOfWork.Object.Printers.FindByIdAsync(printerId, CancellationToken.None))!;
        trackedInRequestScope.FirmwareVersion.Should().Be("v-request-scope-original");

        // "A different scope's request" wins the race: it committed a newer firmware identity
        // for the same printer, using its own separate DbContext, *after* this scope already
        // loaded (and formed its cadence decision on) the row above.
        await using (AppDbContext concurrentWriterDb = CreateDbContext(dbName))
        {
            Printer fromConcurrentWriter = (await concurrentWriterDb.Printers.FindAsync(printerId))!;
            fromConcurrentWriter.FirmwareVersion = "v-concurrent-winner";
            fromConcurrentWriter.FirmwareDetectedAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await concurrentWriterDb.SaveChangesAsync();
        }

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v-this-request-would-have-written",
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };

        bool refreshed = await requestService.RefreshDetectedFirmwareIdentityAsync(printerId, discovered, CancellationToken.None);

        refreshed.Should().BeFalse("this scope's own cadence decision (made before the concurrent write) declined to reprobe");
        requestUnitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // The fix: this scope's tracked `printer` instance — the exact object a caller such as
        // PrinterVersionCache holds a reference to — must be reloaded from the database before
        // this method returns, so it reflects the concurrent winner's row rather than the
        // now-superseded value it happened to be loaded with.
        trackedInRequestScope.FirmwareVersion.Should().Be("v-concurrent-winner");
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_TwoConcurrentCallsForSamePrinter_SerializeInsteadOfRacing()
    {
        // Bishop + Vasquez, PR #1660 review round 3 (blocking): "the reload fix doesn't fully
        // close the concurrency issue ... request A can save, reload, and return its tracked
        // entity before request B's later save commits, so A still serves/caches a value that
        // does not ultimately win in the database." That is a *different* shape than the
        // round-2 regression test above (which covers "a competing write had already committed
        // before I even loaded"): here, both requests are genuinely in flight *at the same
        // time*, each independently deciding — from data loaded before either had written
        // anything — that a reprobe is due.
        //
        // This proves the round-3 fix (a per-printer System.Threading.SemaphoreSlim held across
        // the *entire* body of RefreshDetectedFirmwareIdentityAsync, not just the save) actually
        // serializes two such calls: request B's own load of the printer cannot even begin until
        // request A has fully completed its write, reload, and released the lock. Without the
        // lock, request B — started while request A is deliberately held open inside
        // SaveChangesAsync — would run its FindByIdAsync concurrently against the same
        // (still-stale, still-"due") row A loaded, exactly reproducing the two-successful-writers
        // race the reviewers described.
        string dbName = $"PrintersServiceFirmwareRefreshTests_Concurrent_{Guid.NewGuid():N}";
        Guid printerId = Guid.NewGuid();

        await using (AppDbContext seedDb = CreateDbContext(dbName))
        {
            Printer seed = CreatePrinter();
            seed.Id = printerId;
            seed.FirmwareFamily = PrinterFirmwareFamily.Klipper;
            seed.GcodeDialect = PrinterGcodeDialect.Klipper;
            seed.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
            seed.FirmwareVersion = "v-seed";
            // Well past the default cadence window, so both requests independently observe
            // "reprobe is due" before either one attempts to write anything.
            seed.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-24);
            seedDb.Printers.Add(seed);
            await seedDb.SaveChangesAsync();
        }

        var requestAEnteredSaveSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequestASaveSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // "Request A": its SaveChangesAsync is deliberately held open by releaseRequestASaveSignal
        // so the test can deterministically control exactly when request A's write commits,
        // relative to when request B is started — without relying on a race-prone Task.Delay to
        // approximate "request A is currently mid-write".
        await using AppDbContext dbA = CreateDbContext(dbName);
        var unitOfWorkA = new Mock<IUnitOfWork>();
        unitOfWorkA.Setup(work => work.Printers)
            .Returns(new EfPrintersRepository(dbA, Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>()));
        unitOfWorkA
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                requestAEnteredSaveSignal.TrySetResult();
#pragma warning disable VSTHRD003 // releaseRequestASaveSignal is a TaskCompletionSource this test controls to hold the save open; not a foreign/UI-thread task.
                await releaseRequestASaveSignal.Task;
#pragma warning restore VSTHRD003
                return await dbA.SaveChangesAsync(token);
            });
        PrintersService serviceA = CreateService(dbA, unitOfWorkA.Object);

        // "Request B": a plain, immediately-completing service instance for the *same* printer
        // id. FirmwareIdentityWriteLocks is a static, process-wide table (matching the existing
        // scope of PrinterVersionCache.ForceRefreshWindows and
        // PrintJobManagementService.PrinterHistorySyncLocks elsewhere in this codebase), so this
        // separate PrintersService instance still contends for the exact same lock as serviceA.
        await using AppDbContext dbB = CreateDbContext(dbName);
        var unitOfWorkB = new Mock<IUnitOfWork>();
        unitOfWorkB.Setup(work => work.Printers)
            .Returns(new EfPrintersRepository(dbB, Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>()));
        unitOfWorkB
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
#pragma warning disable IDISP013 // Await in using — Returns callback runs later, not here
            .Returns((CancellationToken token) => dbB.SaveChangesAsync(token));
#pragma warning restore IDISP013
        PrintersService serviceB = CreateService(dbB, unitOfWorkB.Object);

        DiscoveredPrinterDto discoveredA = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v-request-a",
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };
        DiscoveredPrinterDto discoveredB = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v-request-b",
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };

        Task<bool> taskA = serviceA.RefreshDetectedFirmwareIdentityAsync(printerId, discoveredA, CancellationToken.None);

        // Wait until request A is definitely holding the lock and mid-write (inside
        // SaveChangesAsync) before starting request B, so B is guaranteed to have to contend for
        // the lock rather than possibly acquiring it first by scheduling luck.
        await requestAEnteredSaveSignal.Task;

        Task<bool> taskB = serviceB.RefreshDetectedFirmwareIdentityAsync(printerId, discoveredB, CancellationToken.None);

        // Give request B every opportunity to run if the lock were not actually enforced: a
        // regression that dropped the per-printer semaphore would let this complete immediately
        // (racing its own FindByIdAsync against request A's still-in-flight write), instead of
        // blocking.
        await Task.Delay(200);
        taskB.IsCompleted.Should().BeFalse(
            "request B must block on the shared per-printer lock while request A is still inside its critical section");

        releaseRequestASaveSignal.TrySetResult();

        bool refreshedA = await taskA;
        bool refreshedB = await taskB;

        refreshedA.Should().BeTrue("request A's printer was well past the cadence window");

        // Request B's own load could only happen after request A fully committed and released
        // the lock, so request B observes request A's fresh FirmwareDetectedAtUtc and correctly
        // declines to reprobe again — it must not have raced request A using the stale data both
        // would otherwise have loaded before either wrote anything.
        refreshedB.Should().BeFalse(
            "request B's load happens only after request A's write is fully committed, so it must see the freshly-detected identity and decline");

        await using AppDbContext verifyDb = CreateDbContext(dbName);
        Printer persisted = (await verifyDb.Printers.FindAsync(printerId))!;
        persisted.FirmwareVersion.Should().Be("v-request-a");
    }

    [Fact]
    public async Task RemoveAsync_PrinterWithExistingFirmwareLockEntryAndNoInFlightRefresh_EvictsLockEntry()
    {
        // #1656 / PR #1660 review round 7 (Vasquez, blocking): round 6 only evicted a
        // FirmwareIdentityWriteLocks entry when a refresh/detect call itself discovered
        // mid-flight that the printer had vanished. A printer that completes an ordinary,
        // successful refresh and is deleted afterward -- with no refresh/detect racing the
        // delete at all -- left its lock-table entry retained forever, since nothing tied
        // eviction to the printer's actual delete lifecycle. This proves RemoveAsync itself now
        // reclaims that entry unconditionally, for exactly this "quiet" deletion shape.
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer, out Mock<IPrintersRepository> repository);
        repository
            .Setup(r => r.RemoveAsync(printer, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PrintersService service = CreateService(db, unitOfWork.Object);

        // Successfully complete an ordinary refresh so the lock-table entry gets allocated,
        // exactly as production code would for any printer that has ever been probed -- no
        // delete racing it at all.
        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareDetectionConfidence = 1.0m,
            FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };
        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);
        refreshed.Should().BeTrue();
        PrintersService.HasFirmwareIdentityWriteLockForTests(printer.Id).Should().BeTrue(
            "the refresh above must have allocated a lock-table entry for this printer, exactly like production traffic would");

        await service.RemoveAsync(printer, CancellationToken.None);

        PrintersService.HasFirmwareIdentityWriteLockForTests(printer.Id).Should().BeFalse(
            "deleting a printer must reclaim its firmware lock-table entry unconditionally, even with no refresh/detect call racing the delete at all");
    }

    private static AppDbContext CreateDbContext(string? name = null)
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(name ?? $"PrintersServiceFirmwareRefreshTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Firmware refresh test printer",
        ServerUrl = "http://moonraker.local",
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static Mock<IUnitOfWork> CreateUnitOfWork(Printer printer, out Mock<IPrintersRepository> repository)
    {
        repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        // #1656 round 5 (Vasquez): RefreshDetectedFirmwareIdentityAsync/DetectFirmwareIdentityAsync
        // now confirm existence before allocating a lock-table entry; this printer id is real.
        repository
            .Setup(r => r.ExistsAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        return unitOfWork;
    }

    private static PrintersService CreateService(AppDbContext db, IUnitOfWork unitOfWork) =>
        new(
            unitOfWork,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
}
