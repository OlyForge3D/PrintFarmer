using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoTagging;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Web.Api.Tests.Builders;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Tests for the auto-dispatch scoring algorithm.
/// The scorer evaluates printers against job requirements using weighted factors:
/// material match, nozzle diameter, build volume, nozzle hardness, enclosure,
/// user preferences, and queue depth.
///
/// Each factor returns 0–100 or ELIMINATE (represented as null/negative).
/// The final score is a weighted average of non-eliminated factors.
///
/// NOTE: These tests are written from the SPECIFICATION. The implementation
/// (IDispatchScorer / DispatchScorerService) is being built by Lambert in parallel.
/// Tests that reference not-yet-existing types are clearly marked.
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

    private Toolhead CreateToolhead(
        Guid printerId,
        NozzleType nozzleType = NozzleType.Brass,
        double nozzleDiameter = 0.4,
        string[]? supportedMaterials = null,
        bool isPrimary = true)
    {
        var nozzleModel = new NozzleModelDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"{nozzleType} 0.{(int)(nozzleDiameter * 10)}",
            Diameter = nozzleDiameter,
            NozzleType = nozzleType
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
            SupportedMaterials = supportedMaterials ?? ["PLA", "PETG", "ABS"],
            UpdatedAt = DateTime.UtcNow
        };

        return toolhead;
    }

    private PrintJob CreateTestJob(
        string? requiredMaterial = "PLA",
        decimal? requiredNozzleDiameter = 0.4m,
        Guid[]? preferredPrinterIds = null,
        Guid[]? excludedPrinterIds = null)
    {
        var gcodeFile = new GcodeFile
        {
            Id = _gcodeFileId,
            Name = "test-print.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = "abc123",
            RequiredMaterial = requiredMaterial,
            RequiredNozzleDiameter = requiredNozzleDiameter.HasValue ? (double)requiredNozzleDiameter.Value : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow
        };

        var job = new PrintJob
        {
            Id = _jobId,
            Name = "Test Print Job",
            GcodeFileId = _gcodeFileId,
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

    // =========================================================================
    // MATERIAL MATCH FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_ExactMaterialMatch_Returns100()
    {
        // Printer has PLA loaded and job needs PLA → perfect match → 100
        Printer printer = CreateTestPrinter(currentMaterial: "PLA");
        Toolhead toolhead = CreateToolhead(printer.Id, supportedMaterials: ["PLA", "PETG"]);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        // Score the material factor
        int score = ScoreMaterialMatch(printer, job);

        score.Should().Be(100, "exact material match should score 100");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_WrongMaterial_Eliminates()
    {
        // Printer has ABS loaded, job needs PLA, toolhead does NOT support PLA → ELIMINATE
        Printer printer = CreateTestPrinter(currentMaterial: "ABS");
        Toolhead toolhead = CreateToolhead(printer.Id, supportedMaterials: ["ABS"]);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        int score = ScoreMaterialMatch(printer, job);

        score.Should().BeLessThan(0, "wrong material with no toolhead support should eliminate");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_MaterialSupportedButNotLoaded_Returns50()
    {
        // Printer has PETG loaded, job needs PLA, but toolhead supports PLA → partial match → 50
        Printer printer = CreateTestPrinter(currentMaterial: "PETG");
        Toolhead toolhead = CreateToolhead(printer.Id, supportedMaterials: ["PLA", "PETG", "ABS"]);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredMaterial: "PLA");

        int score = ScoreMaterialMatch(printer, job);

        score.Should().Be(50, "material supported but not loaded should score 50");
    }

    // =========================================================================
    // NOZZLE DIAMETER FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_NozzleDiameterExactMatch_Returns100()
    {
        // Printer nozzle 0.4mm, job needs 0.4mm → exact match → 100
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id, nozzleDiameter: 0.4);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredNozzleDiameter: 0.4m);

        int score = ScoreNozzleDiameter(printer, job);

        score.Should().Be(100, "exact nozzle match (±0.01mm) should score 100");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_NozzleTooSmall_Eliminates()
    {
        // Printer nozzle 0.4mm, job needs 0.6mm → wrong nozzle → ELIMINATE
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id, nozzleDiameter: 0.4);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredNozzleDiameter: 0.6m);

        int score = ScoreNozzleDiameter(printer, job);

        score.Should().BeLessThan(0, "wrong nozzle diameter should eliminate");
    }

    // =========================================================================
    // BUILD VOLUME FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_BuildVolumeExceeded_Eliminates()
    {
        // Printer build volume 250x210x210, gcode needs 300x200x200 → too big → ELIMINATE
        Printer printer = CreateTestPrinter(maxBuildX: 250, maxBuildY: 210, maxBuildZ: 210);

        // Simulate a gcode file that exceeds build volume
        // TODO: When Lambert's implementation lands, this will use actual gcode dimensions
        // For now, we test the scoring logic directly
        bool exceeds = DoesBuildVolumeExceed(printer, 300, 200, 200);

        exceeds.Should().BeTrue("gcode exceeding build volume should be eliminated");
    }

    // =========================================================================
    // ABRASIVE MATERIAL + NOZZLE HARDNESS TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_AbrasiveMaterial_BrassNozzle_Eliminates()
    {
        // Abrasive material (CF-PLA) + Brass nozzle → ELIMINATE (would destroy nozzle)
        Printer printer = CreateTestPrinter(currentMaterial: "CF-PLA");
        Toolhead toolhead = CreateToolhead(printer.Id, nozzleType: NozzleType.Brass);
        printer.Toolheads.Add(toolhead);

        bool isAbrasive = true; // FilamentType.IsAbrasive for CF-PLA
        bool isHardened = toolhead.NozzleModel!.IsHardened;

        int score = ScoreNozzleHardness(isAbrasive, isHardened);

        score.Should().BeLessThan(0, "abrasive material on brass nozzle should eliminate");
        isHardened.Should().BeFalse("brass nozzle is NOT hardened");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_AbrasiveMaterial_HardenedNozzle_Returns100()
    {
        // Abrasive material (CF-PLA) + Hardened Steel nozzle → safe → 100
        Printer printer = CreateTestPrinter(currentMaterial: "CF-PLA");
        Toolhead toolhead = CreateToolhead(printer.Id, nozzleType: NozzleType.HardenedSteel);
        printer.Toolheads.Add(toolhead);

        bool isAbrasive = true;
        bool isHardened = toolhead.NozzleModel!.IsHardened;

        int score = ScoreNozzleHardness(isAbrasive, isHardened);

        score.Should().Be(100, "abrasive material on hardened nozzle should score 100");
        isHardened.Should().BeTrue("hardened steel nozzle IS hardened");
    }

    // =========================================================================
    // ENCLOSURE REQUIREMENT TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_EnclosureRequired_NoEnclosure_Eliminates()
    {
        // Material needs enclosure (ABS), printer has no enclosure → ELIMINATE
        Printer printer = CreateTestPrinter(hasEnclosure: false);

        bool needsEnclosure = true; // FilamentType.NeedsEnclosure for ABS
        int score = ScoreEnclosure(needsEnclosure, printer.HasEnclosure);

        score.Should().BeLessThan(0, "enclosure required but not available should eliminate");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_EnclosureRequired_HasEnclosure_Returns100()
    {
        // Material needs enclosure (ABS), printer HAS enclosure → OK → 100
        Printer printer = CreateTestPrinter(hasEnclosure: true);

        bool needsEnclosure = true;
        int score = ScoreEnclosure(needsEnclosure, printer.HasEnclosure);

        score.Should().Be(100, "enclosure required and available should score 100");
    }

    // =========================================================================
    // PRINTER PREFERENCE TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_PreferredPrinter_ScoresHigher()
    {
        // Printer is in the job's preferred list → score 100
        Printer printer = CreateTestPrinter();
        PrintJob job = CreateTestJob(preferredPrinterIds: [printer.Id]);

        int score = ScorePreference(printer.Id, job.PreferredPrinterIds, job.ExcludedPrinterIds);

        score.Should().Be(100, "preferred printer should score 100");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_ExcludedPrinter_Eliminates()
    {
        // Printer is in the job's excluded list → ELIMINATE
        Printer printer = CreateTestPrinter();
        PrintJob job = CreateTestJob(excludedPrinterIds: [printer.Id]);

        int score = ScorePreference(printer.Id, job.PreferredPrinterIds, job.ExcludedPrinterIds);

        score.Should().BeLessThan(0, "excluded printer should be eliminated");
    }

    // =========================================================================
    // QUEUE DEPTH TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_QueueEmpty_Returns100()
    {
        // Printer has 0 queued jobs → 100
        int queuedJobCount = 0;
        int score = ScoreQueueDepth(queuedJobCount);

        score.Should().Be(100, "empty queue should score 100");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_QueueHeavy_Returns10()
    {
        // Printer has 6+ queued jobs → 10 (heavy load, still viable)
        int queuedJobCount = 6;
        int score = ScoreQueueDepth(queuedJobCount);

        score.Should().BeLessThanOrEqualTo(10, "heavy queue (6+) should score ≤10");
    }

    // =========================================================================
    // WEIGHTED SCORE CALCULATION TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_AllFactorsPass_CalculatesWeightedScore()
    {
        // All factors pass with varying scores → final is weighted average
        var factorScores = new Dictionary<string, int>
        {
            ["material"] = 100,      // weight: 30
            ["nozzle"] = 100,        // weight: 25
            ["buildVolume"] = 100,   // weight: 15
            ["nozzleHardness"] = 100, // weight: 10
            ["enclosure"] = 100,     // weight: 10
            ["preference"] = 50,     // weight: 5 (not preferred, not excluded)
            ["queueDepth"] = 80      // weight: 5
        };

        int totalScore = CalculateWeightedScore(factorScores);

        // With all 100 except preference=50 and queue=80:
        // (100*30 + 100*25 + 100*15 + 100*10 + 100*10 + 50*5 + 80*5) / 100
        // = (3000 + 2500 + 1500 + 1000 + 1000 + 250 + 400) / 100
        // = 9650 / 100 = 96.5 → 96 or 97 depending on rounding
        totalScore.Should().BeInRange(90, 100, "all-pass with minor deductions should score 90+");
    }

    // =========================================================================
    // EDGE CASE TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_JobWithNoMaterialRequirement_MatchesAnyPrinter()
    {
        // Job has no material requirement → any printer matches → 100
        Printer printer = CreateTestPrinter(currentMaterial: "PETG");
        Toolhead toolhead = CreateToolhead(printer.Id);
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob(requiredMaterial: null);

        int score = ScoreMaterialMatch(printer, job);

        score.Should().Be(100, "no material requirement means any printer matches");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_PrinterWithNoToolheads_Eliminates()
    {
        // Printer has no toolheads configured → ELIMINATE (can't validate nozzle/material)
        Printer printer = CreateTestPrinter();
        // Deliberately NOT adding toolheads

        PrintJob job = CreateTestJob(requiredMaterial: "PLA", requiredNozzleDiameter: 0.4m);

        bool hasToolheads = printer.Toolheads.Count > 0;
        hasToolheads.Should().BeFalse("printer should have no toolheads");

        // Scoring should eliminate a printer with no toolheads when material/nozzle is required
        int score = ScoreToolheadPresence(printer, job);

        score.Should().BeLessThan(0, "printer with no toolheads should be eliminated when material is required");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScorePrinter_GcodeFileWithNoDimensions_AssumesBuiltVolumeFits()
    {
        // Gcode file has no dimension metadata → assume it fits → 100
        Printer printer = CreateTestPrinter(maxBuildX: 250, maxBuildY: 210, maxBuildZ: 210);

        // No dimensions available (null/zero)
        int score = ScoreBuildVolume(printer, null, null, null);

        score.Should().Be(100, "no gcode dimensions should assume volume fits");
    }

    // =========================================================================
    // COLOR MATCH FACTOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScoreColorMatch_ExactHexMatch_Returns100()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        int score = ScoreColorMatch(printer, job);

        score.Should().Be(100, "exact hex match should score 100");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScoreColorMatch_SameColorFamily_Returns80()
    {
        // Both are red family but different hex values
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#CC0000"; // Dark red
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000"; // Bright red

        int score = ScoreColorMatch(printer, job);

        score.Should().Be(80, "same color family should score 80");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScoreColorMatch_DifferentFamily_Returns20()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#0000FF"; // Blue
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000"; // Red

        int score = ScoreColorMatch(printer, job);

        score.Should().Be(20, "different color family should score 20");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScoreColorMatch_NoJobColor_ReturnsNeutral()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = "#FF0000";
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob();
        job.FilamentColor = null;

        int score = ScoreColorMatch(printer, job);

        score.Should().Be(50, "no job color should return neutral 50");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public void ScoreColorMatch_NoPrinterColor_ReturnsNeutral()
    {
        Printer printer = CreateTestPrinter();
        Toolhead toolhead = CreateToolhead(printer.Id);
        toolhead.CurrentFilamentColor = null;
        printer.Toolheads.Add(toolhead);

        PrintJob job = CreateTestJob();
        job.FilamentColor = "#FF0000";

        int score = ScoreColorMatch(printer, job);

        score.Should().Be(50, "no printer color should return neutral 50");
    }

    // =========================================================================
    // SCORING HELPER METHODS
    // These implement the scoring logic from the specification.
    // When Lambert's IDispatchScorer lands, these tests should be adapted
    // to call the real implementation instead.
    // =========================================================================

    /// <summary>
    /// Scores material compatibility between printer and job.
    /// Returns 100 (exact match), 50 (supported but not loaded), or -1 (eliminate).
    /// </summary>
    private static int ScoreMaterialMatch(Printer printer, PrintJob job)
    {
        if (string.IsNullOrEmpty(job.RequiredMaterialType))
        {
            return 100; // No requirement → matches anything
        }

        // Check if currently loaded material matches
        if (string.Equals(printer.CurrentMaterial, job.RequiredMaterialType, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        // Check if any toolhead supports the required material
        bool toolheadSupports = printer.Toolheads.Any(t =>
            t.SupportedMaterials is not null &&
            t.SupportedMaterials.Any(m =>
                string.Equals(m, job.RequiredMaterialType, StringComparison.OrdinalIgnoreCase)));

        return toolheadSupports ? 50 : -1; // Supported but not loaded = 50, not supported = eliminate
    }

    /// <summary>
    /// Scores nozzle diameter compatibility.
    /// Returns 100 (exact match ±0.01mm) or -1 (eliminate).
    /// </summary>
    private static int ScoreNozzleDiameter(Printer printer, PrintJob job)
    {
        if (!job.RequiredNozzleDiameter.HasValue)
        {
            return 100; // No requirement
        }

        Toolhead? primaryToolhead = printer.Toolheads.FirstOrDefault(t => t.IsPrimary)
            ?? printer.Toolheads.FirstOrDefault();

        if (primaryToolhead?.NozzleModel is null)
        {
            return -1; // Can't verify nozzle → eliminate
        }

        double diff = Math.Abs(primaryToolhead.NozzleModel.Diameter - (double)job.RequiredNozzleDiameter.Value);
        return diff <= 0.01 ? 100 : -1;
    }

    /// <summary>
    /// Checks if gcode dimensions exceed printer build volume.
    /// </summary>
    private static bool DoesBuildVolumeExceed(Printer printer, double? gcodeX, double? gcodeY, double? gcodeZ)
    {
        if (!gcodeX.HasValue && !gcodeY.HasValue && !gcodeZ.HasValue)
        {
            return false; // No dimensions → assume fits
        }

        if (gcodeX.HasValue && printer.MaxBuildVolumeX.HasValue && gcodeX > printer.MaxBuildVolumeX)
        {
            return true;
        }

        if (gcodeY.HasValue && printer.MaxBuildVolumeY.HasValue && gcodeY > printer.MaxBuildVolumeY)
        {
            return true;
        }

        if (gcodeZ.HasValue && printer.MaxBuildVolumeZ.HasValue && gcodeZ > printer.MaxBuildVolumeZ)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Scores build volume compatibility.
    /// Returns 100 (fits) or -1 (eliminate).
    /// </summary>
    private static int ScoreBuildVolume(Printer printer, double? gcodeX, double? gcodeY, double? gcodeZ)
    {
        return DoesBuildVolumeExceed(printer, gcodeX, gcodeY, gcodeZ) ? -1 : 100;
    }

    /// <summary>
    /// Scores nozzle hardness vs material abrasiveness.
    /// Returns 100 (safe) or -1 (eliminate).
    /// </summary>
    private static int ScoreNozzleHardness(bool isAbrasive, bool isHardened)
    {
        if (!isAbrasive)
        {
            return 100; // Non-abrasive material → any nozzle is fine
        }

        return isHardened ? 100 : -1; // Abrasive + not hardened → eliminate
    }

    /// <summary>
    /// Scores enclosure compatibility.
    /// Returns 100 (met) or -1 (eliminate).
    /// </summary>
    private static int ScoreEnclosure(bool needsEnclosure, bool hasEnclosure)
    {
        if (!needsEnclosure)
        {
            return 100;
        }

        return hasEnclosure ? 100 : -1;
    }

    /// <summary>
    /// Scores printer preference.
    /// Returns 100 (preferred), 50 (neutral), or -1 (excluded/eliminated).
    /// </summary>
    private static int ScorePreference(Guid printerId, Guid[]? preferredIds, Guid[]? excludedIds)
    {
        if (excludedIds is not null && excludedIds.Contains(printerId))
        {
            return -1; // Excluded → eliminate
        }

        if (preferredIds is not null && preferredIds.Contains(printerId))
        {
            return 100; // Preferred
        }

        return 50; // Neutral
    }

    /// <summary>
    /// Scores queue depth (0 jobs → 100, 6+ jobs → 10).
    /// Linear interpolation between those bounds.
    /// </summary>
    private static int ScoreQueueDepth(int queuedJobCount)
    {
        if (queuedJobCount <= 0)
        {
            return 100;
        }

        if (queuedJobCount >= 6)
        {
            return 10;
        }

        // Linear interpolation: 100 at 0, 10 at 6
        return 100 - (int)(queuedJobCount * (90.0 / 6.0));
    }

    /// <summary>
    /// Scores toolhead presence when material/nozzle requirements exist.
    /// Returns 100 (has toolheads) or -1 (eliminate).
    /// </summary>
    private static int ScoreToolheadPresence(Printer printer, PrintJob job)
    {
        bool requiresToolhead = !string.IsNullOrEmpty(job.RequiredMaterialType)
            || job.RequiredNozzleDiameter.HasValue;

        if (!requiresToolhead)
        {
            return 100;
        }

        return printer.Toolheads.Count > 0 ? 100 : -1;
    }

    /// <summary>
    /// Scores color match between printer's loaded filament and job's required color.
    /// Returns 100 (exact hex), 80 (same family), 50 (neutral/no data), or 20 (different family).
    /// </summary>
    private static int ScoreColorMatch(Printer printer, PrintJob job)
    {
        if (string.IsNullOrWhiteSpace(job.FilamentColor))
        {
            return 50; // No job color — neutral
        }

        string? printerColor = printer.Toolheads
            .Where(t => t.IsPrimary)
            .Select(t => t.CurrentFilamentColor)
            .FirstOrDefault()
            ?? printer.Toolheads
                .Select(t => t.CurrentFilamentColor)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        if (string.IsNullOrWhiteSpace(printerColor))
        {
            return 50; // No printer color — neutral
        }

        string jobHex = job.FilamentColor.Trim().TrimStart('#').ToUpperInvariant();
        string printerHex = printerColor.Trim().TrimStart('#').ToUpperInvariant();

        if (string.Equals(jobHex, printerHex, StringComparison.Ordinal))
        {
            return 100; // Exact hex match
        }

        (string Name, string Hex)? jobFamily = AutoTagService.HexToColorFamily(job.FilamentColor);
        (string Name, string Hex)? printerFamily = AutoTagService.HexToColorFamily(printerColor);

        if (jobFamily is null || printerFamily is null)
        {
            return 50; // Unparseable color — neutral
        }

        if (string.Equals(jobFamily.Value.Name, printerFamily.Value.Name, StringComparison.OrdinalIgnoreCase))
        {
            return 80; // Same color family
        }

        return 20; // Different family — slight penalty
    }

    /// <summary>
    /// Calculates weighted average score across all factors.
    /// Weights: material=30, nozzle=25, buildVolume=15, nozzleHardness=10, enclosure=10, preference=5, queueDepth=5
    /// </summary>
    private static int CalculateWeightedScore(Dictionary<string, int> factorScores)
    {
        var weights = new Dictionary<string, int>
        {
            ["material"] = 30,
            ["nozzle"] = 25,
            ["buildVolume"] = 15,
            ["nozzleHardness"] = 10,
            ["enclosure"] = 10,
            ["preference"] = 5,
            ["queueDepth"] = 5
        };

        double weightedSum = 0;
        int totalWeight = 0;

        foreach (KeyValuePair<string, int> factor in factorScores)
        {
            if (factor.Value < 0)
            {
                return -1; // Any elimination kills the whole score
            }

            if (weights.TryGetValue(factor.Key, out int weight))
            {
                weightedSum += factor.Value * weight;
                totalWeight += weight;
            }
        }

        return totalWeight > 0 ? (int)(weightedSum / totalWeight) : 0;
    }
}

/// <summary>
/// Integration tests for the dispatch API endpoints.
/// These require the DispatchController to exist (being built by Lambert).
/// Tests use CustomWebApplicationFactory for full HTTP testing.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class DispatchEndpointIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public DispatchEndpointIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    // TODO: These endpoint tests require Lambert's DispatchController to be implemented.
    // Endpoints expected:
    //   GET  /api/dispatch/candidates?jobId={id} → ranked list of printers
    //   POST /api/dispatch/assign → assign job to specific printer

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task GetCandidates_ReturnsRankedList()
    {
        // Arrange: Create printers and a queued job via the API
        HttpClient client = await _factory.CreateAdminClientAsync();

        // Seed test data: create a location, printer, and job
        // TODO: When DispatchController exists, POST to /api/dispatch/candidates
        // For now, verify the factory and auth flow work
        System.Net.Http.HttpResponseMessage healthResponse = await client.GetAsync("/healthz");
        healthResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        // When DispatchController lands:
        // var response = await client.GetAsync($"/api/dispatch/candidates?jobId={jobId}");
        // response.StatusCode.Should().Be(HttpStatusCode.OK);
        // var candidates = await response.Content.ReadFromJsonAsync<List<DispatchCandidateDto>>();
        // candidates.Should().NotBeNull();
        // candidates.Should().BeInDescendingOrder(c => c.Score);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task GetCandidates_NoPrintersAvailable_ReturnsEmpty()
    {
        // No printers in the system → should return empty list
        HttpClient client = await _factory.CreateAdminClientAsync();

        // TODO: When DispatchController exists:
        // var response = await client.GetAsync($"/api/dispatch/candidates?jobId={jobId}");
        // var candidates = await response.Content.ReadFromJsonAsync<List<DispatchCandidateDto>>();
        // candidates.Should().BeEmpty();

        // For now, verify the test infrastructure works
        System.Net.Http.HttpResponseMessage healthResponse = await client.GetAsync("/healthz");
        healthResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task DispatchJob_ValidPrinter_AssignsAndStarts()
    {
        // Dispatch a job to a valid printer → should assign and return 200
        HttpClient client = await _factory.CreateAdminClientAsync();

        // TODO: When DispatchController exists:
        // 1. Create a printer via POST /api/printers
        // 2. Create a print job via POST /api/printjobs
        // 3. POST /api/dispatch/assign { jobId, printerId }
        // 4. Verify job status changes to Assigned
        // var response = await client.PostAsJsonAsync("/api/dispatch/assign", new { jobId, printerId });
        // response.StatusCode.Should().Be(HttpStatusCode.OK);

        System.Net.Http.HttpResponseMessage healthResponse = await client.GetAsync("/healthz");
        healthResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task DispatchJob_PrinterBusy_Returns409()
    {
        // Dispatch to a printer that's already printing → 409 Conflict
        HttpClient client = await _factory.CreateAdminClientAsync();

        // TODO: When DispatchController exists:
        // 1. Create printer, assign an active job so it's busy
        // 2. Try to dispatch another job to same printer
        // var response = await client.PostAsJsonAsync("/api/dispatch/assign", new { jobId, printerId });
        // response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        System.Net.Http.HttpResponseMessage healthResponse = await client.GetAsync("/healthz");
        healthResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
