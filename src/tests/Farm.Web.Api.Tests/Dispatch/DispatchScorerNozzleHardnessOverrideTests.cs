using System.Linq;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
/// Exercises the real <see cref="DispatchScorer"/> — not a reimplementation of its scoring
/// rules — to prove that a per-model <see cref="NozzleHardnessOverride"/> actually reaches
/// the abrasive-filament elimination gate.
/// <para>
/// This matters because <c>IsHardened</c> is a computed property: the override is only
/// meaningful if the value the scorer reads reflects it. A test at the domain level alone
/// would pass even if the scorer read a stale or persisted field instead.
/// </para>
/// </summary>
public sealed class DispatchScorerNozzleHardnessOverrideTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerNozzleHardnessOverrideTests()
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
        _context.NozzleMaterials.AddRange(
            new NozzleMaterial { Id = Guid.NewGuid(), Name = nameof(NozzleType.Brass), IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true },
            new NozzleMaterial { Id = Guid.NewGuid(), Name = nameof(NozzleType.Diamond), IsHardened = true, DefaultMaxTemp = 500, IsBuiltIn = true });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> SeedAbrasiveJobForNozzleAsync(
        NozzleType nozzleType,
        NozzleHardnessOverride hardnessOverride)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        _context.FilamentTypes.Add(new FilamentType
        {
            Name = "CF-PLA",
            IsAbrasive = true,
            IsActive = true,
        });

        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Hardness Mfg {suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model {suffix}" };

        Printer printer = new PrinterBuilder().Build();
        printer.ManufacturerId = mfg.Id;
        printer.ModelId = model.Id;
        printer.ServerUrl = $"http://hardness-{suffix}.local";
        printer.IsAvailable = true;
        printer.IsEnabled = true;
        printer.CurrentMaterial = "CF-PLA";

        NozzleModelDefinition nozzle = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{nozzleType} 0.4",
            Diameter = 0.4,
            NozzleMaterialId = _context.NozzleMaterials.Single(m => m.Name == nozzleType.ToString()).Id,
            HardnessOverride = hardnessOverride,
            ManufacturerId = mfg.Id,
        };
        _context.NozzleModelDefinitions.Add(nozzle);

        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            NozzleModelId = nozzle.Id,
            NozzleModel = nozzle,
            SupportedMaterials = ["CF-PLA"],
            ToolheadType = ToolheadType.Physical,
            CurrentMaterial = "CF-PLA",
            UpdatedAt = DateTime.UtcNow,
        });

        GcodeFile gcode = new()
        {
            Id = Guid.NewGuid(),
            Name = "abrasive.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "Abrasive job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            RequiredMaterialType = "CF-PLA",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _context.Manufacturers.Add(mfg);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        return job.Id;
    }

    private async Task<DispatchScore> ScoreAsync(Guid jobId)
    {
        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        return (await scorer.ScorePrintersForJobAsync(jobId)).Single();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task AbrasiveJob_DiamondNozzle_AutoOverride_IsNotEliminated()
    {
        // Baseline: the new Diamond material must satisfy the abrasive gate on its own.
        Guid jobId = await SeedAbrasiveJobForNozzleAsync(NozzleType.Diamond, NozzleHardnessOverride.Auto);

        DispatchScore score = await ScoreAsync(jobId);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(100);
        score.Eliminated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task AbrasiveJob_DiamondNozzle_PinnedNotHardened_IsEliminated()
    {
        // The safety-critical direction: an operator who pins NotHardened on an
        // abrasion-resistant material must actually remove that printer from consideration.
        Guid jobId = await SeedAbrasiveJobForNozzleAsync(
            NozzleType.Diamond, NozzleHardnessOverride.NotHardened);

        DispatchScore score = await ScoreAsync(jobId);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue("an explicit NotHardened pin must override the material");
        score.EliminationReasons.Should().Contain(r => r.Contains("abrasive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task AbrasiveJob_BrassNozzle_PinnedHardened_IsNotEliminated()
    {
        // The inverse: pinning Hardened on a soft material admits the printer. This is a
        // deliberate operator escape hatch for products the material list cannot describe.
        Guid jobId = await SeedAbrasiveJobForNozzleAsync(
            NozzleType.Brass, NozzleHardnessOverride.Hardened);

        DispatchScore score = await ScoreAsync(jobId);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(100);
        score.Eliminated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    public async Task AbrasiveJob_BrassNozzle_AutoOverride_IsEliminated()
    {
        // Regression guard: adding the override must not weaken the pre-existing gate.
        Guid jobId = await SeedAbrasiveJobForNozzleAsync(NozzleType.Brass, NozzleHardnessOverride.Auto);

        DispatchScore score = await ScoreAsync(jobId);

        score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(0);
        score.Eliminated.Should().BeTrue();
    }
}
