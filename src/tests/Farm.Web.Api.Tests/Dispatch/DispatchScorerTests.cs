using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Tests for the auto-dispatch scoring algorithm.
/// Each test exercises the production <see cref="DispatchScorer"/> and verifies its
/// observable candidate score and factor breakdown.
/// </summary>
public class DispatchScorerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    // Reusable test data
    private readonly Guid _printerId = Guid.NewGuid();
    private readonly Guid _jobId = Guid.NewGuid();
    private readonly Guid _gcodeFileId = Guid.NewGuid();
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        SeedRootFolderNode();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedRootFolderNode()
    {
        var rootFolder = new FolderNode
        {
            Id = _folderId,
            Path = "/",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow
        };
        _context.Set<FolderNode>().Add(rootFolder);
        _context.SaveChanges();
    }

    #region Helper Methods

    private Printer CreateTestPrinter(
        string name = "Test Printer",
        string? currentMaterial = "PLA",
        double? maxBuildX = 250,
        double? maxBuildY = 210,
        double? maxBuildZ = 210,
        bool hasEnclosure = false,
        bool isAvailable = true)
    {
        var printer = new PrinterBuilder()
            .WithId(_printerId)
            .WithName(name)
            .Build();

        printer.CurrentMaterial = currentMaterial;
        printer.MaxBuildVolumeX = maxBuildX;
        printer.MaxBuildVolumeY = maxBuildY;
        printer.MaxBuildVolumeZ = maxBuildZ;
        printer.HasEnclosure = hasEnclosure;
        printer.IsAvailable = isAvailable;
        printer.IsEnabled = true;

        return printer;
    }

    private NozzleMaterial GetOrCreateNozzleMaterial(NozzleType nozzleType)
    {
        NozzleMaterial? material = _context.NozzleMaterials.Local.FirstOrDefault(m => m.Name == nozzleType.ToString())
            ?? _context.NozzleMaterials.FirstOrDefault(m => m.Name == nozzleType.ToString());
        if (material is not null)
        {
            return material;
        }

        material = new NozzleMaterial
        {
            Id = Guid.NewGuid(),
            Name = nozzleType.ToString(),
            IsHardened = nozzleType is NozzleType.HardenedSteel or NozzleType.TungstenCarbide or NozzleType.Abrasive,
            DefaultMaxTemp = 500,
            IsBuiltIn = true
        };
        _context.NozzleMaterials.Add(material);
        return material;
    }

    private Toolhead CreateToolhead(
        Guid printerId,
        NozzleType nozzleType = NozzleType.Brass,
        double nozzleDiameter = 0.4,
        string[]? supportedMaterials = null,
        bool isPrimary = true)
    {
        NozzleMaterial nozzleMaterial = GetOrCreateNozzleMaterial(nozzleType);
        var nozzleModel = new NozzleModelDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"{nozzleType} 0.{(int)(nozzleDiameter * 10)}",
            Diameter = nozzleDiameter,
            NozzleMaterialId = nozzleMaterial.Id,
            NozzleMaterial = nozzleMaterial
        };
        _context.NozzleModelDefinitions.Add(nozzleModel);

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Extruder 1",
            Index = 0,
            IsPrimary = isPrimary,
            NozzleModelId = nozzleModel.Id,
            NozzleModel = nozzleModel,
            NozzleDiameter = nozzleDiameter,
            SupportedMaterials = supportedMaterials ?? ["PLA", "PETG", "ABS"],
            UpdatedAt = DateTime.UtcNow
        };

        return toolhead;
    }

    private PrintJob CreateTestJob(
        string? requiredMaterial = "PLA",
        decimal? requiredNozzleDiameter = 0.4m,
        Guid[]? preferredPrinterIds = null,
        Guid[]? excludedPrinterIds = null,
        Guid? jobId = null,
        Guid? gcodeFileId = null)
    {
        var gcodeFile = new GcodeFile
        {
            Id = gcodeFileId ?? _gcodeFileId,
            Name = "test-print.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = Guid.NewGuid().ToString("N"),
            RequiredMaterial = requiredMaterial,
            RequiredNozzleDiameter = requiredNozzleDiameter.HasValue ? (double)requiredNozzleDiameter.Value : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow
        };

        var job = new PrintJob
        {
            Id = jobId ?? _jobId,
            Name = "Test Print Job",
            GcodeFileId = gcodeFile.Id,
            GcodeFile = gcodeFile,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            RequiredMaterialType = requiredMaterial,
            RequiredNozzleDiameter = requiredNozzleDiameter,
            PreferredPrinterIds = preferredPrinterIds,
            ExcludedPrinterIds = excludedPrinterIds,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };

        return job;
    }

    private async Task<DispatchScore> ScorePrinterAsync(Printer printer, PrintJob job)
    {
        EnsurePrinterReferences(printer);
        await _context.SaveChangesAsync();
        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);
        return scores.Should().ContainSingle().Subject;
    }

    private void EnsurePrinterReferences(Printer printer)
    {
        Guid manufacturerId = printer.ManufacturerId;

        Manufacturer manufacturer = _context.Manufacturers.Local
            .FirstOrDefault(m => m.Id == manufacturerId)
            ?? new Manufacturer { Id = manufacturerId, Name = $"{printer.Name} manufacturer" };
        if (_context.Entry(manufacturer).State == EntityState.Detached)
        {
            _context.Manufacturers.Add(manufacturer);
        }

        foreach (Toolhead toolhead in printer.Toolheads)
        {
            if (toolhead.NozzleModel is { ManufacturerId: var nozzleManufacturerId } nozzle
                && nozzleManufacturerId == Guid.Empty)
            {
                nozzle.ManufacturerId = manufacturerId;
                nozzle.Manufacturer = manufacturer;
            }
        }

        if (printer.ModelId is Guid modelId)
        {
            PrinterModel model = _context.PrinterModels.Local
                .FirstOrDefault(m => m.Id == modelId)
                ?? new PrinterModel
                {
                    Id = modelId,
                    ManufacturerId = manufacturerId,
                    Manufacturer = manufacturer,
                    Name = $"{printer.Name} model"
                };
            if (_context.Entry(model).State == EntityState.Detached)
            {
                _context.PrinterModels.Add(model);
            }

            printer.Model = model;
        }

        printer.Manufacturer = manufacturer;
    }

    #endregion

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrintersForJobAsync_ToolheadLoadedMaterialAndColor_MatchesU1PhysicalLane()
    {
        Printer printer = CreateTestPrinter(currentMaterial: null);
        printer.MultiMaterial = true;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Dispatch U1 Mfg" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Dispatch U1" };
        printer.ManufacturerId = manufacturer.Id;
        printer.ModelId = model.Id;

        Toolhead t0 = CreateToolhead(printer.Id, isPrimary: true);
        t0.NozzleModel!.ManufacturerId = manufacturer.Id;
        t0.Name = "T0";
        t0.CurrentMaterial = "PLA";
        t0.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(t0);

        Toolhead t1 = CreateToolhead(printer.Id, isPrimary: false);
        t1.NozzleModel!.ManufacturerId = manufacturer.Id;
        t1.Index = 1;
        t1.Name = "T1";
        printer.Toolheads.Add(t1);

        Toolhead t2 = CreateToolhead(printer.Id, isPrimary: false);
        t2.NozzleModel!.ManufacturerId = manufacturer.Id;
        t2.Index = 2;
        t2.Name = "T2";
        t2.CurrentMaterial = "ASA";
        t2.CurrentFilamentColor = "#0000FF";
        printer.Toolheads.Add(t2);

        Toolhead t3 = CreateToolhead(printer.Id, isPrimary: false);
        t3.NozzleModel!.ManufacturerId = manufacturer.Id;
        t3.Index = 3;
        t3.Name = "T3";
        t3.CurrentMaterial = "TPU";
        t3.CurrentFilamentColor = "#FFFF00";
        printer.Toolheads.Add(t3);

        PrintJob job = CreateTestJob(requiredMaterial: "ASA");
        job.FilamentColor = "#0000FF";

        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);

        DispatchScore score = scores.Should().ContainSingle().Subject;
        score.Eliminated.Should().BeFalse();
        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(100);
        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrintersForJobAsync_ToolheadLoadedMaterialAndColor_MatchesNonU1MultiToolheadPrinter()
    {
        Printer printer = CreateTestPrinter(name: "Generic IDEX", currentMaterial: null);
        printer.MultiMaterial = true;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Dispatch IDEX Mfg" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Generic IDEX" };
        printer.ManufacturerId = manufacturer.Id;
        printer.ModelId = model.Id;

        Toolhead t0 = CreateToolhead(printer.Id, isPrimary: true);
        t0.NozzleModel!.ManufacturerId = manufacturer.Id;
        t0.Name = "Left Toolhead";
        t0.CurrentMaterial = "PLA";
        t0.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(t0);

        Toolhead t1 = CreateToolhead(printer.Id, isPrimary: false);
        t1.NozzleModel!.ManufacturerId = manufacturer.Id;
        t1.Index = 1;
        t1.Name = "Right Toolhead";
        t1.CurrentMaterial = "ASA";
        t1.CurrentFilamentColor = "#0000FF";
        printer.Toolheads.Add(t1);

        PrintJob job = CreateTestJob(requiredMaterial: "ASA");
        job.FilamentColor = "#0000FF";

        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);

        DispatchScore score = scores.Should().ContainSingle().Subject;
        score.Eliminated.Should().BeFalse();
        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(100);
        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(100);
    }

    [Theory]
    [Trait("Category", "Dispatch")]
    [InlineData(0.6, 0.4, 0.6, 100)] // stored wins over a conflicting nozzle model
    [InlineData(0.6, 0.4, 0.4, 0)] // model diameter alone never satisfies the job
    [InlineData(null, 0.4, 0.4, 50)] // no stored data -> neutral, not a model-derived match
    public async Task ScorePrintersForJobAsync_NozzleDiameter_UsesStoredToolheadDiameter(
        double? storedDiameter, double modelDiameter, double requiredDiameter, double expectedScore)
    {
        Printer printer = CreateTestPrinter();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Nozzle Stored Mfg" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Nozzle Stored Model" };
        printer.ManufacturerId = manufacturer.Id;
        printer.ModelId = model.Id;
        Toolhead toolhead = CreateToolhead(printer.Id, nozzleDiameter: modelDiameter);
        toolhead.NozzleModel!.ManufacturerId = manufacturer.Id;
        toolhead.NozzleDiameter = storedDiameter;
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredNozzleDiameter: (decimal)requiredDiameter);

        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);

        DispatchScore score = scores.Should().ContainSingle().Subject;
        score.ScoreBreakdown["NozzleDiameter"].Score.Should().Be(expectedScore);
    }

    // =========================================================================
    // MATERIAL MATCH FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_ExactMaterialMatch_Returns100()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "PLA");
        printer.Toolheads.Add(CreateToolhead(printer.Id, supportedMaterials: ["PLA", "PETG"]));
        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(100);
        score.Eliminated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_WrongMaterial_Eliminates()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "ABS");
        printer.Toolheads.Add(CreateToolhead(printer.Id, supportedMaterials: ["ABS"]));
        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(0);
        score.ScoreBreakdown["MaterialMatch"].EliminationReason.Should().Contain("PLA");
        score.Eliminated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_MaterialSupportedButNotLoaded_Returns50()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "PETG");
        printer.Toolheads.Add(CreateToolhead(printer.Id, supportedMaterials: ["PLA", "PETG", "ABS"]));
        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(50);
        score.Eliminated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_JobWithNoMaterialRequirement_ReturnsNeutral70()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "PETG");
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob(requiredMaterial: null);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["MaterialMatch"].Score.Should().Be(70);
    }

    // =========================================================================
    // NOZZLE DIAMETER FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_NozzleDiameterExactMatch_Returns100()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id, nozzleDiameter: 0.4));
        PrintJob job = CreateTestJob(requiredNozzleDiameter: 0.4m);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["NozzleDiameter"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_NozzleTooSmall_Eliminates()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id, nozzleDiameter: 0.4));
        PrintJob job = CreateTestJob(requiredNozzleDiameter: 0.6m);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["NozzleDiameter"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue();
    }

    // =========================================================================
    // BUILD VOLUME FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_BuildVolumeExceeded_ReturnsSoftScore20()
    {
        Printer printer = CreateTestPrinter(maxBuildX: 250, maxBuildY: 210, maxBuildZ: 210);
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Gcode target manufacturer" };
        var targetModel = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "300x200x200 target",
            MaxX = 300,
            MaxY = 200,
            MaxZ = 200
        };
        job.GcodeFile!.PrinterModelId = targetModel.Id;
        job.GcodeFile.PrinterModel = targetModel;
        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(targetModel);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["BuildVolume"].Score.Should().Be(20);
        score.Eliminated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_BuildVolumeFits_Returns100()
    {
        Printer printer = CreateTestPrinter(maxBuildX: 250, maxBuildY: 210, maxBuildZ: 210);
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Gcode fit manufacturer" };
        var targetModel = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "200x200x200 target",
            MaxX = 200,
            MaxY = 200,
            MaxZ = 200
        };
        job.GcodeFile!.PrinterModelId = targetModel.Id;
        job.GcodeFile.PrinterModel = targetModel;
        _context.Manufacturers.Add(manufacturer);
        _context.PrinterModels.Add(targetModel);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["BuildVolume"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_GcodeFileWithoutTargetModel_ReturnsNeutral70()
    {
        Printer printer = CreateTestPrinter(maxBuildX: 250, maxBuildY: 210, maxBuildZ: 210);
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["BuildVolume"].Score.Should().Be(70);
    }

    // =========================================================================
    // ABRASIVE MATERIAL + NOZZLE HARDNESS TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_AbrasiveMaterial_BrassNozzle_Eliminates()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "CF-PLA");
        printer.Toolheads.Add(CreateToolhead(printer.Id, nozzleType: NozzleType.Brass));
        PrintJob job = CreateTestJob(requiredMaterial: "CF-PLA");
        _context.FilamentTypes.Add(new FilamentType
        {
            Id = Guid.NewGuid(),
            Name = "CF-PLA",
            IsAbrasive = true,
            IsActive = true
        });

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_AbrasiveMaterial_HardenedNozzle_Returns100()
    {
        Printer printer = CreateTestPrinter(currentMaterial: "CF-PLA");
        printer.Toolheads.Add(CreateToolhead(printer.Id, nozzleType: NozzleType.HardenedSteel));
        PrintJob job = CreateTestJob(requiredMaterial: "CF-PLA");
        _context.FilamentTypes.Add(new FilamentType
        {
            Id = Guid.NewGuid(),
            Name = "CF-PLA",
            IsAbrasive = true,
            IsActive = true
        });

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(100);
        score.Eliminated.Should().BeFalse();
    }

    // =========================================================================
    // ENCLOSURE REQUIREMENT TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_EnclosureRequired_NoEnclosure_Eliminates()
    {
        Printer printer = CreateTestPrinter(hasEnclosure: false);
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob(requiredMaterial: "ABS");
        _context.FilamentTypes.Add(new FilamentType
        {
            Id = Guid.NewGuid(),
            Name = "ABS",
            NeedsEnclosure = true,
            IsActive = true
        });

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["Enclosure"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_EnclosureRequired_HasEnclosure_Returns100()
    {
        Printer printer = CreateTestPrinter(hasEnclosure: true);
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob(requiredMaterial: "ABS");
        _context.FilamentTypes.Add(new FilamentType
        {
            Id = Guid.NewGuid(),
            Name = "ABS",
            NeedsEnclosure = true,
            IsActive = true
        });

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["Enclosure"].Score.Should().Be(100);
        score.Eliminated.Should().BeFalse();
    }

    // =========================================================================
    // PRINTER PREFERENCE TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_PreferredPrinter_Returns100()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob(preferredPrinterIds: [printer.Id]);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["Preferred"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_ExcludedPrinter_Eliminates()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob(excludedPrinterIds: [printer.Id]);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["Preferred"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue();
    }

    // =========================================================================
    // QUEUE DEPTH TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_QueueEmpty_Returns100()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["QueueDepth"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_QueueHeavy_Returns10()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();
        List<PrintJob> queuedJobs = Enumerable.Range(0, 6)
            .Select(_ => CreateTestJob(
                requiredMaterial: null,
                requiredNozzleDiameter: null,
                jobId: Guid.NewGuid(),
                gcodeFileId: Guid.NewGuid()))
            .ToList();
        for (int index = 0; index < queuedJobs.Count; index++)
        {
            queuedJobs[index].AssignedPrinterId = printer.Id;
            queuedJobs[index].QueuePosition = index + 1;
        }
        _ = await ScorePrinterAsync(printer, job);
        _context.PrintJobs.AddRange(queuedJobs);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);
        DispatchScore score = scores.Should().ContainSingle().Subject;

        score.ScoreBreakdown["QueueDepth"].Score.Should().Be(10);
    }

    // =========================================================================
    // EDGE CASE TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinter_PrinterWithNoToolheads_UsesNeutralNozzleScore()
    {
        Printer printer = CreateTestPrinter();
        PrintJob job = CreateTestJob(requiredMaterial: "PLA", requiredNozzleDiameter: 0.4m);

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["NozzleDiameter"].Score.Should().Be(50);
        score.Eliminated.Should().BeFalse();
    }

    // =========================================================================
    // COLOR MATCH FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScoreColorMatch_ExactHexMatch_Returns100()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(toolhead);
        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(100);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScoreColorMatch_SameColorFamily_Returns80()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#CC0000";
        printer.Toolheads.Add(toolhead);
        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(80);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScoreColorMatch_DifferentFamily_Returns20()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#0000FF";
        printer.Toolheads.Add(toolhead);
        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(20);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScoreColorMatch_NoJobColor_ReturnsNeutral50()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(toolhead);
        PrintJob job = CreateTestJob();
        job.FilamentColor = null;

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(50);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScoreColorMatch_NoPrinterColor_ReturnsNeutral50()
    {
        Printer printer = CreateTestPrinter();
        printer.Toolheads.Add(CreateToolhead(printer.Id));
        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        DispatchScore score = await ScorePrinterAsync(printer, job);

        score.ScoreBreakdown["ColorMatch"].Score.Should().Be(50);
    }
}
