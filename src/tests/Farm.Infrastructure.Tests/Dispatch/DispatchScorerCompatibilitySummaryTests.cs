using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// Issue #3280: the queue compatibility summary must be derived from the dispatch scorer's
/// own eligibility rules, so its counts always agree with fleet scoring.
/// </summary>
public class DispatchScorerCompatibilitySummaryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerCompatibilitySummaryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
        _context.Set<FolderNode>().Add(new FolderNode
        {
            Id = _folderId,
            Path = "/",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task Summarize_NoPrinterSupportsMaterial_ReportsZeroCompatible()
    {
        _context.Printers.AddRange(
            CreatePrinter("A", "PLA", supportedMaterials: ["PLA"]),
            CreatePrinter("B", "PETG", supportedMaterials: ["PETG"]));
        PrintJob job = CreateJob("Nylon job", "NYLON");
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        PrinterCompatibilitySummaryDto summary = await SummarizeSingleAsync(job.Id);

        summary.EvaluatedPrinterCount.Should().Be(2);
        summary.CompatiblePrinterCount.Should().Be(0);
        summary.ReadyPrinterCount.Should().Be(0);
        summary.Basis.RequiredMaterial.Should().Be("NYLON");
        summary.ExcludedBy.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PrinterCompatibilityExclusionDto { Constraint = "MaterialMatch", PrinterCount = 2 });
        await AssertAgreesWithFleetScoringAsync(job.Id, summary);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task Summarize_SomePrintersCompatible_CountsOnlyEligibleAndSeparatesBusy()
    {
        _context.Printers.AddRange(
            CreatePrinter("ABS ready", "ABS"),
            CreatePrinter("ABS busy", "ABS", isAvailable: false),
            CreatePrinter("PLA only", "PLA", supportedMaterials: ["PLA"]),
            CreatePrinter("Disabled", "ABS", isEnabled: false));
        PrintJob job = CreateJob("ABS job", "ABS");
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        PrinterCompatibilitySummaryDto summary = await SummarizeSingleAsync(job.Id);

        summary.EvaluatedPrinterCount.Should().Be(3, "disabled printers are never dispatch candidates");
        summary.CompatiblePrinterCount.Should().Be(2, "a busy printer can still run the job once free");
        summary.ReadyPrinterCount.Should().Be(1);
        summary.ExcludedBy.Select(e => e.Constraint).Should().NotContain("Availability");
        await AssertAgreesWithFleetScoringAsync(job.Id, summary);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task Summarize_AllPrintersCompatible_ReportsFullFleet()
    {
        _context.Printers.AddRange(
            CreatePrinter("A", "PLA"),
            CreatePrinter("B", "PETG"),
            CreatePrinter("C", "ABS"));
        PrintJob job = CreateJob("PLA job", "PLA");
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        PrinterCompatibilitySummaryDto summary = await SummarizeSingleAsync(job.Id);

        summary.EvaluatedPrinterCount.Should().Be(3);
        summary.CompatiblePrinterCount.Should().Be(3);
        summary.ReadyPrinterCount.Should().Be(3);
        summary.ExcludedBy.Should().BeEmpty();
        summary.Basis.BuildVolumeEnforced.Should().BeFalse();
        await AssertAgreesWithFleetScoringAsync(job.Id, summary);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task Summarize_AssignedOrNonQueuedJobs_AreOmitted()
    {
        Printer printer = CreatePrinter("A", "PLA");
        _context.Printers.Add(printer);
        PrintJob assigned = CreateJob("Assigned", "PLA");
        assigned.AssignedPrinterId = printer.Id;
        PrintJob printing = CreateJob("Printing", "PLA");
        printing.Status = PrintJobStatus.Printing;
        PrintJob queued = CreateJob("Queued", "PLA");
        _context.PrintJobs.AddRange(assigned, printing, queued);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);
        IReadOnlyDictionary<Guid, PrinterCompatibilitySummaryDto> result =
            await scorer.SummarizeCompatibilityAsync([assigned.Id, printing.Id, queued.Id, Guid.NewGuid()]);

        result.Keys.Should().BeEquivalentTo([queued.Id]);
    }

    private async Task<PrinterCompatibilitySummaryDto> SummarizeSingleAsync(Guid jobId)
    {
        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);
        IReadOnlyDictionary<Guid, PrinterCompatibilitySummaryDto> result = await scorer.SummarizeCompatibilityAsync([jobId]);
        result.Should().ContainKey(jobId);
        return result[jobId];
    }

    private async Task AssertAgreesWithFleetScoringAsync(Guid jobId, PrinterCompatibilitySummaryDto summary)
    {
        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);
        List<DispatchScore> fleet = await scorer.ScorePrintersForJobAsync(jobId);

        fleet.Count.Should().Be(summary.EvaluatedPrinterCount);
        fleet.Count(s => !s.Eliminated).Should().Be(summary.ReadyPrinterCount);
        fleet.Count(s => s.ScoreBreakdown.All(f =>
                f.Key == "Availability" || f.Value is not { IsHardRequirement: true, Score: 0 }))
            .Should().Be(summary.CompatiblePrinterCount);
    }

    private Printer CreatePrinter(
        string name,
        string? currentMaterial,
        bool isEnabled = true,
        bool isAvailable = true,
        string[]? supportedMaterials = null)
    {
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"{name} Mfg" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = $"{name} Model" };
        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(model);

        Printer printer = new PrinterBuilder()
            .WithId(Guid.NewGuid())
            .WithName(name)
            .WithServerUrl($"http://{Guid.NewGuid():N}.test.local")
            .Build();

        printer.CurrentMaterial = currentMaterial;
        printer.MaxBuildVolumeX = 250;
        printer.MaxBuildVolumeY = 210;
        printer.MaxBuildVolumeZ = 210;
        printer.HasEnclosure = false;
        printer.IsAvailable = isAvailable;
        printer.IsEnabled = isEnabled;
        printer.ManufacturerId = manufacturer.Id;
        printer.ModelId = model.Id;

        NozzleMaterial? nozzleMaterial = _context.NozzleMaterials.Local.FirstOrDefault(m => m.Name == nameof(NozzleType.Brass));
        if (nozzleMaterial is null)
        {
            nozzleMaterial = new NozzleMaterial { Id = Guid.NewGuid(), Name = nameof(NozzleType.Brass), IsBuiltIn = true, DefaultMaxTemp = 260 };
            _context.NozzleMaterials.Add(nozzleMaterial);
        }

        var nozzleModel = new NozzleModelDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Brass 0.4",
            Diameter = 0.4,
            NozzleMaterialId = nozzleMaterial.Id,
            NozzleMaterial = nozzleMaterial,
            ManufacturerId = manufacturer.Id,
        };
        _context.NozzleModelDefinitions.Add(nozzleModel);

        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder 1",
            Index = 0,
            IsPrimary = true,
            NozzleModelId = nozzleModel.Id,
            NozzleModel = nozzleModel,
            CurrentMaterial = currentMaterial,
            SupportedMaterials = supportedMaterials ?? ["PLA", "PETG", "ABS"],
            UpdatedAt = DateTime.UtcNow,
        });

        return printer;
    }

    private PrintJob CreateJob(string name, string? requiredMaterial)
    {
        Guid gcodeFileId = Guid.NewGuid();
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            GcodeFileId = gcodeFileId,
            GcodeFile = new GcodeFile
            {
                Id = gcodeFileId,
                Name = $"{name}.gcode",
                FileName = $"{Guid.NewGuid()}.gcode",
                FilePath = "/gcode/",
                FolderId = _folderId,
                FileHash = Guid.NewGuid().ToString("N"),
                RequiredMaterial = requiredMaterial,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                UploadedAt = DateTime.UtcNow,
            },
            Status = PrintJobStatus.Queued,
            Priority = 1,
            RequiredMaterialType = requiredMaterial,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
    }
}
