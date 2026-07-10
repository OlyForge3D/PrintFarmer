using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Focused unit tests for the spool coverage and runout prediction service
/// (issue #709). Exercises the coverage math through a real in-memory
/// <see cref="AppDbContext"/> so we cover the actual EF query shape and the
/// classification logic together.
/// </summary>
public class FilamentCoverageServiceTests
{
    private static (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, Mock<IPrintersService> printers)
        BuildService(
            SpoolCoverageSettings? settings = null,
            double? liveProgress = null)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();

        Mock<ISpoolmanService> spoolMock = new(MockBehavior.Loose);
        Mock<IPrintersService> printerMock = new(MockBehavior.Loose);
        printerMock
            .Setup(p => p.GetPrintJobStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveProgress.HasValue
                ? new PrintJobStatusDto { Progress = liveProgress }
                : null);

        Mock<ISettingsService> settingsMock = new(MockBehavior.Loose);
        settingsMock.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(settings ?? new SpoolCoverageSettings());

        FilamentCoverageService svc = new(
            db,
            spoolMock.Object,
            printerMock.Object,
            settingsMock.Object,
            NullLogger<FilamentCoverageService>.Instance);

        return (svc, db, spoolMock, printerMock);
    }

    private static Printer SeedPrinter(AppDbContext db, string name, params Toolhead[] toolheads)
    {
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = "http://p.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker
        };
        db.Printers.Add(printer);
        foreach (Toolhead t in toolheads)
        {
            t.PrinterId = printer.Id;
            db.Toolheads.Add(t);
        }

        _ = db.SaveChanges();
        return printer;
    }

    private static Toolhead T(int index, int? spoolId, bool primary = false, string? material = null, string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        Index = index,
        Name = name ?? $"Extruder {index + 1}",
        IsPrimary = primary,
        CurrentSpoolId = spoolId,
        CurrentMaterial = material
    };

    private static GcodeFile Gcode(
        double? estimatedTotalGrams = null,
        double[]? perExtruder = null,
        int? extruderCount = null,
        int? totalLayers = null,
        double? printTimeMinutes = null) => new()
        {
            Id = Guid.NewGuid(),
            FileName = "part.gcode",
            EstimatedFilamentWeightG = estimatedTotalGrams,
            FilamentPerExtruderWeightG = perExtruder is not null ? JsonSerializer.Serialize(perExtruder) : null,
            ExtruderCount = extruderCount,
            TotalLayers = totalLayers,
            EstimatedPrintTimeMinutes = printTimeMinutes
        };

    private static PrintJob Job(
        Guid printerId,
        PrintJobStatus status,
        GcodeFile file,
        TimeSpan? estimatedTime = null,
        string name = "job")
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = printerId,
            Status = status,
            GcodeFileId = file.Id,
            GcodeFile = file,
            EstimatedPrintTime = estimatedTime
        };
        return job;
    }

    private static SpoolmanSpoolDto Spool(int id, double remainingG, double? initialG = null, string material = "PLA") =>
        new(id, $"Spool {id}", material, remainingG, "#FFFFFF", InUse: true, InitialWeightG: initialG ?? 1000);

    // ------------------------------------------------------------------
    // Single-toolhead coverage
    // ------------------------------------------------------------------
    [Fact]
    public async Task SingleTool_SpoolCovers_ActivePlusAssignedQueue_ReportsCovers()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 25.0);

        Printer p = SeedPrinter(db, "p1", T(0, spoolId: 42, primary: true, material: "PLA"));

        GcodeFile active = Gcode(estimatedTotalGrams: 100, totalLayers: 200, printTimeMinutes: 60);
        GcodeFile queued = Gcode(estimatedTotalGrams: 40);
        db.GcodeFiles.AddRange(active, queued);

        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, active, TimeSpan.FromMinutes(60), name: "active"));
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queued, name: "q1"));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Spool(42, remainingG: 500));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov.Should().NotBeNull();
        cov!.Status.Should().Be(FilamentCoverageStatus.Covers);
        cov.ActiveJobProgress.Should().Be(25.0);
        cov.AssignedQueuedJobCount.Should().Be(1);
        cov.EarliestPredictedRunoutAt.Should().BeNull();

        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.RemainingGrams.Should().Be(500);
        slot.CurrentJobRequiredGrams.Should().Be(100);
        slot.CurrentJobRemainingGrams.Should().BeApproximately(75, 0.01, "25% progress leaves 75g of demand");
        slot.QueuedRequiredGrams.Should().Be(40);
        slot.TotalDemandGrams.Should().BeApproximately(115, 0.01);
        slot.PredictedRunoutAt.Should().BeNull();
        slot.StatusReason.Should().BeNull();
    }

    [Fact]
    public async Task SingleTool_Insufficient_PredictsRunoutTimeAndLayer()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 50.0);

        Printer p = SeedPrinter(db, "p1", T(0, spoolId: 42, primary: true, material: "PLA"));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, totalLayers: 400, printTimeMinutes: 120);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(120)));
        _ = await db.SaveChangesAsync();

        // 50% done → 100g of demand left, but only 40g remains.
        spool.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Spool(42, remainingG: 40));

        DateTime before = DateTime.UtcNow;
        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov!.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        slot.StatusReason.Should().Be("insufficient-remaining");
        slot.PredictedRunoutAt.Should().NotBeNull();
        // 40g at 200g / 120min rate = ~24 min from now.
        slot.PredictedRunoutAt!.Value.Should().BeCloseTo(before.AddMinutes(24), TimeSpan.FromSeconds(30));
        // Consumed 50% (layer 200) + 40/200 remaining = 20% → layer 280.
        slot.PredictedRunoutLayer.Should().Be(280);
        cov.EarliestPredictedRunoutAt.Should().Be(slot.PredictedRunoutAt);
    }

    // ------------------------------------------------------------------
    // Multi-toolhead
    // ------------------------------------------------------------------
    [Fact]
    public async Task MultiTool_PerExtruderMetadata_AllocatesCorrectly()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(
            db,
            "dual",
            T(0, spoolId: 100, primary: true, material: "PLA"),
            T(1, spoolId: 200, material: "PETG"));

        GcodeFile g = Gcode(estimatedTotalGrams: 100, perExtruder: [70, 30], extruderCount: 2, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();

        // T0 covers, T1 does not (only 10g left of 30g needed).
        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 500));
        spool.Setup(s => s.GetSpoolByIdAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(200, remainingG: 10));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        cov.Toolheads.Should().HaveCount(2);

        ToolheadCoverageDto t0 = cov.Toolheads.Single(s => s.ToolheadIndex == 0);
        t0.CurrentJobRequiredGrams.Should().Be(70);
        t0.Status.Should().Be(FilamentCoverageStatus.Covers);

        ToolheadCoverageDto t1 = cov.Toolheads.Single(s => s.ToolheadIndex == 1);
        t1.CurrentJobRequiredGrams.Should().Be(30);
        t1.RemainingGrams.Should().Be(10);
        t1.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        t1.PredictedRunoutAt.Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // Unknown metadata never becomes a false runout
    // ------------------------------------------------------------------
    [Fact]
    public async Task MultiTool_MissingPerExtruderMetadata_ReturnsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService();

        Printer p = SeedPrinter(db, "dual", T(0, spoolId: 100, primary: true), T(1, spoolId: 200));
        // extruderCount says multi-tool but per-extruder breakdown is absent.
        GcodeFile g = Gcode(estimatedTotalGrams: 90, extruderCount: 2);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 5));
        spool.Setup(s => s.GetSpoolByIdAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(200, remainingG: 5));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Should().OnlyContain(t => t.Status == FilamentCoverageStatus.Unknown);
        cov.Toolheads.Should().OnlyContain(t => t.StatusReason == "no-per-extruder-metadata");
        cov.EarliestPredictedRunoutAt.Should().BeNull("unknown metadata must never surface a runout");
    }

    [Fact]
    public async Task NoGcodeUsageMetadata_SingleTool_ReturnsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) = BuildService();
        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode();
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 500));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Single().StatusReason.Should().Be("no-gcode-metadata");
    }

    [Fact]
    public async Task SpoolRemainingUnknown_ReportsUnknown_NotRunout()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 25.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 5, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 100, printTimeMinutes: 30);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        // Spoolman returns a spool with null RemainingWeightG.
        spool.Setup(s => s.GetSpoolByIdAsync(5, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new SpoolmanSpoolDto(5, "unknown", "PLA", null, "#FFF", true));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Toolheads.Single().Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Single().StatusReason.Should().Be("spool-remaining-unknown");
        cov.EarliestPredictedRunoutAt.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Assigned queue is honored; unassigned shared-queue is NOT
    // ------------------------------------------------------------------
    [Fact]
    public async Task UnassignedSharedQueueJobs_AreExcluded_FromPrinterDemand()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 40);
        db.GcodeFiles.Add(g);

        // Unassigned queue job (no AssignedPrinterId) — must NOT be charged to this printer.
        PrintJob unassigned = Job(p.Id, PrintJobStatus.Queued, g);
        unassigned.AssignedPrinterId = null;
        db.PrintJobs.Add(unassigned);
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 10));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.AssignedQueuedJobCount.Should().Be(0);
        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.QueuedRequiredGrams.Should().Be(0, "shared unassigned jobs must not be charged to any specific printer");
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task AssignedQueueJob_UnknownMetadata_TaintsQueuePortion()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile active = Gcode(estimatedTotalGrams: 20, printTimeMinutes: 10);
        GcodeFile queuedUnknown = Gcode(); // no usage metadata at all
        db.GcodeFiles.AddRange(active, queuedUnknown);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, active, TimeSpan.FromMinutes(10)));
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queuedUnknown));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 500));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Single().StatusReason.Should().Be("queued-job-metadata-unknown");
        cov.Toolheads.Single().QueuedRequiredGrams.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Progress proration
    // ------------------------------------------------------------------
    [Fact]
    public async Task ActiveJobProgress_PararesRemainingGrams()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 75.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 55));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        ToolheadCoverageDto slot = cov!.Toolheads.Single();
        // 25% remains × 200g = 50g demand. 55g remaining → covers.
        slot.CurrentJobRequiredGrams.Should().Be(200);
        slot.CurrentJobRemainingGrams.Should().BeApproximately(50, 0.01);
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task LiveProgressUnavailable_FallsBackToFullDemand()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: null);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 100, printTimeMinutes: 30);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 60));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.ActiveJobProgress.Should().BeNull();
        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.CurrentJobRemainingGrams.Should().Be(100, "with progress unknown, we conservatively assume the full job remains");
        slot.Status.Should().Be(FilamentCoverageStatus.Insufficient);
    }

    // ------------------------------------------------------------------
    // Configurable prediction threshold
    // ------------------------------------------------------------------
    [Fact]
    public async Task ReserveGrams_TighensCoverage()
    {
        SpoolCoverageSettings s = new() { ReserveGrams = 20 };
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(settings: s, liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 100, printTimeMinutes: 30);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        // 110g remaining would cover the job without a reserve, but reserve of
        // 20g holds back → usable 90g < demand 100g → insufficient.
        spool.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 110));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Insufficient);
    }

    [Fact]
    public async Task RunoutWarningLeadMinutes_ControlsAttentionEmission()
    {
        SpoolCoverageSettings tight = new() { RunoutWarningLeadMinutes = 5, Enabled = true };
        (FilamentCoverageService svc1, AppDbContext db1, Mock<ISpoolmanService> spool1, _) =
            BuildService(settings: tight, liveProgress: 0.0);
        Printer p1 = SeedPrinter(db1, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g1 = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 120);
        db1.GcodeFiles.Add(g1);
        db1.PrintJobs.Add(Job(p1.Id, PrintJobStatus.Printing, g1, TimeSpan.FromMinutes(120)));
        _ = await db1.SaveChangesAsync();
        spool1.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 40));

        // Runout ≈ 24 minutes out. RunoutWarningLeadMinutes=5 → no warning.
        IReadOnlyList<FilamentRunoutWarningDto> tightWarnings = await svc1.GetRunoutWarningsAsync(CancellationToken.None);
        tightWarnings.Where(w => w.Reason == "runout-during-active-job").Should().BeEmpty(
            "predicted runout is 24 min out but lead is 5 min");

        SpoolCoverageSettings wide = new() { RunoutWarningLeadMinutes = 60, Enabled = true };
        (FilamentCoverageService svc2, AppDbContext db2, Mock<ISpoolmanService> spool2, _) =
            BuildService(settings: wide, liveProgress: 0.0);
        Printer p2 = SeedPrinter(db2, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g2 = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 120);
        db2.GcodeFiles.Add(g2);
        db2.PrintJobs.Add(Job(p2.Id, PrintJobStatus.Printing, g2, TimeSpan.FromMinutes(120)));
        _ = await db2.SaveChangesAsync();
        spool2.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 40));

        IReadOnlyList<FilamentRunoutWarningDto> wideWarnings = await svc2.GetRunoutWarningsAsync(CancellationToken.None);
        wideWarnings.Should().ContainSingle(w => w.Reason == "runout-during-active-job");
    }

    [Fact]
    public async Task QueuedShortageWarnings_Disabled_SuppressesEtaLessInsufficientOnly()
    {
        // Two independent scenarios so we can prove the toggle is orthogonal
        // to active-job ETA warnings.
        SpoolCoverageSettings disabled = new()
        {
            Enabled = true,
            QueuedShortageWarningsEnabled = false,
            RunoutWarningLeadMinutes = 60
        };

        // Scenario A: ETA-less queue shortage → suppressed.
        (FilamentCoverageService svcQ, AppDbContext dbQ, Mock<ISpoolmanService> spoolQ, _) =
            BuildService(settings: disabled);
        Printer pQ = SeedPrinter(dbQ, "queue", T(0, spoolId: 1, primary: true));
        GcodeFile queued = Gcode(estimatedTotalGrams: 500);
        dbQ.GcodeFiles.Add(queued);
        dbQ.PrintJobs.Add(Job(pQ.Id, PrintJobStatus.Assigned, queued));
        _ = await dbQ.SaveChangesAsync();
        spoolQ.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 100));

        IReadOnlyList<FilamentRunoutWarningDto> qWarnings = await svcQ.GetRunoutWarningsAsync(CancellationToken.None);
        qWarnings.Should().BeEmpty("queued-shortage warnings must be suppressed when the toggle is off");

        // Scenario B: active-job ETA runout inside the lead window still fires.
        (FilamentCoverageService svcA, AppDbContext dbA, Mock<ISpoolmanService> spoolA, _) =
            BuildService(settings: disabled, liveProgress: 0.0);
        Printer pA = SeedPrinter(dbA, "active", T(0, spoolId: 1, primary: true));
        GcodeFile active = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 120);
        dbA.GcodeFiles.Add(active);
        dbA.PrintJobs.Add(Job(pA.Id, PrintJobStatus.Printing, active, TimeSpan.FromMinutes(120)));
        _ = await dbA.SaveChangesAsync();
        spoolA.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 40));

        IReadOnlyList<FilamentRunoutWarningDto> aWarnings = await svcA.GetRunoutWarningsAsync(CancellationToken.None);
        aWarnings.Should().ContainSingle(w => w.Reason == "runout-during-active-job",
            "the disabled toggle must not suppress active-job ETA warnings");
    }

    [Fact]
    public void RunoutWarningLeadMinutes_BelowFloor_FailsValidation()
    {
        SpoolCoverageSettings bad = new() { RunoutWarningLeadMinutes = 1 };
        Action act = () => bad.Validate();
        act.Should().Throw<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [Fact]
    public void RunoutWarningLeadMinutes_AtFloor_PassesValidation()
    {
        SpoolCoverageSettings ok = new() { RunoutWarningLeadMinutes = 5 };
        Action act = () => ok.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task FeatureDisabled_SuppressesAllWarnings()
    {
        // Rebase-note (#725): this is the local Enabled toggle. Once
        // IOperatorFeatureGate.FilamentCoverageEnabled lands, this test moves
        // into a controller-level test that asserts 404 + featureDisabled.
        SpoolCoverageSettings off = new() { Enabled = false, QueuedShortageWarningsEnabled = true };
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(settings: off, liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 120);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(120)));
        _ = await db.SaveChangesAsync();
        spool.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 5));

        IReadOnlyList<FilamentRunoutWarningDto> warnings = await svc.GetRunoutWarningsAsync(CancellationToken.None);
        warnings.Should().BeEmpty("disabled coverage feature must emit no warnings");
    }

    // ------------------------------------------------------------------
    // Fleet batching / performance
    // ------------------------------------------------------------------
    [Fact]
    public async Task FleetEndpoint_BatchesSpoolAndJobQueries_ForManyPrinters()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, Mock<IPrintersService> printers) =
            BuildService(liveProgress: 0.0);

        const int printerCount = 12;
        int nextSpoolId = 1;
        for (int i = 0; i < printerCount; i++)
        {
            int spoolId = nextSpoolId++;
            Printer p = SeedPrinter(db, $"p{i}", T(0, spoolId: spoolId, primary: true));
            GcodeFile g = Gcode(estimatedTotalGrams: 10);
            db.GcodeFiles.Add(g);
            db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, g));
            spool.Setup(s => s.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(spoolId, remainingG: 500));
        }

        _ = await db.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = await svc.GetForFleetAsync(CancellationToken.None);
        fleet.Printers.Should().HaveCount(printerCount);
        fleet.Printers.Should().OnlyContain(p => p.Status == FilamentCoverageStatus.Covers);

        // Each spool resolved exactly once (batching), not printerCount×N times.
        spool.Verify(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(printerCount));
    }

    [Fact]
    public async Task FleetEndpoint_DoesNotCallLiveProgress_WhenNoActiveJob()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, Mock<IPrintersService> printers) =
            BuildService();

        Printer p1 = SeedPrinter(db, "idle1", T(0, spoolId: 1, primary: true));
        Printer p2 = SeedPrinter(db, "idle2", T(0, spoolId: 2, primary: true));
        spool.Setup(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((int id, CancellationToken _) => Spool(id, 100));
        _ = await db.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = await svc.GetForFleetAsync(CancellationToken.None);
        fleet.Printers.Should().HaveCount(2);
        // Neither printer had an active job — live-progress lookups must be skipped.
        printers.Verify(x => x.GetPrintJobStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // Attention seam
    // ------------------------------------------------------------------
    [Fact]
    public async Task AttentionSource_EmitsWarnings_ForInsufficientQueue_EvenWithoutRunoutEta()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService();

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile queued = Gcode(estimatedTotalGrams: 500);
        db.GcodeFiles.Add(queued);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queued));
        _ = await db.SaveChangesAsync();

        spool.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 100));

        IReadOnlyList<FilamentRunoutWarningDto> warnings = await svc.GetRunoutWarningsAsync(CancellationToken.None);
        warnings.Should().ContainSingle(w =>
            w.Reason == "insufficient-for-assigned-queue"
            && w.PrinterId == p.Id
            && w.ToolheadIndex == 0);
    }

    [Fact]
    public async Task AttentionSource_NeverEmits_WhenSlotIsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(); // unknown metadata
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(10)));
        _ = await db.SaveChangesAsync();

        spool.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 5));

        IReadOnlyList<FilamentRunoutWarningDto> warnings = await svc.GetRunoutWarningsAsync(CancellationToken.None);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task PrinterNotFound_ReturnsNull()
    {
        (FilamentCoverageService svc, AppDbContext _, _, _) = BuildService();
        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(Guid.NewGuid(), CancellationToken.None);
        cov.Should().BeNull();
    }
}
