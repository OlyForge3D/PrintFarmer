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
using Farm.Infrastructure.Services.OperatorFeatures;
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
            double? liveProgress = null,
            bool coverageEnabled = true,
            bool tracksLiveConsumption = false,
            IReadOnlyDictionary<Guid, PrinterStatusDto>? cachedStatuses = null)
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
        Mock<IPrinterStatusCacheReader> statusCacheMock = new(MockBehavior.Strict);
        statusCacheMock.Setup(c => c.GetAllStatuses())
            .Returns(cachedStatuses ?? new Dictionary<Guid, PrinterStatusDto>());

        Mock<ISettingsService> settingsMock = new(MockBehavior.Loose);
        settingsMock.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(settings ?? new SpoolCoverageSettings());

        Mock<IOperatorFeatureGate> gateMock = new(MockBehavior.Strict);
        gateMock.Setup(g => g.IsEnabled(OperatorFeature.FilamentCoverage)).Returns(coverageEnabled);
        gateMock.Setup(g => g.IsEnabledAsync(OperatorFeature.FilamentCoverage, It.IsAny<CancellationToken>())).ReturnsAsync(coverageEnabled);

        Mock<IFilamentCoverageSpoolResolver> resolverMock = new(MockBehavior.Strict);
        resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<IReadOnlyList<Printer>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<Printer> printers, CancellationToken ct) =>
            {
                Dictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result = [];
                foreach (Printer printer in printers)
                {
                    Dictionary<int, FilamentCoverageSpoolSnapshot> spools = [];
                    foreach (int spoolId in printer.Toolheads
                        .Where(t => t.CurrentSpoolId.HasValue)
                        .Select(t => t.CurrentSpoolId!.Value)
                        .Concat(printer.CurrentSpoolId.HasValue ? [printer.CurrentSpoolId.Value] : [])
                        .Distinct())
                    {
                        SpoolmanSpoolDto? spool = await spoolMock.Object.GetSpoolByIdAsync(spoolId, ct);
                        spools[spoolId] = spool is null
                            ? new(null, tracksLiveConsumption, FilamentCoverageSpoolResolver.ReasonSourceUnavailable)
                            : new(spool, tracksLiveConsumption, null);
                    }

                    result[printer.Id] = spools;
                }

                return result;
            });

        FilamentCoverageService svc = new(
            db,
            resolverMock.Object,
            printerMock.Object,
            statusCacheMock.Object,
            settingsMock.Object,
            gateMock.Object,
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
        double? printTimeMinutes = null,
        string[]? perExtruderMaterials = null,
        string? requiredMaterial = null) => new()
        {
            Id = Guid.NewGuid(),
            FileName = "part.gcode",
            EstimatedFilamentWeightG = estimatedTotalGrams,
            FilamentPerExtruderWeightG = perExtruder is not null ? JsonSerializer.Serialize(perExtruder) : null,
            FilamentPerExtruderType = perExtruderMaterials is not null ? JsonSerializer.Serialize(perExtruderMaterials) : null,
            ExtruderCount = extruderCount,
            TotalLayers = totalLayers,
            EstimatedPrintTimeMinutes = printTimeMinutes,
            RequiredMaterial = requiredMaterial,
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
        slot.RemainingGrams.Should().BeApproximately(475, 0.01,
            "managed Spoolman is completion-updated, so 25g estimated consumption is reconciled once");
        slot.CurrentJobRequiredGrams.Should().Be(100);
        slot.CurrentJobRemainingGrams.Should().BeApproximately(75, 0.01,
            "25% progress leaves 75g on the current copy");
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
        PrintJob activeJob = Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(120));
        activeJob.ActualStartTime = DateTime.UtcNow.AddMinutes(-60);
        db.PrintJobs.Add(activeJob);
        _ = await db.SaveChangesAsync();

        // Stable basis: spool 40g vs full 200g demand → insufficient.
        spool.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
             .ReturnsAsync(Spool(42, remainingG: 40));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov!.Status.Should().Be(FilamentCoverageStatus.Runout);
        ToolheadCoverageDto slot = cov.Toolheads.Single();
        slot.Status.Should().Be(FilamentCoverageStatus.Runout);
        slot.StatusReason.Should().Be("insufficient-remaining");
        slot.PredictedRunoutAt.Should().NotBeNull();
        slot.PredictedRunoutAt!.Value.Should().BeCloseTo(
            activeJob.ActualStartTime.Value.AddMinutes(24),
            TimeSpan.FromSeconds(2));
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
        cov!.Status.Should().Be(FilamentCoverageStatus.Runout);
        cov.Toolheads.Should().HaveCount(2);

        ToolheadCoverageDto t0 = cov.Toolheads.Single(s => s.ToolheadIndex == 0);
        t0.CurrentJobRequiredGrams.Should().Be(70);
        t0.Status.Should().Be(FilamentCoverageStatus.Covers);

        ToolheadCoverageDto t1 = cov.Toolheads.Single(s => s.ToolheadIndex == 1);
        t1.CurrentJobRequiredGrams.Should().Be(30);
        t1.RemainingGrams.Should().Be(10);
        t1.Status.Should().Be(FilamentCoverageStatus.Runout);
        t1.PredictedRunoutAt.Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // Issue #711 round-10 Finding 2: MMU demand index space.
    // Slicer demand keys are 0-based G-code T-indices; MMU gates are stored
    // 1-based (physical hotend takes Index 0). Demand must route through
    // ToolheadIndexMapper so T0->gate 1, T1->gate 2, T2->gate 3 instead of
    // being shifted by one gate.
    // ------------------------------------------------------------------
    [Fact]
    public async Task MmuGates_PerExtruderDemand_RoutesToCorrectGateIndex()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        static Toolhead Gate(int index, int spoolId, string material) => new()
        {
            Id = Guid.NewGuid(),
            Index = index,
            Name = $"Gate {index}",
            ToolheadType = ToolheadType.MmuGate,
            CurrentSpoolId = spoolId,
            CurrentMaterial = material,
        };

        Toolhead physical = T(0, spoolId: 999, primary: true, material: "STALE");
        Printer p = SeedPrinter(
            db,
            "mmu",
            physical,
            Gate(1, spoolId: 100, material: "PLA"),
            Gate(2, spoolId: 200, material: "PETG"),
            Gate(3, spoolId: 300, material: "ABS"));

        // Per-extruder demand keyed by 0-based G-code tool index: T0=10g, T1=20g, T2=30g.
        GcodeFile g = Gcode(estimatedTotalGrams: 60, perExtruder: [10, 20, 30], extruderCount: 3, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 500, material: "PLA"));
        spool.Setup(s => s.GetSpoolByIdAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(200, remainingG: 500, material: "PETG"));
        spool.Setup(s => s.GetSpoolByIdAsync(300, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(300, remainingG: 500, material: "ABS"));
        spool.Setup(s => s.GetSpoolByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(999, remainingG: 500, material: "PLA"));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov!.Toolheads.Should().HaveCount(3, "each demand key maps onto a real gate; no phantom slots");
        cov.Toolheads.Should().NotContain(
            toolhead => toolhead.ToolheadId == physical.Id,
            "the shared physical hotend is not a filament source when MMU gates exist");

        // ToolheadIndex is the 0-based G-code index (issue #711, round-19 M19-2 — the DTO must
        // emit the mapped G-code index, not the raw stored gate index): gate stored at Index 1
        // maps to G-code T0, Index 2 -> T1, Index 3 -> T2.
        cov.Toolheads.Single(t => t.ToolheadIndex == 0).CurrentJobRequiredGrams.Should().Be(10);
        cov.Toolheads.Single(t => t.ToolheadIndex == 1).CurrentJobRequiredGrams.Should().Be(20);
        cov.Toolheads.Single(t => t.ToolheadIndex == 2).CurrentJobRequiredGrams.Should().Be(30);
    }

    // Issue #711 round-19 Finding M19-2: the coverage DTO must emit the mapped 0-based G-code
    // index, not the raw 1-based stored gate index — the doc-comment already claimed 0-based,
    // but the wire value was the bug.
    [Fact]
    public async Task MmuGate_StoredIndexOne_EmitsZeroBasedGcodeToolheadIndex()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        Printer p = SeedPrinter(
            db,
            "single-gate-mmu",
            T(0, spoolId: null, primary: true, material: null),
            new Toolhead
            {
                Id = Guid.NewGuid(),
                Index = 1,
                Name = "Gate 1",
                ToolheadType = ToolheadType.MmuGate,
                CurrentSpoolId = 100,
                CurrentMaterial = "PLA",
            });

        GcodeFile g = Gcode(estimatedTotalGrams: 10, perExtruder: [10], extruderCount: 1, printTimeMinutes: 10);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(10)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 500, material: "PLA"));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        ToolheadCoverageDto gate = cov!.Toolheads.Should().ContainSingle().Which;
        gate.ToolheadIndex.Should().Be(0, "gate stored at Index 1 must emit the mapped G-code T0, not the raw stored index 1");
    }

    [Fact]
    public async Task NonMmu_PhysicalHotend_SingleToolDemand_RoutesToPhysical()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0);

        // Non-MMU printer: physical hotend at Index 0. T0 must resolve to the physical hotend.
        Printer p = SeedPrinter(db, "single", T(0, spoolId: 100, primary: true, material: "PLA"));

        // Single-tool gcode (no per-extruder breakdown) exercises the single-tool fallback,
        // which must attribute demand to the primary toolhead in 0-based G-code space.
        GcodeFile g = Gcode(estimatedTotalGrams: 50, printTimeMinutes: 30);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        spool.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(100, remainingG: 500));

        PrinterFilamentCoverageDto? cov = await svc.GetForPrinterAsync(p.Id, CancellationToken.None);

        cov!.Toolheads.Should().HaveCount(1);
        ToolheadCoverageDto t0 = cov.Toolheads.Single(t => t.ToolheadIndex == 0);
        t0.CurrentJobRequiredGrams.Should().Be(50, "T0 maps to the physical hotend at Index 0");
        t0.Status.Should().Be(FilamentCoverageStatus.Covers);
    }
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
    public async Task NoGcodeUsageMetadata_UnboundSingleToolActiveJob_ReturnsUnknownMetadataReason()
    {
        (FilamentCoverageService svc, AppDbContext db, _, _) = BuildService();
        Printer p = SeedPrinter(db, "p", T(0, spoolId: null, primary: true));
        GcodeFile g = Gcode();
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
        _ = await db.SaveChangesAsync();

        PrinterFilamentCoverageDto coverage = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!;

        coverage.Status.Should().Be(FilamentCoverageStatus.Unknown);
        coverage.Toolheads.Single().Status.Should().Be(FilamentCoverageStatus.Unknown);
        coverage.Toolheads.Single().StatusReason.Should().Be("no-gcode-metadata");
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
        slot.CurrentJobRequiredGrams.Should().Be(200);
        slot.CurrentJobRemainingGrams.Should().BeApproximately(50, 0.01);
        slot.RemainingGrams.Should().Be(0,
            "managed-source snapshot 55g minus estimated 150g consumed is clamped at zero");
        slot.Status.Should().Be(FilamentCoverageStatus.Runout,
            "availability and active demand are both reconciled at the same progress point");
    }

    [Theory]
    [InlineData(0.0, 500.0, 200.0)]
    [InlineData(50.0, 400.0, 100.0)]
    [InlineData(99.0, 302.0, 2.0)]
    [InlineData(100.0, 300.0, 0.0)]
    public async Task ActiveJobProgress_ManagedSource_ReconcilesStaticWeightOnce(
        double progress,
        double expectedAvailable,
        double expectedDemand)
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: progress);

        Printer p = SeedPrinter(db, "managed", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 500));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.RemainingGrams.Should().BeApproximately(expectedAvailable, 0.01);
        slot.CurrentJobRemainingGrams.Should().BeApproximately(expectedDemand, 0.01);
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task ActiveJobProgress_NativeSource_DoesNotSubtractConsumptionTwice()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 50, tracksLiveConsumption: true);

        Printer p = SeedPrinter(db, "native", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 400));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.RemainingGrams.Should().Be(400, "native Spoolman already reports live consumption");
        slot.CurrentJobRemainingGrams.Should().Be(100);
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task CompletedTransition_UsesCompletionUpdatedWeightWithoutActiveEstimate()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) = BuildService();

        Printer p = SeedPrinter(db, "completed", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 60);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Completed, g, TimeSpan.FromMinutes(60)));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 300));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.RemainingGrams.Should().Be(300);
        slot.CurrentJobRequiredGrams.Should().BeNull();
        slot.CurrentJobRemainingGrams.Should().BeNull();
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
        slot.Status.Should().Be(FilamentCoverageStatus.Runout);
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
        cov!.Status.Should().Be(FilamentCoverageStatus.Runout);
        cov.Toolheads.Single().AvailableForNewDemandGrams.Should().Be(
            0,
            "existing demand and the configured reserve consume all capacity for a newly dispatched job");
    }

    [Fact]
    public async Task RunoutWarningLeadMinutes_ControlsAttentionEmission()
    {
        SpoolCoverageSettings tight = new() { RunoutWarningLeadMinutes = 5 };
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

        SpoolCoverageSettings wide = new() { RunoutWarningLeadMinutes = 60 };
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
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0.0, coverageEnabled: false);

        Printer p = SeedPrinter(db, "p", T(0, spoolId: 1, primary: true));
        GcodeFile g = Gcode(estimatedTotalGrams: 200, printTimeMinutes: 120);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(120)));
        _ = await db.SaveChangesAsync();
        spool.Setup(x => x.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Spool(1, remainingG: 5));

        IReadOnlyList<FilamentRunoutWarningDto> warnings = await svc.GetRunoutWarningsAsync(CancellationToken.None);
        warnings.Should().BeEmpty("disabled coverage feature must emit no warnings");
        spool.Verify(
            x => x.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the shared gate must short-circuit before coverage source access");
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
    // #709 CONVERGENCE ITEM 1: progress-reconciled availability and stable
    // runout ETA/layer as progress ticks.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ActiveJob_InitiallyInsufficient_NeverFlipsToCovers_AsProgressAdvances()
    {
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

            slot.Status.Should().Be(FilamentCoverageStatus.Runout,
                $"progress={progress}: managed availability and remaining demand are reconciled at the same point");
            slot.CurrentJobRequiredGrams.Should().Be(200,
                $"progress={progress}: full required grams remain informational");
            slot.CurrentJobRemainingGrams.Should().BeApproximately(200 * (1 - (progress / 100.0)), 0.01);
            slot.RemainingGrams.Should().BeApproximately(Math.Max(0, 40 - (200 * progress / 100.0)), 0.01);
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
    // #709 R4: fleet must use one cache snapshot and never call live status.
    // ------------------------------------------------------------------
    [Fact]
    public async Task FleetEndpoint_UsesSingleCacheSnapshot_AndNeverCallsLivePrinterStatus()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();

        Mock<IPrintersService> printerMock = new(MockBehavior.Strict);
        Mock<IPrinterStatusCacheReader> statusCacheMock = new(MockBehavior.Strict);
        Dictionary<Guid, PrinterStatusDto> cachedStatuses = [];
        statusCacheMock.Setup(c => c.GetAllStatuses()).Returns(cachedStatuses);

        Mock<ISpoolmanService> spoolMock = new(MockBehavior.Loose);
        Mock<ISettingsService> settingsMock = new(MockBehavior.Loose);
        settingsMock.Setup(s => s.Get<SpoolCoverageSettings>()).Returns(new SpoolCoverageSettings());
        Mock<IOperatorFeatureGate> gateMock = new(MockBehavior.Strict);
        gateMock.Setup(g => g.IsEnabled(OperatorFeature.FilamentCoverage)).Returns(true);
        gateMock.Setup(g => g.IsEnabledAsync(OperatorFeature.FilamentCoverage, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Mock<IFilamentCoverageSpoolResolver> resolverMock = new(MockBehavior.Strict);
        resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<IReadOnlyList<Printer>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<Printer> printers, CancellationToken ct) =>
            {
                Dictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result = [];
                foreach (Printer printer in printers)
                {
                    Dictionary<int, FilamentCoverageSpoolSnapshot> spools = [];
                    foreach (int spoolId in printer.Toolheads.Select(t => t.CurrentSpoolId).OfType<int>())
                    {
                        spools[spoolId] = new(
                            await spoolMock.Object.GetSpoolByIdAsync(spoolId, ct),
                            false,
                            null);
                    }

                    result[printer.Id] = spools;
                }

                return result;
            });

        List<Guid> printerIds = [];
        for (int i = 0; i < 6; i++)
        {
            int spoolId = 1000 + i;
            Printer p = SeedPrinter(db, $"p{i}", T(0, spoolId: spoolId, primary: true));
            printerIds.Add(p.Id);
            cachedStatuses[p.Id] = new PrinterStatusDto(p.Id, true, "Printing", Progress: i * 10);
            GcodeFile g = Gcode(estimatedTotalGrams: 10, printTimeMinutes: 30);
            db.GcodeFiles.Add(g);
            db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g, TimeSpan.FromMinutes(30)));
            spoolMock.Setup(s => s.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Spool(spoolId, remainingG: 500));
        }

        _ = await db.SaveChangesAsync();
        FilamentCoverageService svc = new(
            db,
            resolverMock.Object,
            printerMock.Object,
            statusCacheMock.Object,
            settingsMock.Object,
            gateMock.Object,
            NullLogger<FilamentCoverageService>.Instance);

        FleetFilamentCoverageDto fleet = await svc.GetForFleetAsync(CancellationToken.None);

        fleet.Printers.Should().HaveCount(6);
        fleet.Printers
            .OrderBy(p => p.PrinterName)
            .Select(p => p.Toolheads.Single().CurrentJobRemainingGrams)
            .Should().Equal(10, 9, 8, 7, 6, 5);
        statusCacheMock.Verify(c => c.GetAllStatuses(), Times.Once);
        printerMock.Verify(
            p => p.GetPrintJobStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
        slot.Status.Should().Be(FilamentCoverageStatus.Runout);
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

    [Fact]
    public async Task PrimaryToolhead_UsesLegacyPrinterSpoolBinding_WhenToolheadBindingIsEmpty()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "legacy", T(0, spoolId: null, primary: true));
        p.CurrentSpoolId = 42;
        p.CurrentMaterial = "PLA";
        GcodeFile g = Gcode(estimatedTotalGrams: 50, requiredMaterial: "PLA");
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(42, remainingG: 100));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.SpoolId.Should().Be(42);
        slot.Material.Should().Be("PLA");
        slot.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task ActiveJob_PerToolMaterialMismatch_ReturnsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "material", T(0, spoolId: 1, primary: true, material: "PETG"));
        GcodeFile g = Gcode(
            perExtruder: [50],
            extruderCount: 1,
            perExtruderMaterials: ["PLA"]);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 500, material: "PETG"));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.Status.Should().Be(FilamentCoverageStatus.Unknown);
        slot.StatusReason.Should().Be("material-mismatch");
    }

    [Fact]
    public async Task OwningSourceMaterial_OverridesStaleDenormalizedToolheadMaterial()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "source-material", T(0, spoolId: 1, primary: true, material: "PLA"));
        GcodeFile g = Gcode(estimatedTotalGrams: 50, requiredMaterial: "PLA");
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 500, material: "PETG"));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.Material.Should().Be("PETG");
        slot.Status.Should().Be(FilamentCoverageStatus.Unknown);
        slot.StatusReason.Should().Be("material-mismatch");
    }

    [Fact]
    public async Task AssignedQueue_DifferentRequiredMaterial_ReturnsUnknown()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "queue-material", T(0, spoolId: 1, primary: true, material: "PLA"));
        GcodeFile active = Gcode(estimatedTotalGrams: 20, requiredMaterial: "PLA");
        GcodeFile queued = Gcode(estimatedTotalGrams: 30, requiredMaterial: "PETG");
        db.GcodeFiles.AddRange(active, queued);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, active));
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Assigned, queued));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 500));

        ToolheadCoverageDto slot = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!.Toolheads.Single();

        slot.TotalDemandGrams.Should().Be(50);
        slot.Status.Should().Be(FilamentCoverageStatus.Unknown);
        slot.StatusReason.Should().Be("material-mismatch");
    }

    [Fact]
    public async Task ActiveJob_RequiresMissingPrinterToolhead_ReturnsSyntheticUnknownSlot()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "single", T(0, spoolId: 1, primary: true, material: "PLA"));
        GcodeFile g = Gcode(
            perExtruder: [20, 30],
            extruderCount: 2,
            perExtruderMaterials: ["PLA", "PETG"]);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 500));

        PrinterFilamentCoverageDto coverage = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!;
        ToolheadCoverageDto missing = coverage.Toolheads.Single(slot => slot.ToolheadIndex == 1);

        missing.CurrentJobRequiredGrams.Should().Be(30);
        missing.Status.Should().Be(FilamentCoverageStatus.Unknown);
        missing.StatusReason.Should().Be("toolhead-unavailable");
        coverage.Status.Should().Be(FilamentCoverageStatus.Unknown);
    }

    [Fact]
    public async Task ActiveJob_AtHundredPercent_WithNoFutureCopies_IgnoresExhaustedCompatibilityDemand()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 100);
        Printer p = SeedPrinter(db, "transition", T(0, spoolId: 1, primary: true, material: "ABS"));
        GcodeFile g = Gcode(
            perExtruder: [20, 30],
            extruderCount: 2,
            perExtruderMaterials: ["PLA", "PETG"]);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(1, remainingG: 480, material: "ABS"));

        PrinterFilamentCoverageDto coverage = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!;

        coverage.Toolheads.Should().ContainSingle();
        coverage.Toolheads.Single().CurrentJobRemainingGrams.Should().Be(0);
        coverage.Status.Should().Be(FilamentCoverageStatus.Covers);
    }

    [Fact]
    public async Task ActiveJob_OnPrinterWithoutToolheads_ReturnsUnknownInsteadOfCovers()
    {
        (FilamentCoverageService svc, AppDbContext db, _, _) = BuildService(liveProgress: 0);
        Printer p = SeedPrinter(db, "no-tools");
        GcodeFile g = Gcode();
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();

        PrinterFilamentCoverageDto coverage = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!;

        coverage.Status.Should().Be(FilamentCoverageStatus.Unknown);
        coverage.Toolheads.Should().ContainSingle();
        coverage.Toolheads.Single().StatusReason.Should().Be("no-gcode-metadata");
    }

    [Fact]
    public async Task MissingRequiredToolhead_IsInsertedInDeterministicIndexOrder()
    {
        (FilamentCoverageService svc, AppDbContext db, Mock<ISpoolmanService> spool, _) =
            BuildService(liveProgress: 0);
        Printer p = SeedPrinter(
            db,
            "sparse",
            T(0, spoolId: 10, primary: true),
            T(2, spoolId: 12));
        GcodeFile g = Gcode(perExtruder: [10, 20, 30], extruderCount: 3);
        db.GcodeFiles.Add(g);
        db.PrintJobs.Add(Job(p.Id, PrintJobStatus.Printing, g));
        _ = await db.SaveChangesAsync();
        spool.Setup(s => s.GetSpoolByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(10, remainingG: 100));
        spool.Setup(s => s.GetSpoolByIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(12, remainingG: 100));

        PrinterFilamentCoverageDto coverage = (await svc.GetForPrinterAsync(p.Id, CancellationToken.None))!;

        coverage.Toolheads.Select(slot => slot.ToolheadIndex).Should().Equal(0, 1, 2);
        coverage.Toolheads[1].StatusReason.Should().Be("toolhead-unavailable");
    }
}
