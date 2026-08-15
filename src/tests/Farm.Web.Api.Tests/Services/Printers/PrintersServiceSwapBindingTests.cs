using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Production-path tests for <c>PrintersService.SetToolheadSpoolAsync</c> covering the
/// Bishop/Hicks/Vasquez review blocks B3 (legacy single-tool T0 binds via the Printer scalar,
/// never a fabricated MMU gate at index 0) and B6 (a durable <see cref="FilamentSwapOverride"/>
/// audit row is written in the SAME unit of work as the binding, and only for a genuine
/// authorized override). Uses a real <see cref="AppUnitOfWork"/> over a SQLite in-memory
/// database so the audit row actually persists (relational SaveChanges wraps binding + audit in
/// a single transaction) rather than being asserted against a fake.
/// </summary>
public sealed class PrintersServiceSwapBindingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PrintersServiceSwapBindingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext db = new(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewDb() => new(_options);

    private static PrintersService CreateService(
        AppDbContext db,
        Mock<ISpoolmanService> spoolman,
        IFilamentCoverageBroadcaster? coverageBroadcaster = null,
        IFilamentCoverageSpoolResolver? spoolResolver = null,
        IBackendClientFactory? backendClientFactory = null)
    {
        backendClientFactory ??= Mock.Of<IBackendClientFactory>();
        spoolResolver ??= CreateResolver(spoolman);
        var uow = new AppUnitOfWork(db, Mock.Of<ISensitiveDataProtector>());
        return new PrintersService(
            uow,
            db,
            backendClientFactory,
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<ISensitiveDataProtector>(),
            spoolman.Object,
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            spoolResolver,
            coverageBroadcaster);
    }

    private static IFilamentCoverageSpoolResolver CreateResolver(Mock<ISpoolmanService> spoolman)
    {
        var resolver = new Mock<IFilamentCoverageSpoolResolver>();
        resolver.Setup(r => r.ResolveSpoolAsync(
                It.IsAny<Printer>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (Printer _, int spoolId, CancellationToken ct) =>
            {
                SpoolmanSpoolDto? spool = await spoolman.Object.GetSpoolByIdAsync(spoolId, ct);
                return new FilamentCoverageSpoolSnapshot(
                    spool,
                    TracksLiveConsumption: false,
                    spool is null ? FilamentCoverageSpoolResolver.ReasonSpoolNotFound : null);
            });
        return resolver.Object;
    }

    private static Mock<ISpoolmanService> Spoolman(int spoolId, string material)
    {
        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(spoolId, $"spool-{spoolId}", material, 500, "#ABCDEF", true));
        return spoolman;
    }

    private Guid SeedLegacyPrinterNoToolheads()
    {
        using AppDbContext db = NewDb();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "legacy",
            ServerUrl = "http://legacy.local",
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.SaveChanges();
        return printer.Id;
    }

    private Guid SeedMmuPrinterNoToolheads()
    {
        using AppDbContext db = NewDb();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "MMU Manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "MMU Model", ManufacturerId = manufacturer.Id };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "mmu",
            ServerUrl = "http://mmu.local",
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            HasMmu = true,
        };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.SaveChanges();
        return printer.Id;
    }

    private Guid SeedPrinterWithExistingToolhead(
        int? currentSpoolId = null,
        string? currentMaterial = null,
        PrinterBackend backend = PrinterBackend.Moonraker)
    {
        using AppDbContext db = NewDb();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Toolhead Manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Toolhead Model", ManufacturerId = manufacturer.Id };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "toolhead",
            ServerUrl = "http://toolhead.local",
            Backend = (int)backend,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "T0",
            IsPrimary = true,
            CurrentSpoolId = currentSpoolId,
            CurrentMaterial = currentMaterial,
            CurrentFilamentColor = "#111111",
        });
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.SaveChanges();
        return printer.Id;
    }

    private void SeedRelevantJob(Guid printerId, string? requiredMaterial)
    {
        using AppDbContext db = NewDb();
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "relevant-job",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Printing,
            RequiredMaterialType = requiredMaterial,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── B3: legacy single-tool T0 binds via the Printer scalar ──

    [Fact]
    public async Task SetToolheadSpoolAsync_LegacyT0NoRow_BindsViaPrinterScalar_NoGateCreated()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(77, "PLA");

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 77, CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().Be(77);
        saved.CurrentMaterial.Should().Be("PLA");
        // B3: no fabricated MMU gate at index 0 (or anywhere).
        saved.Toolheads.Should().BeEmpty();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    // ── Defence-in-depth: spoolId 0 means "release", never "bind spool 0" ──

    [Fact]
    public async Task SetToolheadSpoolAsync_NonPositiveSpoolId_RejectedWithoutPersisting()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(77, "PLA");

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 0, CancellationToken.None);
            result.Success.Should().BeFalse();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().BeNull("a rejected non-positive spoolId must never persist as a bogus binding");
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    // ── B6: durable override audit written atomically with the binding ──

    [Fact]
    public async Task SetToolheadSpoolAsync_WithOverrideContext_WritesDurableAudit_AtomicallyWithBinding()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(88, "PETG");
        Guid affectedJob = Guid.NewGuid();
        var ctx = new FilamentSwapOverrideContext(
            UserId: "user-42",
            UserName: "op",
            Reason: "operator override",
            ExpectedMaterial: "PLA",
            ScannedMaterial: "PETG",
            AffectedJobIds: new[] { affectedJob });

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 88, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().Be(88);

        FilamentSwapOverride audit = await verify.FilamentSwapOverrides.SingleAsync();
        audit.PrinterId.Should().Be(printerId);
        audit.ToolheadIndex.Should().Be(0);
        audit.SpoolId.Should().Be(88);
        audit.UserId.Should().Be("user-42");
        audit.UserName.Should().Be("op");
        audit.Reason.Should().Be("operator override");
        audit.ExpectedMaterial.Should().Be("PLA");
        audit.ScannedMaterial.Should().Be("PETG");
        audit.CreatedAtUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        JsonSerializer.Deserialize<List<Guid>>(audit.AffectedJobIdsJson)
            .Should().ContainSingle().Which.Should().Be(affectedJob);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_WithoutOverrideContext_WritesNoAudit()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(5, "PLA");

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            await service.SetToolheadSpoolAsync(printerId, 0, 5, overrideAudit: null, SpoolBindPolicy.Guided, CancellationToken.None);
        }

        await using AppDbContext verify = NewDb();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_WhenBindFails_WritesNoAudit()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        // Spoolman lookup throws → the bind fails before staging/committing anything, so the
        // audit row must NOT be written (no success-shaped fallback, atomic rollback).
        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("spoolman down"));

        var ctx = new FilamentSwapOverrideContext("u", "n", "reason", "PLA", "PETG", Array.Empty<Guid>());

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 99, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeFalse();
        }

        await using AppDbContext verify = NewDb();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
        Printer saved = await verify.Printers.FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().BeNull();
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_OutOfRangeIndex_ReturnsFailure_NoAudit()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        var spoolman = new Mock<ISpoolmanService>(MockBehavior.Strict);
        var ctx = new FilamentSwapOverrideContext("u", "n", "reason", "PLA", "PETG", Array.Empty<Guid>());

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 99, 1, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeFalse();
        }

        await using AppDbContext verify = NewDb();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    // ── C1: guided bind fails closed when the commit-time spool re-resolution is null ──

    [Fact]
    public async Task SetToolheadSpoolAsync_GuidedLegacyT0_CommitTimeSpoolNull_PersistsNothing()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = SpoolmanReturningNull();
        var ctx = new FilamentSwapOverrideContext("u", "n", "reason", "PLA", "PETG", Array.Empty<Guid>());

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId, 0, 77, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeFalse();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().BeNull();
        saved.CurrentMaterial.Should().BeNull();
        saved.Toolheads.Should().BeEmpty();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_DirectLegacyT0_CommitTimeSpoolNull_StillAssigns()
    {
        // Converged non-change: the generic/legacy DIRECT control keeps its assign-anyway
        // behavior even when the spool no longer resolves (material simply stays null).
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = SpoolmanReturningNull();

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 77, CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().Be(77);
        saved.CurrentMaterial.Should().BeNull();
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_DirectMoonrakerBinding_RetainsCentralLookupSemantics()
    {
        Guid printerId = SeedPrinterWithExistingToolhead();
        Mock<ISpoolmanService> central = Spoolman(77, "CENTRAL");
        var sourceResolver = new Mock<IFilamentCoverageSpoolResolver>(MockBehavior.Strict);

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(
                db,
                central,
                spoolResolver: sourceResolver.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                77,
                CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentMaterial.Should().Be("CENTRAL");
        sourceResolver.VerifyNoOtherCalls();
        central.Verify(
            s => s.GetSpoolByIdAsync(77, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── C3: gate materialization + bind + audit commit in ONE SaveChanges ──

    [Fact]
    public async Task SetToolheadSpoolAsync_GuidedMmuGate_CommitTimeSpoolNull_NoGate_NoPromotion_NoAudit()
    {
        // A valid unmaterialized MMU gate would promote MultiMaterial and stage gate rows.
        // Under guided mode a null commit-time re-resolution must leave nothing tracked or
        // persisted (C1 + C3).
        Guid printerId = SeedMmuPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = SpoolmanReturningNull();
        var ctx = new FilamentSwapOverrideContext("u", "n", "reason", "PLA", "PETG", Array.Empty<Guid>());

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId, 1, 88, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeFalse();

            // The request-scoped context may be saved again by later work. Failed binding must
            // leave no tracked promotion/gates/audit that such a save could accidentally commit.
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).FirstAsync(p => p.Id == printerId);
        saved.Toolheads.Should().BeEmpty();
        saved.MultiMaterial.Should().BeFalse();
        (await verify.Toolheads.CountAsync()).Should().Be(0);
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_GuidedMmuGate_Success_CommitsGateBindAndAudit_Atomically()
    {
        Guid printerId = SeedMmuPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(88, "PETG");
        Guid affectedJob = Guid.NewGuid();
        var ctx = new FilamentSwapOverrideContext(
            "user-9", "op", "override", "PLA", "PETG", new[] { affectedJob });

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId, 1, 88, ctx, SpoolBindPolicy.Guided, CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).FirstAsync(p => p.Id == printerId);
        saved.MultiMaterial.Should().BeTrue();

        Toolhead gate = saved.Toolheads.Single(t => t.Index == 1);
        gate.CurrentSpoolId.Should().Be(88);
        gate.CurrentMaterial.Should().Be("PETG");

        FilamentSwapOverride audit = await verify.FilamentSwapOverrides.SingleAsync();
        audit.ToolheadIndex.Should().Be(1);
        audit.SpoolId.Should().Be(88);
        audit.UserId.Should().Be("user-9");
    }

    [Fact]
    public async Task GuidedLegacyT0_ValidatorOkThenCommitLookupNull_PersistsNothing()
    {
        Guid printerId = SeedLegacyPrinterNoToolheads();
        SeedRelevantJob(printerId, "PLA");
        Mock<ISpoolmanService> spoolman = SpoolmanSequence(88, "PLA", second: null);

        await using (AppDbContext db = NewDb())
        {
            IFilamentCoverageSpoolResolver resolver = CreateResolver(spoolman);
            var validator = new PrinterToolheadSwapValidator(
                db,
                resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId, 0, 88, CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Ok);

            PrintersService service = CreateService(db, spoolman, spoolResolver: resolver);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                overrideAudit: null,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        spoolman.Verify(
            s => s.GetSpoolByIdAsync(88, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).SingleAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().BeNull();
        saved.CurrentMaterial.Should().BeNull();
        saved.Toolheads.Should().BeEmpty();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GuidedExistingToolhead_ValidatorMismatchThenCommitLookupNull_PreservesBindingAndNoAudit()
    {
        Guid printerId = SeedPrinterWithExistingToolhead(currentSpoolId: 12, currentMaterial: "ABS");
        SeedRelevantJob(printerId, "PLA");
        Mock<ISpoolmanService> spoolman = SpoolmanSequence(88, "PETG", second: null);

        await using (AppDbContext db = NewDb())
        {
            IFilamentCoverageSpoolResolver resolver = CreateResolver(spoolman);
            var validator = new PrinterToolheadSwapValidator(
                db,
                resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId, 0, 88, CancellationToken.None);
            SwapValidationResultDto body = validation.Result!;
            body.Status.Should().Be(SwapValidationStatus.Mismatch);

            var audit = new FilamentSwapOverrideContext(
                "user-1",
                "operator",
                "approved mismatch",
                body.Expected,
                body.Scanned,
                body.AffectedJobs.Select(j => j.JobId).ToArray());
            PrintersService service = CreateService(db, spoolman, spoolResolver: resolver);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                audit,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        spoolman.Verify(
            s => s.GetSpoolByIdAsync(88, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(12);
        saved.CurrentMaterial.Should().Be("ABS");
        saved.CurrentFilamentColor.Should().Be("#111111");
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GuidedUnmaterializedMmuGate_ValidatorOkThenCommitLookupNull_NoTopologyPersists()
    {
        Guid printerId = SeedMmuPrinterNoToolheads();
        SeedRelevantJob(printerId, "PLA");
        Mock<ISpoolmanService> spoolman = SpoolmanSequence(88, "PLA", second: null);

        await using (AppDbContext db = NewDb())
        {
            IFilamentCoverageSpoolResolver resolver = CreateResolver(spoolman);
            var validator = new PrinterToolheadSwapValidator(
                db,
                resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId, 1, 88, CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Ok);

            PrintersService service = CreateService(db, spoolman, spoolResolver: resolver);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                1,
                88,
                overrideAudit: null,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        spoolman.Verify(
            s => s.GetSpoolByIdAsync(88, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).SingleAsync(p => p.Id == printerId);
        saved.MultiMaterial.Should().BeFalse();
        saved.Toolheads.Should().BeEmpty();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GuidedUnmaterializedMmuGate_CommitLookupThrows_NoTrackedTopologyCanPersist()
    {
        Guid printerId = SeedMmuPrinterNoToolheads();
        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(88, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("spoolman unavailable"));

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                1,
                88,
                overrideAudit: null,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).SingleAsync(p => p.Id == printerId);
        saved.MultiMaterial.Should().BeFalse();
        saved.Toolheads.Should().BeEmpty();
    }

    [Fact]
    public async Task GuidedUnmaterializedMmuGate_FinalSaveFails_NoTrackedChangesCanPersistLater()
    {
        Guid printerId = SeedMmuPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(88, "PETG");
        var interceptor = new ThrowOnceSaveChangesInterceptor();
        DbContextOptions<AppDbContext> failingOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        var audit = new FilamentSwapOverrideContext(
            "user-1",
            "operator",
            "approved mismatch",
            "PLA",
            "PETG",
            Array.Empty<Guid>());

        await using (var db = new AppDbContext(failingOptions))
        {
            PrintersService service = CreateService(db, spoolman);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                1,
                88,
                audit,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();

            // The interceptor throws only once. A later request-scope save must have nothing
            // from the failed bind left to commit.
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).SingleAsync(p => p.Id == printerId);
        saved.MultiMaterial.Should().BeFalse();
        saved.Toolheads.Should().BeEmpty();
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void ToolheadModel_HasUniqueCanonicalPrinterIndex()
    {
        using AppDbContext db = NewDb();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType toolheadEntity = db.Model
            .FindEntityType(typeof(Toolhead))!;
        Microsoft.EntityFrameworkCore.Metadata.IIndex index = toolheadEntity
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(Toolhead.PrinterId), nameof(Toolhead.Index) }));

        index.IsUnique.Should().BeTrue();
        index.GetDatabaseName().Should().Be("UX_Toolheads_PrinterId_Index");

        // Referencing FKs are allowed only for the F6 (issue #711) additions. All use Restrict
        // to avoid SQL Server multiple-cascade-path violations. Physical toolheads have no
        // standalone deletion path, and MMU gates are not eligible for maintenance scope.
        HashSet<string> allowedReferencingTypes =
        [
            nameof(Farm.Infrastructure.Domain.FilamentFallbackGroupMember),
            nameof(Farm.Infrastructure.Domain.MaintenanceAlert),
            nameof(Farm.Infrastructure.Domain.MaintenanceLog),
            nameof(Farm.Infrastructure.Domain.PrinterMaintenanceSchedule),
        ];
        List<Microsoft.EntityFrameworkCore.Metadata.IForeignKey> referencingForeignKeys = toolheadEntity
            .GetReferencingForeignKeys()
            .ToList();
        IEnumerable<string> actualReferencingTypes = referencingForeignKeys
            .Select(fk => fk.DeclaringEntityType.ClrType.Name);
        actualReferencingTypes.Should().BeSubsetOf(allowedReferencingTypes);
        referencingForeignKeys.Should().OnlyContain(fk => fk.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public async Task GuidedConcurrentFirstGateBinds_OneSucceeds_OneConflicts_AndTopologyRemainsCanonical()
    {
        string databasePath = Path.Join(
            AppContext.BaseDirectory,
            $"toolhead-race-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={databasePath};Default Timeout=30";

        try
        {
            await using var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            await using (SqliteCommand command = anchor.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
                _ = await command.ExecuteNonQueryAsync();
            }

            DbContextOptions<AppDbContext> seedOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;
            Guid printerId;
            await using (var seed = new AppDbContext(seedOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Race Manufacturer" };
                var model = new PrinterModel
                {
                    Id = Guid.NewGuid(),
                    Name = "Race Model",
                    ManufacturerId = manufacturer.Id,
                };
                var printer = new Printer
                {
                    Id = Guid.NewGuid(),
                    Name = "race-printer",
                    ServerUrl = "http://race.local",
                    Backend = (int)PrinterBackend.Moonraker,
                    ManufacturerId = manufacturer.Id,
                    ModelId = model.Id,
                    HasMmu = true,
                };
                printerId = printer.Id;
                seed.AddRange(manufacturer, model, printer);
                await seed.SaveChangesAsync();
            }

            using var barrier = new Barrier(2);
            Mock<ISpoolmanService> central = new(MockBehavior.Strict);
            var resolver = new Mock<IFilamentCoverageSpoolResolver>();
            resolver.Setup(r => r.ResolveSpoolAsync(
                    It.IsAny<Printer>(),
                    88,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                    new SpoolmanSpoolDto(88, "race", "PETG", 500, "#ABCDEF", true),
                    TracksLiveConsumption: true,
                    ErrorReason: null));

            async Task<CommandResult> BindAsync(string userId)
            {
                var interceptor = new CoordinatedFirstSaveInterceptor(barrier);
                DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connectionString)
                    .AddInterceptors(interceptor)
                    .Options;
                await using var db = new AppDbContext(options);
                PrintersService service = CreateService(
                    db,
                    central,
                    spoolResolver: resolver.Object);
                var audit = new FilamentSwapOverrideContext(
                    userId,
                    userId,
                    "concurrent override",
                    "PLA",
                    "PETG",
                    Array.Empty<Guid>());
                CommandResult result = await service.SetToolheadSpoolAsync(
                    printerId,
                    1,
                    88,
                    audit,
                    SpoolBindPolicy.Guided,
                    CancellationToken.None);
                return result;
            }

            Task<CommandResult> first = Task.Run(() => BindAsync("user-a"));
            Task<CommandResult> second = Task.Run(() => BindAsync("user-b"));
            CommandResult[] results = await Task.WhenAll(first, second);

            results.Count(r => r.Success).Should().Be(1);
            ToolheadSpoolBindResult conflict = results
                .OfType<ToolheadSpoolBindResult>()
                .Single(r => r.FailureKind == ToolheadSpoolBindFailureKind.TopologyConflict);
            conflict.Success.Should().BeFalse();

            await using var verify = new AppDbContext(seedOptions);
            Printer saved = await verify.Printers
                .Include(p => p.Toolheads)
                .SingleAsync(p => p.Id == printerId);
            saved.MultiMaterial.Should().BeTrue();
            saved.Toolheads.Should().HaveCount(4);
            saved.Toolheads.GroupBy(t => t.Index).Should().OnlyContain(g => g.Count() == 1);
            Toolhead gate = saved.Toolheads.Single(t => t.Index == 1);
            gate.CurrentSpoolId.Should().Be(88);
            gate.CurrentMaterial.Should().Be("PETG");
            (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }

            if (File.Exists($"{databasePath}-wal"))
            {
                File.Delete($"{databasePath}-wal");
            }

            if (File.Exists($"{databasePath}-shm"))
            {
                File.Delete($"{databasePath}-shm");
            }
        }
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_CoverageBroadcastFailsAfterCommit_ReturnsSuccess()
    {
        Guid printerId = SeedPrinterWithExistingToolhead();
        Mock<ISpoolmanService> spoolman = Spoolman(88, "PLA");
        var broadcaster = new Mock<IFilamentCoverageBroadcaster>();
        broadcaster.Setup(b => b.BroadcastPrinterChangedAsync(
                printerId,
                FilamentCoverageChangeReasons.SpoolBinding,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("signalr unavailable"));

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman, broadcaster.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                overrideAudit: null,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(88);
        saved.CurrentMaterial.Should().Be("PLA");
    }

    [Fact]
    public async Task GuidedNativePrinter_DuplicateCentralId_ValidatesBindsAndAuditsNativeMaterial()
    {
        Guid printerId = SeedPrinterWithExistingToolhead();
        SeedRelevantJob(printerId, "PLA");
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .SetupSequence(n => n.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(
                new[] { new { id = 88, material = "PETG", remaining_weight = 400 } }))
            .ReturnsAsync(JsonSerializer.Serialize(
                new[] { new { id = 88, material = "ABS", remaining_weight = 400 } }));
        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        var resolver = new FilamentCoverageSpoolResolver(
            central.Object,
            backendFactory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);

        await using (AppDbContext db = NewDb())
        {
            var validator = new PrinterToolheadSwapValidator(db, resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId,
                0,
                88,
                CancellationToken.None);
            SwapValidationResultDto body = validation.Result!;
            body.Status.Should().Be(SwapValidationStatus.Mismatch);
            body.Expected.Should().Be("PLA");
            body.Scanned.Should().Be("PETG");

            var audit = new FilamentSwapOverrideContext(
                "native-user",
                "operator",
                "native material override",
                body.Expected,
                body.Scanned,
                body.AffectedJobs.Select(j => j.JobId).ToArray());
            PrintersService service = CreateService(
                db,
                central,
                spoolResolver: resolver,
                backendClientFactory: backendFactory.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                audit,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(88);
        saved.CurrentMaterial.Should().Be("ABS");
        FilamentSwapOverride persistedAudit = await verify.FilamentSwapOverrides.SingleAsync();
        persistedAudit.ExpectedMaterial.Should().Be("PLA");
        persistedAudit.ScannedMaterial.Should().Be("ABS");
        native.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        central.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GuidedManagedPrinter_DuplicateNativeId_UsesCentralMaterialForValidationAndBinding()
    {
        Guid printerId = SeedPrinterWithExistingToolhead(backend: PrinterBackend.OctoPrint);
        SeedRelevantJob(printerId, "PLA");
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central.Setup(s => s.ListSpoolsAsync(
                It.IsAny<SpoolmanSpoolQueryParams>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>(
                [new SpoolmanSpoolDto(88, "central", "PLA", 400, "#FFFFFF", true)],
                1));
        Mock<IBackendClientFactory> backendFactory = new(MockBehavior.Strict);
        var resolver = new FilamentCoverageSpoolResolver(
            central.Object,
            backendFactory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);

        await using (AppDbContext db = NewDb())
        {
            var validator = new PrinterToolheadSwapValidator(db, resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId,
                0,
                88,
                CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Ok);
            validation.Result.Scanned.Should().Be("PLA");

            PrintersService service = CreateService(
                db,
                central,
                spoolResolver: resolver,
                backendClientFactory: backendFactory.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                overrideAudit: null,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeTrue();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentMaterial.Should().Be("PLA");
        central.Verify(
            s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        central.Verify(
            s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        backendFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GuidedNativePrinter_CommitResolutionMissing_DoesNotSwitchToCentralOrWriteAudit()
    {
        Guid printerId = SeedPrinterWithExistingToolhead(currentSpoolId: 12, currentMaterial: "ABS");
        SeedRelevantJob(printerId, "PLA");
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .SetupSequence(n => n.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonSerializer.Serialize(new[] { new { id = 88, material = "PETG" } }))
            .ReturnsAsync("[]");
        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        var resolver = new FilamentCoverageSpoolResolver(
            central.Object,
            backendFactory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);

        await using (AppDbContext db = NewDb())
        {
            var validator = new PrinterToolheadSwapValidator(db, resolver);
            SwapValidationResultDto body = (await validator.ValidateAsync(
                printerId,
                0,
                88,
                CancellationToken.None)).Result!;
            body.Status.Should().Be(SwapValidationStatus.Mismatch);
            body.Scanned.Should().Be("PETG");

            var audit = new FilamentSwapOverrideContext(
                "native-user",
                "operator",
                "override",
                body.Expected,
                body.Scanned,
                body.AffectedJobs.Select(j => j.JobId).ToArray());
            PrintersService service = CreateService(
                db,
                central,
                spoolResolver: resolver,
                backendClientFactory: backendFactory.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                audit,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(12);
        saved.CurrentMaterial.Should().Be("ABS");
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
        central.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GuidedNativePrinter_SourceUnavailable_IsUnknownAndCannotBindOrAudit()
    {
        Guid printerId = SeedPrinterWithExistingToolhead(currentSpoolId: 12, currentMaterial: "ABS");
        SeedRelevantJob(printerId, "PLA");
        Mock<IBackendClient> native = NativeClient(null);
        Mock<IBackendClientFactory> backendFactory = new();
        backendFactory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        var resolver = new FilamentCoverageSpoolResolver(
            central.Object,
            backendFactory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);

        await using (AppDbContext db = NewDb())
        {
            var validator = new PrinterToolheadSwapValidator(db, resolver);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId,
                0,
                88,
                CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Unknown);

            var audit = new FilamentSwapOverrideContext(
                "native-user",
                "operator",
                "must not persist",
                "PLA",
                "PETG",
                Array.Empty<Guid>());
            PrintersService service = CreateService(
                db,
                central,
                spoolResolver: resolver,
                backendClientFactory: backendFactory.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(
                printerId,
                0,
                88,
                audit,
                SpoolBindPolicy.Guided,
                CancellationToken.None);
            result.Success.Should().BeFalse();
            await db.SaveChangesAsync();
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(12);
        saved.CurrentMaterial.Should().Be("ABS");
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0);
        central.VerifyNoOtherCalls();
    }

    // ── H-3: spool-bind natural idempotency backstop (no-op re-bind) ──

    [Fact]
    public async Task SetToolheadSpoolAsync_RebindSameSpoolToMaterializedToolhead_IsIdempotentNoOp()
    {
        // Hicks H-3: re-binding the SAME spool to the SAME materialized (printer, toolhead)
        // slot must be a no-op — no coverage broadcast, no UpdatedAt churn, no audit — so a
        // replayed bind (Idempotency-Key retry while the replay flag is off/transitioning,
        // or a staleness-reclaimed retry) cannot produce duplicate state. This is
        // spool-bind's natural idempotency backstop, mirroring adjust and harvest.
        Guid printerId = SeedPrinterWithExistingToolhead(currentSpoolId: 77, currentMaterial: "PLA");

        DateTime seededUpdatedAt;
        await using (AppDbContext pre = NewDb())
        {
            Toolhead seeded = await pre.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
            seededUpdatedAt = seeded.UpdatedAt;
        }

        Mock<ISpoolmanService> spoolman = Spoolman(77, "PLA");
        var broadcaster = new Mock<IFilamentCoverageBroadcaster>();

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman, broadcaster.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 77, CancellationToken.None);
            result.Success.Should().BeTrue("re-binding the same spool to the same slot is a successful no-op");
        }

        await using AppDbContext verify = NewDb();
        Toolhead saved = await verify.Toolheads.SingleAsync(t => t.PrinterId == printerId && t.Index == 0);
        saved.CurrentSpoolId.Should().Be(77);
        saved.CurrentMaterial.Should().Be("PLA");
        saved.UpdatedAt.Should().Be(seededUpdatedAt, "a no-op re-bind must not churn UpdatedAt");
        (await verify.FilamentSwapOverrides.CountAsync()).Should().Be(0, "a no-op re-bind must not write an audit row");
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a no-op re-bind must not broadcast a coverage change");
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_RebindSameSpoolToLegacyT0_IsIdempotentNoOp()
    {
        // Hicks H-3: the legacy single-tool T0 path (Printer.CurrentSpoolId scalar) gets the
        // same natural idempotency backstop — a re-bind of the already-bound spool is a no-op
        // that neither fabricates a toolhead row nor broadcasts a coverage change.
        Guid printerId = SeedLegacyPrinterNoToolheads();
        Mock<ISpoolmanService> spoolman = Spoolman(77, "PLA");

        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman);
            (await service.SetToolheadSpoolAsync(printerId, 0, 77, CancellationToken.None)).Success.Should().BeTrue();
        }

        var broadcaster = new Mock<IFilamentCoverageBroadcaster>();
        await using (AppDbContext db = NewDb())
        {
            PrintersService service = CreateService(db, spoolman, broadcaster.Object);
            CommandResult result = await service.SetToolheadSpoolAsync(printerId, 0, 77, CancellationToken.None);
            result.Success.Should().BeTrue("re-binding the same spool via the legacy T0 scalar is a successful no-op");
        }

        await using AppDbContext verify = NewDb();
        Printer saved = await verify.Printers.Include(p => p.Toolheads).FirstAsync(p => p.Id == printerId);
        saved.CurrentSpoolId.Should().Be(77);
        saved.Toolheads.Should().BeEmpty("a no-op re-bind must not fabricate a toolhead row");
        broadcaster.Verify(
            b => b.BroadcastPrinterChangedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a no-op re-bind must not broadcast a coverage change");
    }

    private static Mock<ISpoolmanService> SpoolmanReturningNull()
    {
        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanSpoolDto?)null);
        return spoolman;
    }

    private static Mock<ISpoolmanService> SpoolmanSequence(
        int spoolId,
        string firstMaterial,
        SpoolmanSpoolDto? second)
    {
        var spoolman = new Mock<ISpoolmanService>();
        spoolman.SetupSequence(s => s.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(
                spoolId,
                $"spool-{spoolId}",
                firstMaterial,
                500,
                "#ABCDEF",
                true))
            .ReturnsAsync(second);
        return spoolman;
    }

    private static Mock<IBackendClient> NativeClient(string? json)
    {
        Mock<IBackendClient> client = new();
        client.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
        return client;
    }

    private sealed class ThrowOnceSaveChangesInterceptor : SaveChangesInterceptor
    {
        private bool _shouldThrow = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_shouldThrow)
            {
                _shouldThrow = false;
                throw new InvalidOperationException("simulated relational save failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CoordinatedFirstSaveInterceptor(Barrier barrier) : SaveChangesInterceptor
    {
        private int _coordinated;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _coordinated, 1) == 0)
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken))
                {
                    throw new TimeoutException("Concurrent gate-save barrier timed out.");
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
