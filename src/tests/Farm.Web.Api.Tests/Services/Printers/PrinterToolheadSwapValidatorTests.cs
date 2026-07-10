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

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenPrinterMissing()
    {
        await using AppDbContext db = CreateDb();
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);

        SwapValidationResultDto? result = await validator.ValidateAsync(Guid.NewGuid(), 0, 1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenToolheadIndexOutOfRange()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db, toolheadCount: 1);
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(1, "PLA"), out _);

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 5, 1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsOkWithNoExpected_WhenNoJobsAssigned()
    {
        await using AppDbContext db = CreateDb();
        Printer printer = SeedPrinter(db);
        PrinterToolheadSwapValidator validator = CreateValidator(db, Spool(42, "PLA"), out _);

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 0, 42, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
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

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 0, 7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
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

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 0, 8, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
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
        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 1, 9, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
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

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 0, 11, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal("PLA", result.Expected);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNotOk_WhenSpoolmanCannotResolveSpool()
    {
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

        SwapValidationResultDto? result = await validator.ValidateAsync(printer.Id, 0, 999, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.Ok);
        Assert.Equal("PLA", result.Expected);
        Assert.Null(result.Scanned);
        Assert.NotNull(result.Reason);
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
    public void ExtractExpectedMaterial_FallsBackToLegacyForTool0_WhenNoPerToolData()
    {
        var job = new PrintJob { RequiredMaterialType = "PLA" };

        Assert.Equal("PLA", PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 0));
        // Legacy field is single-tool only — do not leak to higher-index toolheads.
        Assert.Null(PrinterToolheadSwapValidator.ExtractExpectedMaterial(job, 1));
    }
}
