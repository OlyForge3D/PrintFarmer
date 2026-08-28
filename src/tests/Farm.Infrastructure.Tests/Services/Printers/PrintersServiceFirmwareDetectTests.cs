using System.Net;
using System.Text;
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
/// Tests for the on-demand firmware detection producer
/// <see cref="PrintersService.DetectFirmwareIdentityAsync"/>.
///
/// The persisted firmware columns the calibration context resolver reads previously had no
/// operator-reachable writer: onboarding wrote them once, and the only refresh path was a side
/// effect of a discovery scan posting back a matching ServerUrl, throttled to a multi-hour cadence.
/// A printer registered before firmware detection existed therefore could never reach a calibratable
/// state. These tests pin the behaviour that closes that gap.
/// </summary>
public sealed class PrintersServiceFirmwareDetectTests
{
    private const string PrinterInfoPayload =
        """
        {"result":{"state_message":"Printer is ready","klipper_path":"/home/pi/klipper","hostname":"qp4","software_version":"v0.12.0-321"}}
        """;

    private const string PrinterInfoPayloadWithoutVersion =
        """
        {"result":{"state_message":"Printer is ready","klipper_path":"/home/pi/klipper","hostname":"qp4"}}
        """;

    [Fact]
    public async Task DetectFirmwareIdentityAsync_PrinterNotFound_ReturnsPrinterNotFound()
    {
        await using AppDbContext db = CreateDbContext();
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);
        // #1656 round 5 (Vasquez): the id used below is never registered, so the existence
        // pre-check (added to stop the lock table from growing for nonexistent ids) must also
        // report it as absent.
        repository
            .Setup(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(Guid.NewGuid(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.PrinterNotFound);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// #1656 / PR #1660 review round 5 (Vasquez, blocking): DetectFirmwareIdentityAsync is
    /// reachable from POST /printers/{id}/firmware/detect with the raw, caller-supplied route id.
    /// Before the fix, a lock-table entry (FirmwareIdentityWriteLocks, a static, never-evicted
    /// ConcurrentDictionary) was allocated for whatever id was passed in *before* confirming the
    /// printer existed, so any authenticated caller could grow that table without bound just by
    /// repeatedly requesting detection for nonexistent GUIDs — an unbounded process-memory sink.
    /// This proves a request for a nonexistent id never even touches the repository's write path
    /// (FindByIdAsync is never called), which is the observable proxy for "no lock entry was
    /// allocated for this id" available from this test's vantage point.
    /// </summary>
    [Fact]
    public async Task DetectFirmwareIdentityAsync_PrinterDoesNotExist_NeverCallsFindByIdAsync()
    {
        await using AppDbContext db = CreateDbContext();
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(Guid.NewGuid(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.PrinterNotFound);
        repository.Verify(
            r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a nonexistent printer id must be rejected by the cheap existence check before any lock-table entry is allocated or the tracked entity is loaded");
    }

    /// <summary>
    /// #1656 / PR #1660 review round 6 (Vasquez, blocking): the round-5 pre-lock existence check
    /// only closes the window before this call acquires the per-printer lock — a concurrent delete
    /// from a different scope/request between that check and lock acquisition (or while waiting on
    /// it) must still be caught, and must not leave the lock-table entry retained forever.
    /// FindByIdAsync alone cannot be trusted to detect this: it is backed by DbSet.FindAsync
    /// (identity-map lookup), which can return an already-tracked, non-null instance without ever
    /// re-querying the database. This simulates the delete-after-exists window with ExistsAsync
    /// answering "yes" pre-lock and "no" post-lock, and asserts the write path never reaches
    /// FindByIdAsync/SaveChangesAsync once the printer is confirmed gone, and that the lock-table
    /// entry allocated for the pre-lock check is evicted rather than leaked.
    /// </summary>
    [Fact]
    public async Task DetectFirmwareIdentityAsync_PrinterDeletedAfterPreLockExistsCheck_DoesNotWriteAndEvictsLock()
    {
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
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printerId, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.PrinterNotFound);
        repository.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        PrintersService.HasFirmwareIdentityWriteLockForTests(printerId).Should().BeFalse(
            "the lock-table entry allocated for the pre-lock existence check must be evicted once the printer is confirmed deleted, not retained forever");
    }

    /// <summary>
    /// #1656 / PR #1660 review round 7 (Bishop + Vasquez, blocking): round 6 only closed the
    /// window up to and including the post-lock existence recheck — a delete landing after that
    /// recheck but before/during the write itself was never caught, since every fix so far was
    /// another check-then-act layer with its own gap after it. The structural fix makes the write
    /// (SaveChangesAsync) itself the atomic existence boundary. This simulates that shape: both
    /// existence rechecks still report the printer present, and only SaveChangesAsync itself
    /// discovers the row is gone via a genuine DbUpdateConcurrencyException wrapping an
    /// EntityEntry whose current database values are null — exactly what
    /// WasFirmwareIdentityPrinterDeletedAsync looks for.
    /// </summary>
    [Fact]
    public async Task DetectFirmwareIdentityAsync_SaveChangesThrowsConcurrencyExceptionForDeletedRow_ReturnsPrinterNotFoundAndEvictsLock()
    {
        string dbName = $"PrintersServiceFirmwareDetectTests_ConcurrencyDeleted_{Guid.NewGuid():N}";
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

        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printerId, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.PrinterNotFound);
        PrintersService.HasFirmwareIdentityWriteLockForTests(printerId).Should().BeFalse(
            "a delete discovered only when the write itself fails must still evict this printer's lock-table entry, not just the pre-write recheck path");

        // #1656 / PR #1660 review round 8 (Bishop, blocking): confirming the delete is not
        // enough on its own -- this scope's `db` must no longer be tracking the deleted `printer`
        // instance, or a subsequent same-scope repository lookup would be satisfied from the
        // identity map with this stale, since-deleted instance instead of genuinely re-querying
        // the database and observing the deletion.
        entry.State.Should().Be(Microsoft.EntityFrameworkCore.EntityState.Detached,
            "the confirmed-deleted printer's tracked entity must be detached, otherwise a same-scope FindAsync re-read after this call would still be satisfied from the identity map with the stale pre-delete instance instead of observing the deletion");
        (await db.Printers.FindAsync(new object?[] { printerId }, CancellationToken.None)).Should().BeNull(
            "with the tracked entity detached, a same-scope FindAsync re-read must genuinely re-query the database and observe the deletion, not silently return the stale identity-mapped instance");
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_NonMoonrakerBackend_ReturnsBackendNotSupported()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.Backend = (int)PrinterBackend.PrusaLink;
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.BackendNotSupported);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Unknown);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ServerUrlNotAbsolute_ReturnsServerUrlInvalid()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.ServerUrl = "moonraker.local";
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.ServerUrlInvalid);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeUnreachable_ReturnsProbeFailed()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, Throwing());

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.ProbeFailed);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Unknown);
        printer.FirmwareDetectedAtUtc.Should().BeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeSucceeds_PersistsIdentityButDoesNotMarkVerified()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().Be(FirmwareDetectionFailure.None);

        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
        printer.FirmwareDetectionSource.Should().Be(FirmwareDetectionSource.Printer);
        printer.FirmwareVersion.Should().Be("v0.12.0-321");
        printer.FirmwareDetectionVersion.Should().Be(MoonrakerOnboardingResolver.FirmwareProbeVersion);
        printer.FirmwareDetectionConfidence.Should().NotBeNull();
        printer.FirmwareDetectedAtUtc.Should().NotBeNull();

        // Detection populates facts; only a human attests them (#1613 AC #3). If this ever flips,
        // the "Mark firmware verified" action becomes a no-op the operator can silently skip.
        printer.FirmwareIdentityVerified.Should().BeFalse();
        result.IdentityVerified.Should().BeFalse();

        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeOmitsSoftwareVersion_PreservesRecordedVersion()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareVersion = "v0.11.0-previously-recorded";
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service =
            CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayloadWithoutVersion));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);

        // A probe that cannot read software_version must not erase a version recorded earlier,
        // because firmware.version is required by calibration context resolution.
        printer.FirmwareVersion.Should().Be("v0.11.0-previously-recorded");
    }

    /// <summary>
    /// Detection is deliberately not subject to the re-probe cadence guard that
    /// <see cref="PrintersService.RefreshDetectedFirmwareIdentityAsync"/> applies: an operator who
    /// just fixed their printer must not be told to wait six hours.
    ///
    /// The refresh assertion is the control. Both halves run the same freshly-detected printer
    /// through the same cadence window, so the contrast is what carries the meaning — without it,
    /// a detect call that updated for some unrelated reason would look identical to one that
    /// correctly bypassed the throttle.
    /// </summary>
    [Fact]
    public async Task DetectFirmwareIdentityAsync_RecentlyDetected_BypassesTheReprobeCadenceGuard()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareVersion = "v0.10.0-stale";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow; // well inside the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        // Control: the throttled refresh path declines the identical printer in the same window.
        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(
            printer.Id,
            new DiscoveredPrinterDto
            {
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                FirmwareVersion = "v0.12.0-321",
            },
            CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareVersion.Should().Be("v0.10.0-stale");

        // Subject: the on-demand path proceeds anyway.
        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        printer.FirmwareVersion.Should().Be("v0.12.0-321");
    }

    private static AppDbContext CreateDbContext(string? name = null)
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(name ?? $"PrintersServiceFirmwareDetectTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Firmware detect test printer",
        ServerUrl = "http://moonraker.local",
        BackendPort = MoonrakerOnboardingResolver.DefaultMoonrakerPort,
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static Mock<IUnitOfWork> CreateUnitOfWork(Printer printer)
    {
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        // #1656 round 5 (Vasquez): DetectFirmwareIdentityAsync now confirms existence before
        // allocating a lock-table entry; this printer id is real.
        repository
            .Setup(r => r.ExistsAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        return unitOfWork;
    }

    private static StubHandler RespondWith(string payload) =>
        new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });

    private static StubHandler Throwing() =>
        new StubHandler(_ => throw new HttpRequestException("connection refused"));

    private static PrintersService CreateService(
        AppDbContext db,
        IUnitOfWork unitOfWork,
        HttpMessageHandler handler)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new PrintersService(
            unitOfWork,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            httpClientFactory.Object,
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
