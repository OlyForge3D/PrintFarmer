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

namespace Farm.Web.Api.Tests.Services.Printers;

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
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new() { FirmwareFamily = PrinterFirmwareFamily.Klipper };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(Guid.NewGuid(), discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
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
        // restore the row (as happens here, since this test's `db` never actually persisted
        // `printer`), the tracked entity could keep the failed mutation. The fix restores the
        // pre-mutation snapshot on the tracked entity directly (no I/O dependency) before
        // attempting the best-effort reload, so this must hold regardless of what the reload does.
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectionVersion = "moonraker-printer-info-v1";
        printer.FirmwareDetectionConfidence = 1.0m;
        DateTime originalDetectedAt = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        printer.FirmwareDetectedAtUtc = originalDetectedAt;

        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        unitOfWork
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated database outage"));
        PrintersService service = CreateService(db, unitOfWork.Object);

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

        Func<Task> act = () => service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // `printer` was never added to `db`, so the best-effort ReloadAsync call finds no
        // matching row and cannot itself restore anything — proving the manual snapshot/restore
        // (not the reload) is what guarantees no caller ever observes the failed mutation.
        printer.FirmwareVersion.Should().Be("v0.11.0");
        printer.FirmwareDetectionVersion.Should().Be("moonraker-printer-info-v1");
        printer.FirmwareDetectionConfidence.Should().Be(1.0m);
        printer.FirmwareDetectedAtUtc.Should().Be(originalDetectedAt);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
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
            .Returns((CancellationToken token) => requestDb.SaveChangesAsync(token));
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
                await releaseRequestASaveSignal.Task;
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
            .Returns((CancellationToken token) => dbB.SaveChangesAsync(token));
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
