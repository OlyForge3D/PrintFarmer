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

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// Equivalence tests for issue #1705: the targeted single-printer scoring path
/// (<see cref="IDispatchScorer.ScorePrinterForJobAsync"/>) must produce results
/// that are identical to the corresponding entry the fleet-wide scoring path
/// (<see cref="IDispatchScorer.ScorePrintersForJobAsync"/>) would have produced
/// for that same printer — including <see cref="DispatchScore.Eliminated"/> and
/// the elimination reasons, not just <see cref="DispatchScore.TotalScore"/>.
///
/// This guards against the two paths drifting apart now that
/// <see cref="Farm.Infrastructure.Services.Queue.Dispatch.AutoDispatchBackgroundService"/>
/// calls the targeted method instead of scoring the whole fleet and discarding
/// all but one printer per candidate job.
/// </summary>
public class DispatchScorerTargetedEquivalenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerTargetedEquivalenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        var rootFolder = new FolderNode
        {
            Id = _folderId,
            Path = "/",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow,
        };
        _context.Set<FolderNode>().Add(rootFolder);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Printer CreateTestPrinter(
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

        Toolhead toolhead = CreateToolhead(printer.Id, currentMaterial, manufacturer.Id, supportedMaterials);
        printer.Toolheads.Add(toolhead);

        return printer;
    }

    private Toolhead CreateToolhead(Guid printerId, string? currentMaterial, Guid manufacturerId, string[]? supportedMaterials = null)
    {
        NozzleMaterial? nozzleMaterial = _context.NozzleMaterials.Local.FirstOrDefault(m => m.Name == nameof(NozzleType.Brass))
            ?? _context.NozzleMaterials.FirstOrDefault(m => m.Name == nameof(NozzleType.Brass));
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
            ManufacturerId = manufacturerId,
        };
        _context.NozzleModelDefinitions.Add(nozzleModel);

        return new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Extruder 1",
            Index = 0,
            IsPrimary = true,
            NozzleModelId = nozzleModel.Id,
            NozzleModel = nozzleModel,
            CurrentMaterial = currentMaterial,
            SupportedMaterials = supportedMaterials ?? ["PLA", "PETG", "ABS"],
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private PrintJob CreateTestJob(string name, string? requiredMaterial)
    {
        Guid gcodeFileId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
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
        };

        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            GcodeFileId = gcodeFileId,
            GcodeFile = gcodeFile,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            RequiredMaterialType = requiredMaterial,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Builds a representative matrix: two jobs (one requiring PLA, one requiring
    /// ABS) scored against four printers — one that matches both jobs well, one
    /// that only supports PLA (and is therefore hard-eliminated for material
    /// mismatch on the ABS job), one that is unavailable (hard-eliminated on the
    /// availability gate regardless of job), and one that is disabled (and
    /// therefore absent from fleet scoring entirely). For every (job, enabled
    /// printer) pair, asserts the targeted score is equivalent — field for
    /// field, including elimination reasons — to the entry the fleet scan would
    /// have produced for that printer.
    /// </summary>
    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinterForJobAsync_MatrixOfJobsAndPrinters_MatchesFleetScoringEntry()
    {
        Printer plaPrinter = CreateTestPrinter(
            "PLA Printer", currentMaterial: "PLA", supportedMaterials: ["PLA", "PETG", "ABS"]);
        Printer absPrinter = CreateTestPrinter(
            "ABS Printer", currentMaterial: "ABS", supportedMaterials: ["PLA", "PETG", "ABS"]);
        Printer plaOnlyPrinter = CreateTestPrinter(
            "PLA-Only Printer", currentMaterial: "PLA", supportedMaterials: ["PLA"]);
        Printer busyPrinter = CreateTestPrinter("Busy Printer", currentMaterial: "PLA", isAvailable: false);
        Printer disabledPrinter = CreateTestPrinter("Disabled Printer", currentMaterial: "PLA", isEnabled: false);

        PrintJob plaJob = CreateTestJob("PLA Job", requiredMaterial: "PLA");
        PrintJob absJob = CreateTestJob("ABS Job", requiredMaterial: "ABS");

        _context.Printers.AddRange(plaPrinter, absPrinter, plaOnlyPrinter, busyPrinter, disabledPrinter);
        _context.PrintJobs.AddRange(plaJob, absJob);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        Guid[] enabledPrinterIds = [plaPrinter.Id, absPrinter.Id, plaOnlyPrinter.Id, busyPrinter.Id];
        Guid[] jobIds = [plaJob.Id, absJob.Id];

        foreach (Guid jobId in jobIds)
        {
            List<DispatchScore> fleetScores = await scorer.ScorePrintersForJobAsync(jobId);

            foreach (Guid printerId in enabledPrinterIds)
            {
                DispatchScore? fleetEntry = fleetScores.FirstOrDefault(s => s.PrinterId == printerId);
                fleetEntry.Should().NotBeNull(
                    "every enabled printer must appear in the fleet-wide score list");

                DispatchScore? targetedEntry = await scorer.ScorePrinterForJobAsync(jobId, printerId);

                targetedEntry.Should().BeEquivalentTo(
                    fleetEntry,
                    "the targeted single-printer score must be identical to the corresponding " +
                    "fleet-scoring entry, including TotalScore, Eliminated, EliminationReasons, " +
                    "and the full ScoreBreakdown");
            }
        }

        // The busy (unavailable) printer is eliminated on the availability hard-gate —
        // assert this elimination (and its reason) is preserved identically by the
        // targeted path, not just the aggregate TotalScore.
        DispatchScore? busyPrinterOnPlaJobFleet = (await scorer.ScorePrintersForJobAsync(plaJob.Id))
            .FirstOrDefault(s => s.PrinterId == busyPrinter.Id);
        busyPrinterOnPlaJobFleet.Should().NotBeNull();
        busyPrinterOnPlaJobFleet!.Eliminated.Should().BeTrue(
            "the printer is unavailable and must be eliminated regardless of material match");
        busyPrinterOnPlaJobFleet.EliminationReasons.Should().NotBeEmpty();

        DispatchScore? busyPrinterOnPlaJobTargeted =
            await scorer.ScorePrinterForJobAsync(plaJob.Id, busyPrinter.Id);
        busyPrinterOnPlaJobTargeted.Should().BeEquivalentTo(
            busyPrinterOnPlaJobFleet,
            "the eliminated targeted entry (including its elimination reason) must match the fleet entry exactly");

        // The PLA-only printer's toolhead declares an explicit supported-materials list
        // that excludes ABS, so the ABS job hard-eliminates it on a true material
        // mismatch (not an availability gate) — assert this elimination (and its
        // reason) is preserved identically by the targeted path.
        DispatchScore? plaOnlyPrinterOnAbsJobFleet = (await scorer.ScorePrintersForJobAsync(absJob.Id))
            .FirstOrDefault(s => s.PrinterId == plaOnlyPrinter.Id);
        plaOnlyPrinterOnAbsJobFleet.Should().NotBeNull();
        plaOnlyPrinterOnAbsJobFleet!.Eliminated.Should().BeTrue(
            "the printer's toolhead does not support the ABS material the job requires");
        plaOnlyPrinterOnAbsJobFleet.EliminationReasons.Should().Contain(
            reason => reason.Contains("ABS", StringComparison.OrdinalIgnoreCase));

        DispatchScore? plaOnlyPrinterOnAbsJobTargeted =
            await scorer.ScorePrinterForJobAsync(absJob.Id, plaOnlyPrinter.Id);
        plaOnlyPrinterOnAbsJobTargeted.Should().BeEquivalentTo(
            plaOnlyPrinterOnAbsJobFleet,
            "the eliminated targeted entry (including its material-mismatch elimination reason) " +
            "must match the fleet entry exactly");

        // A disabled printer never appears in the fleet list at all — the targeted
        // path must mirror that by returning null, not an "eliminated" score.
        (await scorer.ScorePrintersForJobAsync(plaJob.Id))
            .Should().NotContain(s => s.PrinterId == disabledPrinter.Id);
        DispatchScore? disabledTargeted =
            await scorer.ScorePrinterForJobAsync(plaJob.Id, disabledPrinter.Id);
        disabledTargeted.Should().BeNull(
            "a disabled printer is absent from fleet scoring, so the targeted path must return null, " +
            "matching what scores.FirstOrDefault(s => s.PrinterId == printerId) would yield on the fleet result");
    }

    /// <summary>
    /// A printer id that does not exist at all (not merely disabled) must also
    /// yield null from the targeted path, matching the fleet path's implicit
    /// absence.
    /// </summary>
    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinterForJobAsync_UnknownPrinterId_ReturnsNull()
    {
        PrintJob job = CreateTestJob("Solo Job", requiredMaterial: "PLA");
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        DispatchScore? result = await scorer.ScorePrinterForJobAsync(job.Id, Guid.NewGuid());

        result.Should().BeNull();
    }

    /// <summary>
    /// An unknown job id must yield null from the targeted path, matching the
    /// fleet path's behavior of returning an empty list for a job it cannot load.
    /// </summary>
    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task ScorePrinterForJobAsync_UnknownJobId_ReturnsNull()
    {
        Printer printer = CreateTestPrinter("Solo Printer", currentMaterial: "PLA");
        _context.Printers.Add(printer);
        await _context.SaveChangesAsync();

        var scorer = new DispatchScorer(_context, NullLogger<DispatchScorer>.Instance);

        List<DispatchScore> fleetScores = await scorer.ScorePrintersForJobAsync(Guid.NewGuid());
        fleetScores.Should().BeEmpty();

        DispatchScore? targeted = await scorer.ScorePrinterForJobAsync(Guid.NewGuid(), printer.Id);
        targeted.Should().BeNull();
    }
}
