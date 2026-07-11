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
        // Stable-basis: CurrentJobRequiredGrams is full per-copy × RemainingCopies.
        slot.CurrentJobRequiredGrams.Should().Be(100);
        // Display-only: prorated by progress on the current copy only.
        slot.CurrentJobRemainingGrams.Should().BeApproximately(75, 0.01,
            "display value: 25% progress → 75g left on current copy");
        slot.QueuedRequiredGrams.Should().Be(40);
        // TotalDemandGrams is the classification basis: full active + queued.
        slot.TotalDemandGrams.Should().BeApproximately(140, 0.01);
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

        // Stable basis: spool 40g vs full 200g demand → insufficient.
        spool.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Spool(42, remainingG: 40));

        DateTime before = DateTime.UtcNow;
        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov!.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.Status.Should().Be(FilamentCoverageStatus.Insufficient);
        slot.StatusReason.Should().Be("insufficient-remaining");
        slot.PredictedRunoutAt.Should().NotBeNull();
        // Fallback anchor (ActualStartTime null): now + 40/200 × 120min = now + 24min.
        slot.PredictedRunoutAt!.Value.Should().BeCloseTo(before.AddMinutes(24), TimeSpan.FromSeconds(30));
        // Stable layer projection: (40/200) × 400 = layer 80.
        slot.PredictedRunoutLayer.Should().Be(80);
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
        // Stable basis: classification uses full 200g demand, remaining 55g → Insufficient
        // regardless of progress (would have been "covers" under the old shrinking basis).
        slot.CurrentJobRequiredGrams.Should().Be(200);
        // Display only: 25% of current copy remains × 200g = 50g.
        slot.CurrentJobRemainingGrams.Should().BeApproximately(50, 0.01);
        slot.Status.Should().Be(FilamentCoverageStatus.Insufficient,
            "classification is on stable full-demand basis; static spool remaining < full demand");
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

    // ------------------------------------------------------------------
    // #709 CONVERGENCE ITEM 1: stable-basis classification and stable
    // runout ETA/layer as progress ticks.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ActiveJob_InitiallyInsufficient_NeverFlipsToCovers_AsProgressAdvances()
    {
        // A print that is insufficient at the START must remain insufficient
        // at every progress tick — the fixed spool remaining does not grow.
        Printer? seedPrinter = null;
        DateTime start = DateTime.UtcNow.AddMinutes(-30);

        async Task<PrinterFilamentCoverageDto?> Snapshot(double progress)
        {
            (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
                BuildService(liveProgress: progress);

            Printer p = SeedPrinter(db, "prog", T(0, spoolId: 1, primary: true));
            seedPrinter = p;
            GcodeFile g = Gcode(estimatedTotalGrams: 200, totalLayers: 400, printTimeMinutes: 120);
            db.GcodeFiles.Add(g);
            PrintJob job = Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(120));
            job.ActualStartTime = start; // stable ETA anchor
            db.PrintJobs.Add(job);
            _ = await db.SaveChangesAsync();
            spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 40));

            return await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        }

        DateTime? etaAt0 = null;
        int? layerAt0 = null;
        foreach (double progress in new[] { 0.0, 15.0, 30.0, 55.0, 80.0 })
        {
            PrinterFilamentCoverageDto? cov = await Snapshot(progress);
            cov.Should().NotBeNull($"progress={progress}");
            ToolheadCoverageDto slot = cov!.Toolheads.Single();

            slot.Status.Should().Be(FilamentCoverageStatus.Insufficient,
                $"progress={progress}: static spool remaining (40g) is < full stable demand (200g); classification must not flip to Covers");
            slot.CurrentJobRequiredGrams.Should().Be(200,
                $"progress={progress}: required grams reports the stable full-demand basis");
            slot.PredictedRunoutAt.Should().NotBeNull($"progress={progress}");
            slot.PredictedRunoutLayer.Should().NotBeNull($"progress={progress}");

            if (etaAt0 is null)
            {
                etaAt0 = slot.PredictedRunoutAt;
                layerAt0 = slot.PredictedRunoutLayer;
            }
            else
            {
                slot.PredictedRunoutAt!.Value.Should().BeCloseTo(etaAt0!.Value, TimeSpan.FromSeconds(1),
                    $"progress={progress}: ETA anchored to ActualStartTime must not drift");
                slot.PredictedRunoutLayer.Should().Be(layerAt0,
                    $"progress={progress}: layer is (usable/reqFull) × totalLayers — stable across progress");
            }
        }

        // Layer projection: 40/200 × 400 = 80. Stable across every progress tick above.
        layerAt0.Should().Be(80);
        seedPrinter.Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // #709 CONVERGENCE ITEM 2: fleet must not concurrently touch the
    // shared IPrintersService/AppDbContext.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FleetEndpoint_DoesNotConcurrentlyAccessSharedContextOrPrintersService()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();

        // Overlap detector: throws when a second caller enters while the
        // first is still inside GetPrintJobStatusAsync. The prior fanned-out
        // implementation triggered this immediately with N>1 printers; the
        // sequential prefetch loop must not.
        int inFlight = 0;
        int maxObserved = 0;
        Mock<IPrintersService> printerMock = new(MockBehavior.Loose);
        printerMock
            .Setup(x => x.GetPrintJobStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _pid, CancellationToken _ct) =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int prevMax;
                do
                {
                    prevMax = maxObserved;
                    if (now <= prevMax)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref maxObserved, now, prevMax) != prevMax);

                if (now > 1)
                {
                    throw new InvalidOperationException(
                        "GetPrintJobStatusAsync called concurrently — shared IPrintersService/AppDbContext is not safe for concurrent use.");
                }

                // Simulate a small amount of I/O work so any concurrent
                // caller would definitely overlap.
                await Task.Delay(20).ConfigureAwait(false);
                _ = Interlocked.Decrement(ref inFlight);
                return new PrintJobStatusDto { Progress = 42.0 };
            });

        Mock<ISpoolmanService> spoolMock = new(MockBehavior.Loose);
        Mock<ISettingsService> settingsMock = new(MockBehavior.Loose);
        settingsMock.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(new SpoolCoverageSettings());

        FilamentCoverageService svc = new(
            db,
            spoolMock.Object,
            printerMock.Object,
            settingsMock.Object,
            NullLogger<FilamentCoverageService>.Instance);

        // Seed several printers, each with an ACTIVE job so live-progress
        // is fetched for each one.
        for (int i = 0; i < 6; i++)
        {
            int spoolId = 1000 + i;
            Printer p = SeedPrinter(db, $"p{i}", T(0, spoolId: spoolId, primary: true));
            GcodeFile g = Gcode(estimatedTotalGrams: 10, printTimeMinutes: 30);
            db.GcodeFiles.Add(g);
            db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
            spoolMock.Setup(s => s.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Spool(spoolId, remainingG: 500));
        }

        _ = await db.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = await svc.GetForFleetAsync(CancellationToken.None);
        fleet.Printers.Should().HaveCount(6);
        maxObserved.Should().Be(1,
            "the fleet path must prefetch live progress sequentially — no overlapping calls into the shared scoped IPrintersService");
    }

    // ------------------------------------------------------------------
    // #709 CONVERGENCE ITEM 3: multi-copy demand accounting.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ActiveJob_MultiCopy_ChargesFullRemainingCopyDemand()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 60, printTimeMinutes: 45);
        db.GcodeFiles.Add(g);
        PrintJob job = Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(45));
        job.Copies = 3;
        job.CompletedCopies = 1; // → 2 copies remaining
        db.PrintJobs.Add(job);
        _ = await db.SaveChangesAsync();

        // 100g remaining vs 60g × 2 remaining copies = 120g demand → Insufficient.
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 100));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        ToolheadCoverageDto slot = cov!.Toolheads.Single();
        slot.CurrentJobRequiredGrams.Should().Be(120, "per-copy 60g × 2 remaining copies");
        slot.CurrentJobRemainingGrams.Should().BeApproximately(120, 0.01,
            "progress=0 → current copy full (60g) + 1 future full copy (60g) = 120g");
        slot.Status.Should().Be(FilamentCoverageStatus.Insufficient);
    }

    [Fact]
    public async Task QueuedJob_MultiCopy_ChargesPerCopyTimesRemainingCopies()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));

        GcodeFile queued = Gcode(estimatedTotalGrams: 30);
        db.GcodeFiles.Add(queued);
        PrintJob qj = Job(p.Id, PrintJobStatus.Assigned, queued);
        qj.Copies = 4;
        qj.CompletedCopies = 1; // 3 remaining
        db.PrintJobs.Add(qj);
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 200));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        ToolheadCoverageDto slot = cov!.Toolheads.Single();
        slot.QueuedRequiredGrams.Should().Be(90, "30g × 3 remaining copies");
        slot.TotalDemandGrams.Should().Be(90);
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    // ------------------------------------------------------------------
    // #709 CONVERGENCE ITEM 4: no-spool + demand is Unknown, never Covers.
    // ------------------------------------------------------------------
    [Fact]
    public async Task NoSpool_WithActiveDemand_ReturnsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> _, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: null, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 40, printTimeMinutes: 20);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(20)));
        _ = await db.SaveChangesAsync();

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        ToolheadCoverageDto slot = cov!.Toolheads.Single();
        slot.Status.Should().Be(FilamentCoverageStatus.Unknown);
        slot.StatusReason.Should().Be("no-spool-assigned");
    }

    [Fact]
    public async Task NoSpool_WithNoActiveJobButAssignedQueueDemand_ReturnsUnknown()
    {
        // Regression: previously an activeJob-null short-circuit returned
        // Covers even when queued jobs assigned to the same printer needed
        // this toolhead. Item 4 requires Unknown in that state.
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> _, _) =
            BuildService();

        Printer p = SeedPrinter(db, "p", T(0, spoolId: null, primary: true));
        GcodeFile queued = Gcode(estimatedTotalGrams: 25);
        db.GcodeFiles.Add(queued);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queued));
        _ = await db.SaveChangesAsync();

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        cov!.Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Single().Status.Should().Be(FilamentCoverageStatus.Unknown);
        cov.Toolheads.Single().StatusReason.Should().Be("no-spool-assigned");
    }

    [Fact]
    public async Task NoSpool_ToolheadUnusedByActive_ButNeededByQueue_ReturnsUnknown()
    {
        // Multi-toolhead: T0 is used by active print, T1 is unused by the
        // active print but a queued job needs both extruders. T1 has no spool
        // bound. Must be Unknown — old code let this pass as Covers because
        // active-demand for T1 was zero.
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(
            db,
            "dual",
            T(0, spoolId: 100, primary: true, material: "PLA"),
            T(1, spoolId: null, material: "PETG"));

        // Active job uses ONLY T0.
        GcodeFile active = Gcode(perExtruder: [50, 0], extruderCount: 2, printTimeMinutes: 30);
        // Queued job uses BOTH extruders.
        GcodeFile queued = Gcode(perExtruder: [20, 15], extruderCount: 2);
        db.GcodeFiles.AddRange(active, queued);

        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, active, TimeSpan.FromMinutes(30)));
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queued));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 500));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);
        ToolheadCoverageDto t0 = cov!.Toolheads.Single(s => s.ToolheadIndex == 0);
        ToolheadCoverageDto t1 = cov.Toolheads.Single(s => s.ToolheadIndex == 1);

        t0.Status.Should().Be(FilamentCoverageStatus.Covers);
        t1.Status.Should().Be(FilamentCoverageStatus.Unknown,
            "T1 has queued demand but no spool bound — must be Unknown even though the active job does not use it");
        t1.StatusReason.Should().Be("no-spool-assigned");
        cov.Status.Should().Be(FilamentCoverageStatus.Unknown);
    }
}
