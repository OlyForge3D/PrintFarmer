using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Unit tests for <see cref="PrinterToolheadSwapValidator"/> backing GitHub issue
/// OlyForge3D/PrintFarmer#710 — the guided filament swap flow's material validation.
/// </summary>
public class PrinterToolheadSwapValidatorTests
{
    private static AppDbContext CreateDb()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SwapValidator_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static PrinterToolheadSwapValidator CreateValidator(
        AppDbContext db,
        SpoolmanSpoolDto? spoolResult,
        out Mock<ISpoolmanService> spoolman)
    {
        spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spoolResult);
        return new PrinterToolheadSwapValidator(db, spoolman.Object, NullLogger<PrinterToolheadSwapValidator>.Instance);
    }

    private static SpoolmanSpoolDto Spool(int id, string material) =>
        new(id, $"Spool {id}", material, RemainingWeightG: 500, ColorHex: "#FFFFFF", InUse: true);

    /// <summary>Asserts the envelope carries a concrete validation body and returns it.</summary>
    private static SwapValidationResultDto Body(SwapValidationResult envelope)
    {
        Assert.Equal(SwapValidationOutcome.Validated, envelope.Outcome);
        Assert.NotNull(envelope.Result);
        return envelope.Result!;
    }

    private static Printer SeedPrinter(AppDbContext db, int toolheadCount = 1)
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "test-printer",
            ServerUrl = "http://p.local"
        };
        for (int i = 0; i < toolheadCount; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Index = i,
                Name = $"T{i}",
                IsPrimary = i == 0,
            });
        }

        db.Printers.Add(printer);
        db.SaveChanges();
        return printer;
    }

    /// <summary>
    /// Seeds an MMU-style printer: the physical hotend as T0 (Index=0, Physical) and
    /// <paramref name="gateCount"/> virtual gates at Index=1..N (MmuGate). Mirrors
    /// <c>PrintersService.CreateMmuVirtualToolheads</c> so the swap validator sees the
    /// same layout as production.
    /// </summary>
    private static Printer SeedMmuPrinter(AppDbContext db, int gateCount = 4)
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "mmu-printer",
            ServerUrl = "http://mmu.local",
            HasMmu = true,
        };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Hotend",
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
        });
        for (int i = 1; i <= gateCount; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Index = i,
                Name = $"Gate {i}",
                IsPrimary = false,
                ToolheadType = ToolheadType.MmuGate,
            });
        }
        db.Printers.Add(printer);
        db.SaveChanges();
        return printer;
    }

    /// <summary>
    /// Seeds a Snapmaker U1-style printer with <paramref name="laneCount"/> physical
    /// lanes stored at Index 0..N-1 (all <see cref="ToolheadType.Physical"/>) — matches
    /// <c>MoonrakerSubscriptionService.PersistSnapmakerU1ToolheadStateAsync</c>.
    /// </summary>
    private static Printer SeedU1Printer(AppDbContext db, int laneCount = 4)
    {
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "u1-printer",
            ServerUrl = "http://u1.local",
        };
        for (int i = 0; i < laneCount; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Index = i,
                Name = $"Lane {i}",
                IsPrimary = i == 0,
                ToolheadType = ToolheadType.Physical,
            });
        }
        db.Printers.Add(printer);
        db.SaveChanges();
        return printer;
    }

    [Fact]
    public async Task ValidateAsync_ReturnsPrinterNotFound_WhenPrinterMissing()
    {
        await using AppDbContext db = CreateDb();
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);

        SwapValidationResult result = await validator.ValidateAsync(Guid.NewGuid(), 0, 1, CancellationToken.None);

        Assert.Equal(SwapValidationOutcome.PrinterNotFound, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsToolheadNotFound_WhenLaneNotAValidSourceOnNonMmuPrinter()
    {
        // A single-tool, non-MMU printer has no lane at index 5 and cannot host MMU gates, so
        // the lane is not a valid filament source → ToolheadNotFound (404, no write), NOT a
        // blind bind (B2).
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db, toolheadCount: 1);
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);

        SwapValidationResult result = await validator.ValidateAsync(printer.Id, 5, 1, CancellationToken.None);

        Assert.Equal(SwapValidationOutcome.ToolheadNotFound, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsOutOfRange_WhenIndexBeyondMax()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db, toolheadCount: 1);
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);

        SwapValidationResult result = await validator.ValidateAsync(printer.Id, 999, 1, CancellationToken.None);

        Assert.Equal(SwapValidationOutcome.ToolheadOutOfRange, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsOkWithNoExpected_WhenNoJobsAssigned()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(42, "PLA"), out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 42, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Ok, result.Status);
        Assert.Null(result.Expected);
        Assert.Equal("PLA", result.Scanned);
        Assert.Empty(result.AffectedJobs);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsOk_WhenMatchesLegacyRequiredMaterialType()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "legacy-single-material",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            RequiredMaterialType = "PETG",
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(7, "PETG"), out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 7, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Ok, result.Status);
        Assert.Equal("PETG", result.Expected);
        Assert.Equal("PETG", result.Scanned);
        Assert.Empty(result.AffectedJobs);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsMismatch_WhenLegacyRequiredMaterialDiffers()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        Guid jobId = Guid.NewGuid();
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = "mismatch-legacy",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            RequiredMaterialType = "PETG",
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(8, "PLA"), out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 8, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Mismatch, result.Status);
        Assert.Equal("PETG", result.Expected);
        Assert.Equal("PLA", result.Scanned);
        SwapValidationAffectedJobDto job = Assert.Single(result.AffectedJobs);
        Assert.Equal(jobId, job.JobId);
        Assert.Equal(0, job.Tool);
        Assert.Equal("PETG", job.ExpectedMaterial);
        Assert.Equal(PrintJobStatus.Assigned, job.Status);
    }

    [Fact]
    public async Task ValidateAsync_MultiToolMismatch_OnlyReportsJobsMismatchingSpecificToolhead()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db, toolheadCount: 2);

        Guid jobMismatchId = Guid.NewGuid();
        var jobMismatch = new PrintJob
        {
            Id = jobMismatchId,
            Name = "multi-mismatch",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", null, 20),
                new(1, "PETG", null, 5),
            },
        };
        Guid jobOnlyT0Id = Guid.NewGuid();
        var jobOnlyT0 = new PrintJob
        {
            Id = jobOnlyT0Id,
            Name = "single-tool-job",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 2,
            QueuedAt = DateTime.UtcNow.AddSeconds(1),
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", null, 15),
            },
        };
        db.PrintJobs.AddRange(jobMismatch, jobOnlyT0);
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(9, "PLA"), out _);

        // Scan a PLA spool onto T1 — jobMismatch requires PETG on T1; jobOnlyT0 has no T1 requirement.
        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 1, 9, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Mismatch, result.Status);
        Assert.Equal("PETG", result.Expected);
        SwapValidationAffectedJobDto affected = Assert.Single(result.AffectedJobs);
        Assert.Equal(jobMismatchId, affected.JobId);
        Assert.Equal(1, affected.Tool);
    }

    [Fact]
    public async Task ValidateAsync_ActiveJobTakesPrecedenceOverQueuedJob()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);

        var queued = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "queued",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            RequiredMaterialType = "PETG",
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        };
        var active = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "active",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Printing,
            RequiredMaterialType = "PLA",
            QueuePosition = 0,
            QueuedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.PrintJobs.AddRange(queued, active);
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(11, "PLA"), out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 11, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Ok, result.Status);
        Assert.Equal("PLA", result.Expected);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsUnknown_WhenSpoolmanCannotResolveSpool()
    {
        // B7: an unresolved/nonexistent Spoolman spool is UNKNOWN, not mismatch — the guided
        // PUT must not write/override on unknown.
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "expects-pla",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            RequiredMaterialType = "PLA",
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, spoolResult: null, out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 999, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Unknown, result.Status);
        Assert.Equal("PLA", result.Expected);
        Assert.Null(result.Scanned);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsUnknown_WhenScannedSpoolHasNoMaterialMetadata()
    {
        // B7: a requirement exists but the scanned spool carries no material metadata →
        // UNKNOWN (cannot compare), never mismatch.
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "expects-pla",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            RequiredMaterialType = "PLA",
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Spool resolves but has an empty material string.
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(5, string.Empty), out _);

        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 0, 5, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Unknown, result.Status);
        Assert.Equal("PLA", result.Expected);
        Assert.Empty(result.AffectedJobs);
    }

    [Fact]
    public void ExtractExpectedMaterial_PerToolWinsOverLegacyField()
    {
        var job = new PrintJob
        {
            RequiredMaterialType = "PLA",
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PETG", null, null),
            },
        };

        string? material = PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 0);

        Assert.Equal("PETG", material);
    }

    [Fact]
    public void ExtractExpectedMaterial_PerToolIsAuthoritative_NoLegacyFallback_WhenToolMissing()
    {
        // Authoritative per-tool contract (issue #710): if RequiredMaterialsPerTool is present
        // and the specific tool has no entry, the answer is "no requirement" — the caller must
        // NOT silently fall back to RequiredMaterialType for T0.
        var job = new PrintJob
        {
            RequiredMaterialType = "PLA",
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(1, "PETG", null, null), // only T1 has a requirement
            },
        };

        Assert.Null(PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 0));
        Assert.Equal("PETG", PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 1));
    }

    [Fact]
    public void ExtractExpectedMaterial_FallsBackToLegacyForTool0_WhenNoPerToolData()
    {
        var job = new PrintJob { RequiredMaterialType = "PLA" };

        Assert.Equal("PLA", PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 0));
        // Legacy field is single-tool only — do not leak to higher-index toolheads.
        Assert.Null(PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 1));
    }

    // ── MMU / U1 index-translation regressions (issue #710) ──

    [Fact]
    public async Task ValidateAsync_MmuPrinter_TranslatesGateIndexToGcodeTool()
    {
        // MMU printers store T0 as the physical hotend (Index=0) and gates 1..N as
        // MmuGate at Index=1..N. G-code arrays are 0-based, so gate Index=1 must map
        // to gcode Tool=0. Regression for the reviewer-identified indexing bug.
        await using AppDbContext db = CreateDb();
        Printer printer = SeedMmuPrinter(db, gateCount: 4);
        Guid jobId = Guid.NewGuid();
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = "mmu-multi",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", null, 10),   // tool 0 → gate 1
                new(1, "PETG", null, 5),   // tool 1 → gate 2
            },
        });
        await db.SaveChangesAsync();

        // Scan a PLA spool onto GATE Index=1 — expected material is tool 0 (PLA) → OK.
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);
        SwapValidationResultDto okResult = Body(await validator.ValidateAsync(printer.Id, 1, 1, CancellationToken.None));
        Assert.Equal(SwapValidationStatus.Ok, okResult.Status);
        Assert.Equal("PLA", okResult.Expected);

        // Scan a PLA spool onto GATE Index=2 — expected is tool 1 (PETG) → mismatch,
        // and the affected-job report must carry the G-code tool index (1), NOT the
        // gate Index (2).
        PrinterToolheadSwapValidator validator2 = CreateValidator(db, Spool(1, "PLA"), out _);
        SwapValidationResultDto mismatch = Body(await validator2.ValidateAsync(printer.Id, 2, 1, CancellationToken.None));
        Assert.Equal(SwapValidationStatus.Mismatch, mismatch.Status);
        Assert.Equal("PETG", mismatch.Expected);
        SwapValidationAffectedJobDto affected = Assert.Single(mismatch.AffectedJobs);
        Assert.Equal(jobId, affected.JobId);
        Assert.Equal(1, affected.Tool);
    }

    [Fact]
    public async Task ValidateAsync_MmuPrinter_GateIndex0IsUnmapped()
    {
        // MMU gate stored at Index=0 has no G-code tool mapping (the physical hotend
        // shared by an MMU is not itself a filament source). Treat as ToolheadNotFound (404)
        // rather than silently accepting the scan.
        await using AppDbContext db = CreateDb();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "broken-mmu",
            ServerUrl = "http://p.local",
        };
        // Deliberately synthesise the degenerate case: an MmuGate at Index=0.
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Bad Gate",
            ToolheadType = ToolheadType.MmuGate,
        });
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);
        SwapValidationResult result = await validator.ValidateAsync(printer.Id, 0, 1, CancellationToken.None);

        Assert.Equal(SwapValidationOutcome.ToolheadNotFound, result.Outcome);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ValidateAsync_UnmaterializedMmuGate_IsValidatedNotBlindlyBound()
    {
        // B2: an MMU-capable printer with NO materialized gate rows must still validate a
        // requested gate (index N → gcode tool N-1) rather than fall through to a blind
        // auto-create bind. Here the printer only has the physical hotend row; gate index 2
        // is synthesized and validated (tool 1 → PETG) → mismatch against a scanned PLA spool.
        await using AppDbContext db = CreateDb();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "capable-no-gates",
            ServerUrl = "http://p.local",
            MultiMaterial = true,
        };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "Hotend",
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
        });
        db.Printers.Add(printer);
        Guid jobId = Guid.NewGuid();
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = "needs-petg-on-tool1",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(1, "PETG", null, 5),
            },
        });
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);
        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 2, 1, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Mismatch, result.Status);
        Assert.Equal("PETG", result.Expected);
        SwapValidationAffectedJobDto affected = Assert.Single(result.AffectedJobs);
        Assert.Equal(1, affected.Tool);
    }

    [Fact]
    public async Task ValidateAsync_SnapmakerU1_LaneIndexIsIdentityWithGcodeTool()
    {
        // Snapmaker U1 stores each lane as ToolheadType.Physical at Index=0..N-1
        // (see MoonrakerSubscriptionService.PersistSnapmakerU1ToolheadStateAsync).
        // Non-MMU printers keep 1:1 indexing so a scan for lane Index=2 must be
        // validated against G-code tool 2.
        await using AppDbContext db = CreateDb();
        Printer printer = SeedU1Printer(db, laneCount: 4);
        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "u1-multi",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", null, 10),
                new(1, "PETG", null, 5),
                new(2, "TPU", null, 2),
                new(3, "ABS", null, 1),
            },
        });
        await db.SaveChangesAsync();

        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "TPU"), out _);
        SwapValidationResultDto result = Body(await validator.ValidateAsync(printer.Id, 2, 1, CancellationToken.None));

        Assert.Equal(SwapValidationStatus.Ok, result.Status);
        Assert.Equal("TPU", result.Expected);
    }

    // ── ToolheadIndexMapper unit tests ──

    [Fact]
    public void ToolheadIndexMapper_Physical_ReturnsIdentity()
    {
        Assert.Equal(0, ToolheadIndexMapper.ToGcodeToolIndex(new Toolhead { Index = 0, ToolheadType = ToolheadType.Physical }));
        Assert.Equal(3, ToolheadIndexMapper.ToGcodeToolIndex(new Toolhead { Index = 3, ToolheadType = ToolheadType.Physical }));
    }

    [Fact]
    public void ToolheadIndexMapper_MmuGate_SubtractsOne()
    {
        Assert.Equal(0, ToolheadIndexMapper.ToGcodeToolIndex(new Toolhead { Index = 1, ToolheadType = ToolheadType.MmuGate }));
        Assert.Equal(3, ToolheadIndexMapper.ToGcodeToolIndex(new Toolhead { Index = 4, ToolheadType = ToolheadType.MmuGate }));
    }

    [Fact]
    public void ToolheadIndexMapper_MmuGateAtIndex0_ReturnsNull()
    {
        // Degenerate case: gate Index=0 has no meaningful G-code tool mapping — the
        // physical hotend of an MMU printer is not a filament source.
        Assert.Null(ToolheadIndexMapper.ToGcodeToolIndex(new Toolhead { Index = 0, ToolheadType = ToolheadType.MmuGate }));
    }
}
