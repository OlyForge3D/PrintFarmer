using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
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
        IFilamentCoverageBroadcaster? coverageBroadcaster = null)
    {
        var uow = new AppUnitOfWork(db, Mock.Of<ISensitiveDataProtector>());
        return new PrintersService(
            uow,
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
            Mock.Of<ISensitiveDataProtector>(),
            spoolman.Object,
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            coverageBroadcaster);
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
        string? currentMaterial = null)
    {
        using AppDbContext db = NewDb();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Toolhead Manufacturer" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "Toolhead Model", ManufacturerId = manufacturer.Id };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "toolhead",
            ServerUrl = "http://toolhead.local",
            Backend = (int)PrinterBackend.Moonraker,
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
            var validator = new PrinterToolheadSwapValidator(
                db,
                spoolman.Object,
                NullLogger<PrinterToolheadSwapValidator>.Instance);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId, 0, 88, CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Ok);

            PrintersService service = CreateService(db, spoolman);
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
            var validator = new PrinterToolheadSwapValidator(
                db,
                spoolman.Object,
                NullLogger<PrinterToolheadSwapValidator>.Instance);
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
            PrintersService service = CreateService(db, spoolman);
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
            var validator = new PrinterToolheadSwapValidator(
                db,
                spoolman.Object,
                NullLogger<PrinterToolheadSwapValidator>.Instance);
            SwapValidationResult validation = await validator.ValidateAsync(
                printerId, 1, 88, CancellationToken.None);
            validation.Result!.Status.Should().Be(SwapValidationStatus.Ok);

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
}
