using System.Linq;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// #1827 dispatch/backward-compat parity: locks in that <see cref="DispatchScorer.ScoreNozzleHardness"/>
/// (via <c>Toolhead.NozzleModel.IsHardened</c>) still eliminates/admits abrasive-filament jobs
/// identically to the pre-catalog <c>IsHardenedByMaterial(NozzleType)</c> baseline (removed in
/// commit eb2804eb1's predecessor, before the #1824 <see cref="NozzleMaterial"/> catalog
/// existed), now that hardness is resolved through the user-editable catalog.
/// <para>
/// Unlike <see cref="DispatchScorerNozzleHardnessOverrideTests"/> (which hand-seeds only Brass
/// and Diamond), this suite seeds the catalog via the real
/// <see cref="DataSeedService.SeedNozzleMaterialsAsync"/> — the same seeding path used by local
/// dev/test databases that use <c>EnsureCreated</c> instead of applying the
/// <c>AddNozzleMaterialCatalog</c> migration — and exercises every built-in
/// <see cref="NozzleType"/> member so no material's hardness classification regresses silently.
/// </para>
/// </summary>
public sealed class DispatchScorerNozzleMaterialCatalogParityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerNozzleMaterialCatalogParityTests()
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

        // Seed via the real production seeding path (not hand-built fixtures) so this test
        // actually exercises the same catalog rows a fresh local/dev database would have.
        Mock<IYamlSeedDataReader> reader = new();
        Mock<ILogger<DataSeedService>> logger = new();
        DataSeedService seedService = new(_context, reader.Object, logger.Object);
        seedService.SeedNozzleMaterialsAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Guid> SeedAbrasiveJobForNozzleAsync(NozzleType nozzleType)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];

        _context.FilamentTypes.Add(new FilamentType
        {
            Name = "CF-PLA",
            IsAbrasive = true,
            IsActive = true,
        });

        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Catalog Parity Mfg {suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model {suffix}" };

        Printer printer = new PrinterBuilder().Build();
        printer.ManufacturerId = mfg.Id;
        printer.ModelId = model.Id;
        printer.ServerUrl = $"http://catalog-parity-{suffix}.local";
        printer.IsAvailable = true;
        printer.IsEnabled = true;
        printer.CurrentMaterial = "CF-PLA";

        NozzleModelDefinition nozzle = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{nozzleType} 0.4",
            Diameter = 0.4,
            NozzleMaterialId = _context.NozzleMaterials.Single(m => m.Name == nozzleType.ToString()).Id,
            HardnessOverride = NozzleHardnessOverride.Auto,
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

    // Ground truth recovered from the pre-catalog IsHardenedByMaterial(NozzleType) static switch
    // (introduced alongside NozzleHardnessOverride in commit eb2804eb1's predecessor, before
    // #1824 replaced the NozzleType column with the NozzleMaterial catalog FK). This is the
    // exact classification the seeded NozzleMaterial rows (DataSeedService and the
    // AddNozzleMaterialCatalog migration SQL) must continue to reproduce.
    [Theory]
    [Trait("Category", "Dispatch")]
    [InlineData(NozzleType.Brass, false)]
    [InlineData(NozzleType.HardenedSteel, true)]
    [InlineData(NozzleType.StainlessSteel, false)]
    [InlineData(NozzleType.TungstenCarbide, true)]
    [InlineData(NozzleType.Abrasive, true)]
    [InlineData(NozzleType.Diamond, true)]
    [InlineData(NozzleType.Ruby, true)]
    [InlineData(NozzleType.PlatedCopper, false)]
    [InlineData(NozzleType.ToolSteel, true)]
    public async Task AbrasiveJob_ForEachBuiltInNozzleMaterial_MatchesPreCatalogHardnessBaseline(
        NozzleType nozzleType,
        bool expectedHardened)
    {
        Guid jobId = await SeedAbrasiveJobForNozzleAsync(nozzleType);

        DispatchScore score = await ScoreAsync(jobId);

        _ = score.Eliminated.Should().Be(
            !expectedHardened,
            $"{nozzleType} was {(expectedHardened ? "hardened" : "not hardened")} " +
            "under the pre-catalog IsHardenedByMaterial baseline");
        _ = score.ScoreBreakdown["NozzleHardness"].Score.Should().Be(expectedHardened ? 100 : 0);
        if (!expectedHardened)
        {
            _ = score.EliminationReasons.Should()
                .Contain(r => r.Contains("abrasive", StringComparison.OrdinalIgnoreCase));
        }
    }
}
