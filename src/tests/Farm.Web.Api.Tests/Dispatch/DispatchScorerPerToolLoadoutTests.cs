using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Web.Api.Tests.Builders;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Per-tool loadout factor coverage for the dispatch scorer (issue #711, F6).
/// Verifies that <c>DispatchScorer</c> emits one explainable
/// <c>PerToolLoadout.T{n}</c> factor per required tool, cross-referencing
/// #710 <c>RequiredMaterialsPerTool</c> with #709 per-toolhead loaded-spool state,
/// and that these factors never eliminate a candidate on their own.
/// </summary>
public class DispatchScorerPerToolLoadoutTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly Guid _folderId = Guid.NewGuid();

    public DispatchScorerPerToolLoadoutTests()
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

    private (Printer P, Toolhead T0, Toolhead T1) SeedMultiToolheadPrinter(string? t0Material, string? t1Material)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"PerTool Mfg {suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"IDEX {suffix}" };
        Printer printer = new PrinterBuilder().Build();
        printer.ManufacturerId = mfg.Id;
        printer.ModelId = model.Id;
        printer.ServerUrl = $"http://per-tool-{suffix}.local";
        printer.MultiMaterial = true;
        printer.CurrentMaterial = null;
        printer.IsAvailable = true;
        printer.IsEnabled = true;

        NozzleModelDefinition nozzle = new()
        {
            Id = Guid.NewGuid(),
            Name = "Brass 0.4",
            Diameter = 0.4,
            NozzleType = NozzleType.Brass,
            ManufacturerId = mfg.Id,
        };
        _context.NozzleModelDefinitions.Add(nozzle);

        Toolhead t0 = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            NozzleModelId = nozzle.Id,
            NozzleModel = nozzle,
            SupportedMaterials = ["PLA", "PETG", "ABS"],
            ToolheadType = ToolheadType.Physical,
            CurrentMaterial = t0Material,
            UpdatedAt = DateTime.UtcNow,
        };
        Toolhead t1 = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T1",
            Index = 1,
            IsPrimary = false,
            NozzleModelId = nozzle.Id,
            NozzleModel = nozzle,
            SupportedMaterials = ["PLA", "PETG", "ABS"],
            ToolheadType = ToolheadType.Physical,
            CurrentMaterial = t1Material,
            UpdatedAt = DateTime.UtcNow,
        };
        printer.Toolheads.Add(t0);
        printer.Toolheads.Add(t1);

        _context.Manufacturers.Add(mfg);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        return (printer, t0, t1);
    }

    private PrintJob CreateJobWithToolRequirements(params PrintJobToolMaterialRequirement[] reqs)
    {
        GcodeFile gcode = new()
        {
            Id = Guid.NewGuid(),
            Name = "multi.gcode",
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
            Name = "Multi tool",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
            RequiredMaterialsPerTool = reqs,
        };
        return job;
    }

    [Fact]
    public async Task ScorePrinters_EmitsPerToolFactor_PerRequirement()
    {
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 25.0),
            new PrintJobToolMaterialRequirement(1, "PETG", null, 10.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);

        DispatchScore s = scores.Should().ContainSingle().Subject;
        s.ScoreBreakdown.Should().ContainKey("PerToolLoadout.T0");
        s.ScoreBreakdown.Should().ContainKey("PerToolLoadout.T1");
        s.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(100);
        s.ScoreBreakdown["PerToolLoadout.T1"].Score.Should().Be(100);
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_MismatchOnRequiredTool_ScoresLowButDoesNotEliminate()
    {
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "ABS");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, null),
            new PrintJobToolMaterialRequirement(1, "PETG", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(100);
        FactorScore t1 = s.ScoreBreakdown["PerToolLoadout.T1"];
        t1.Score.Should().Be(20);
        t1.EliminationReason.Should().NotBeNull();
        t1.IsHardRequirement.Should().BeFalse();
        s.Eliminated.Should().BeFalse("per-tool loadout is a soft factor even on mismatch");
    }

    [Fact]
    public async Task ScorePrinters_RequiredToolLoadedElsewhere_ReceivesFallbackCreditNotFullMatch()
    {
        // T0 loaded PLA, T1 loaded PLA — but requirement asks for T1 to be PETG, which is
        // loaded nowhere. So T1 should get the "not loaded anywhere" 20 score.
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, null),
            new PrintJobToolMaterialRequirement(1, "PETG", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown["PerToolLoadout.T1"].Score.Should().Be(20);
    }

    [Fact]
    public async Task ScorePrinters_MaterialLoadedOnDifferentToolhead_ScoresPartialCredit()
    {
        // T0 has PETG, T1 has PLA. Requirement asks T0 = PLA. Indexed match fails but the
        // material is available on another physical toolhead → partial credit (75).
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PETG", t1Material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(75);
    }

    [Fact]
    public async Task ScorePrinters_MmuGateOnlyPrinter_MapsStoredIndicesToGcodeTools()
    {
        (Printer _, Toolhead gate0, Toolhead gate1) = SeedMultiToolheadPrinter(
            t0Material: "PLA",
            t1Material: "PETG");
        gate0.ToolheadType = ToolheadType.MmuGate;
        gate0.Index = 1;
        gate1.ToolheadType = ToolheadType.MmuGate;
        gate1.Index = 2;
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, null),
            new PrintJobToolMaterialRequirement(1, "PETG", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore score = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        score.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(100);
        score.ScoreBreakdown["PerToolLoadout.T1"].Score.Should().Be(100);
    }

    [Fact]
    public async Task ScorePrinters_MixedPhysicalAndMmuGate_PrefersMappedMmuSourceForT0()
    {
        (Printer _, _, Toolhead gate0) = SeedMultiToolheadPrinter(
            t0Material: "ABS",
            t1Material: "PLA");
        gate0.ToolheadType = ToolheadType.MmuGate;
        gate0.Index = 1;
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore score = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        score.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(100);
    }

    [Fact]
    public async Task ScorePrinters_MmuMaterialLoadedOnDifferentTool_ScoresPartialCredit()
    {
        (Printer _, _, Toolhead gate0) = SeedMultiToolheadPrinter(
            t0Material: "ABS",
            t1Material: "PETG");
        gate0.ToolheadType = ToolheadType.MmuGate;
        gate0.Index = 1;
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(1, "PETG", null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore score = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        score.ScoreBreakdown["PerToolLoadout.T1"].Score.Should().Be(75);
    }

    [Fact]
    public async Task ScorePrinters_NullPerToolMaterial_ScoresNeutral()
    {
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: null, t1Material: null);
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, null, null, null));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(60);
    }

    [Fact]
    public async Task ScorePrinters_MultiSlotFallbackDisabled_OmitsPerToolFactors()
    {
        // Issue #711, FIX E: when the multi-slot-fallback operator feature is OFF, the scorer
        // must not emit any per-tool loadout factors even though the job carries per-tool reqs.
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 25.0),
            new PrintJobToolMaterialRequirement(1, "PETG", null, 10.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance, gate.Object);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown.Should().NotContainKey("PerToolLoadout.T0");
        s.ScoreBreakdown.Should().NotContainKey("PerToolLoadout.T1");
        // Core factors still score — gating only removes the per-tool explainability.
        s.ScoreBreakdown.Should().ContainKey("MaterialMatch");
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_MultiSlotFallbackEnabled_EmitsPerToolFactors()
    {
        // Complement of the gate-off test: an explicitly enabled gate matches default behavior.
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 25.0),
            new PrintJobToolMaterialRequirement(1, "PETG", null, 10.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance, gate.Object);
        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        s.ScoreBreakdown.Should().ContainKey("PerToolLoadout.T0");
        s.ScoreBreakdown.Should().ContainKey("PerToolLoadout.T1");
    }

    [Fact]
    public async Task ScorePrinters_MultipleCandidates_QueriesFeatureGateOnce()
    {
        _ = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        _ = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 25.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = new(MockBehavior.Strict);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance, gate.Object);
        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);

        scores.Should().HaveCount(2);
        gate.Verify(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Finding 4: per-tool grams coverage overlay ----

    [Fact]
    public async Task ScorePrinters_ExactMatchWithSufficientGrams_KeepsFullFactor()
    {
        // (a) Exact indexed match AND the loaded spool has more grams than the tool needs →
        // the material score is preserved at 100 with a "covers" explanation.
        (Printer printer, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 500.0), (1, 500.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            featureGate: null,
            coverageService: new StubCoverageService(fleet));

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        FactorScore f = s.ScoreBreakdown["PerToolLoadout.T0"];
        f.Score.Should().Be(100);
        f.EliminationReason.Should().Contain("covers");
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_ExactMatchButShortOnGrams_DiscountsProportionally()
    {
        // (b) Only 1g remaining for a 100g requirement, with no fallback group → the factor is
        // discounted to ~1 (100 * 1/100) and the shortfall is spelled out for the operator.
        (Printer printer, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 1.0), (1, 500.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            featureGate: null,
            coverageService: new StubCoverageService(fleet));

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        FactorScore f = s.ScoreBreakdown["PerToolLoadout.T0"];
        f.Score.Should().BeApproximately(1.0, 0.001);
        f.EliminationReason.Should().Contain("source usable 1g");
        f.EliminationReason.Should().Contain("shortfall remaining 99g");
        f.EliminationReason.Should().Contain("insufficient usable coverage");
        f.IsHardRequirement.Should().BeFalse();
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_ShortfallCoveredByFallbackGroup_ReceivesDiscountedCredit()
    {
        // (c) T0 is loaded PLA but short; T1 is loaded PLA with plenty. A fallback group binding
        // both toolheads for PLA lets the auto-switch dependency keep T0 viable at a reduced
        // weight (60 = 100 * 0.6) rather than the near-zero proportional penalty.
        (Printer printer, Toolhead t0, Toolhead t1) =
            SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FilamentFallbackGroupService fallback =
            new(_context, NullLogger<FilamentFallbackGroupService>.Instance);
        await fallback.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 10.0), (1, 500.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            featureGate: null,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback);

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        FactorScore f = s.ScoreBreakdown["PerToolLoadout.T0"];
        f.Score.Should().Be(64);
        f.EliminationReason.Should().Contain("T1 usable 500g, consumed 90g");
        f.EliminationReason.Should().Contain("fallback closed gap: yes");
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_RawRemainingConsumedByCommitmentsAndReserve_ScoresNearZero()
    {
        (Printer printer, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        // The coverage service has already subtracted 450g committed demand and a 50g reserve.
        FleetFilamentCoverageDto fleet = CoverageForUsable(
            printer.Id,
            (Index: 0, Remaining: 500.0, TotalDemand: 450.0, Available: 0.0),
            (Index: 1, Remaining: 500.0, TotalDemand: 0.0, Available: 500.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet));

        FactorScore factor = (await scorer.ScorePrintersForJobAsync(job.Id))
            .Single()
            .ScoreBreakdown["PerToolLoadout.T0"];

        factor.Score.Should().Be(0);
        factor.EliminationReason.Should().Contain("source usable 0g");
        factor.EliminationReason.Should().Contain("insufficient usable coverage");
    }

    [Fact]
    public async Task ScorePrinters_SourceAndFallbackAggregateToFullCoverage_DiscountsFallbackPortion()
    {
        (Printer printer, Toolhead t0, Toolhead t1) =
            SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FilamentFallbackGroupService fallback =
            new(_context, NullLogger<FilamentFallbackGroupService>.Instance);
        await fallback.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 60.0), (1, 50.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback);

        FactorScore factor = (await scorer.ScorePrintersForJobAsync(job.Id))
            .Single()
            .ScoreBreakdown["PerToolLoadout.T0"];

        factor.Score.Should().Be(84);
        factor.EliminationReason.Should().Contain("T1 usable 50g, consumed 40g");
        factor.EliminationReason.Should().Contain("shortfall remaining 0g");
        factor.EliminationReason.Should().Contain("fallback closed gap: yes");
    }

    [Fact]
    public async Task ScorePrinters_TwoFallbackMembersAggregateToCloseGap()
    {
        (Printer printer, Toolhead t0, Toolhead t1) =
            SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        Toolhead t2 = AddToolhead(printer, index: 2, material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FilamentFallbackGroupService fallback =
            new(_context, NullLogger<FilamentFallbackGroupService>.Instance);
        await fallback.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA chain", "PLA", null, [t0.Id, t1.Id, t2.Id]),
            CancellationToken.None);

        FleetFilamentCoverageDto fleet =
            CoverageFor(printer.Id, (0, 40.0), (1, 40.0), (2, 40.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback);

        FactorScore factor = (await scorer.ScorePrintersForJobAsync(job.Id))
            .Single()
            .ScoreBreakdown["PerToolLoadout.T0"];

        factor.Score.Should().Be(76);
        factor.EliminationReason.Should().Contain("T1 usable 40g, consumed 40g");
        factor.EliminationReason.Should().Contain("T2 usable 40g, consumed 20g");
        factor.EliminationReason.Should().Contain("fallback closed gap: yes");
    }

    [Fact]
    public async Task ScorePrinters_NoCoverageService_KeepsMaterialScoreWithUnknownExplanation()
    {
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(_context, NullLogger<DispatchScorer>.Instance);
        FactorScore factor = (await scorer.ScorePrintersForJobAsync(job.Id))
            .Single()
            .ScoreBreakdown["PerToolLoadout.T0"];

        factor.Score.Should().Be(100);
        factor.EliminationReason.Should().Contain("coverage unknown");
    }

    [Fact]
    public async Task ScorePrinters_MultipleCandidates_QueriesFallbackBatchOnce()
    {
        (Printer first, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        (Printer second, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = new(
            [
                CoverageFor(first.Id, (0, 10.0), (1, 20.0)).Printers.Single(),
                CoverageFor(second.Id, (0, 10.0), (1, 20.0)).Printers.Single(),
            ],
            DateTime.UtcNow);
        Mock<IFilamentFallbackGroupService> fallback = new(MockBehavior.Strict);
        fallback
            .Setup(service => service.GetAvailableFallbacksAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<FilamentFallbackLookupKey, FilamentFallbackResolution>());
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback.Object);

        (await scorer.ScorePrintersForJobAsync(job.Id)).Should().HaveCount(2);

        fallback.Verify(service => service.GetAvailableFallbacksAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Distinct().Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);
        fallback.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScorePrinters_CoverageDataMissing_KeepsMaterialScoreWithUnknownNote()
    {
        // (d) The coverage service is wired but returns no data for this printer → the factor
        // falls back to the material-only score (100) and flags that grams are unverified.
        (Printer _, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FleetFilamentCoverageDto emptyFleet = new([], DateTime.UtcNow);
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            featureGate: null,
            coverageService: new StubCoverageService(emptyFleet));

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        FactorScore f = s.ScoreBreakdown["PerToolLoadout.T0"];
        f.Score.Should().Be(100);
        f.EliminationReason.Should().Contain("coverage unknown");
        s.Eliminated.Should().BeFalse();
    }

    [Fact]
    public async Task ScorePrinters_CoverageFailure_NullsOriginProvenance()
    {
        _ = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PETG");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 100.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new ThrowingCoverageService(),
            watermarkReader: new ConstantWatermarkReader(41));

        DispatchScoreResult result = await scorer.ScorePrintersForJobWithOriginAsync(job.Id);

        result.OriginWatermark.Should().BeNull();
    }

    // ---- Finding H4: printer-wide grams allocation ledger (no double-counting) ----

    [Fact]
    public async Task ScorePrinters_SharedFallbackMember_AllocatedOncePerPrinter_SecondRequirementShorted()
    {
        // Chain T0→T1→T2 with only T2 holding grams (100g). Two 80g requirements (tools 0 and 1)
        // both fall back to the shared T2 tail. Without a ledger each would claim the full 100g
        // (160g "covered"); with the ledger the first takes 80g and the second only sees 20g.
        (Printer printer, Toolhead t0, Toolhead t1) =
            SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        Toolhead t2 = AddToolhead(printer, index: 2, material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 80.0),
            new PrintJobToolMaterialRequirement(1, "PLA", null, 80.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FilamentFallbackGroupService fallback =
            new(_context, NullLogger<FilamentFallbackGroupService>.Instance);
        await fallback.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA chain", "PLA", null, [t0.Id, t1.Id, t2.Id]),
            CancellationToken.None);

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 0.0), (1, 0.0), (2, 100.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback);

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        FactorScore t0Factor = s.ScoreBreakdown["PerToolLoadout.T0"];
        FactorScore t1Factor = s.ScoreBreakdown["PerToolLoadout.T1"];
        // First requirement consumes 80g of the shared tail and is covered.
        t0Factor.EliminationReason.Should().Contain("T2 usable 100g, consumed 80g");
        t0Factor.EliminationReason.Should().Contain("fallback closed gap: yes");
        // Second requirement only sees the remaining 20g — the shared spool is not double-counted.
        t1Factor.EliminationReason.Should().Contain("T2 usable 20g, consumed 20g");
        t1Factor.EliminationReason.Should().Contain("shortfall remaining 60g");
        t1Factor.EliminationReason.Should().Contain("insufficient usable coverage");
        t1Factor.Score.Should().BeLessThan(t0Factor.Score);
    }

    [Fact]
    public async Task ScorePrinters_TwoRequirementsShareSourceToolhead_SumAgainstItsCoverage()
    {
        // T0 loaded PLA (100g), T1 empty. Tool 0 matches T0 exactly; tool 1 finds the same PLA on
        // T0. Both requirements draw grams from the single T0 spool, so the second must see it
        // already partly allocated rather than a fresh 100g.
        (Printer printer, _, _) = SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: null);
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 80.0),
            new PrintJobToolMaterialRequirement(1, "PLA", null, 80.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 100.0), (1, 0.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet));

        DispatchScore s = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        // Tool 0 (exact) consumes 80g of the 100g source and stays fully covered.
        s.ScoreBreakdown["PerToolLoadout.T0"].Score.Should().Be(100);
        // Tool 1 shares the same source spool and only sees the remaining 20g.
        FactorScore t1 = s.ScoreBreakdown["PerToolLoadout.T1"];
        t1.EliminationReason.Should().Contain("source usable 20g");
        t1.EliminationReason.Should().Contain("shortfall remaining 60g");
        t1.EliminationReason.Should().Contain("insufficient usable coverage");
    }

    [Fact]
    public async Task ScorePrinters_SharedFallbackAllocation_IsDeterministicAcrossCalls()
    {
        (Printer printer, Toolhead t0, Toolhead t1) =
            SeedMultiToolheadPrinter(t0Material: "PLA", t1Material: "PLA");
        Toolhead t2 = AddToolhead(printer, index: 2, material: "PLA");
        PrintJob job = CreateJobWithToolRequirements(
            new PrintJobToolMaterialRequirement(0, "PLA", null, 80.0),
            new PrintJobToolMaterialRequirement(1, "PLA", null, 80.0));
        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync();

        FilamentFallbackGroupService fallback =
            new(_context, NullLogger<FilamentFallbackGroupService>.Instance);
        await fallback.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA chain", "PLA", null, [t0.Id, t1.Id, t2.Id]),
            CancellationToken.None);

        FleetFilamentCoverageDto fleet = CoverageFor(printer.Id, (0, 0.0), (1, 0.0), (2, 100.0));
        DispatchScorer scorer = new(
            _context,
            NullLogger<DispatchScorer>.Instance,
            coverageService: new StubCoverageService(fleet),
            fallbackService: fallback);

        DispatchScore first = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();
        DispatchScore second = (await scorer.ScorePrintersForJobAsync(job.Id)).Single();

        first.ScoreBreakdown["PerToolLoadout.T0"].Score
            .Should().Be(second.ScoreBreakdown["PerToolLoadout.T0"].Score);
        first.ScoreBreakdown["PerToolLoadout.T1"].Score
            .Should().Be(second.ScoreBreakdown["PerToolLoadout.T1"].Score);
        first.ScoreBreakdown["PerToolLoadout.T1"].EliminationReason
            .Should().Be(second.ScoreBreakdown["PerToolLoadout.T1"].EliminationReason);
    }

    private static FleetFilamentCoverageDto CoverageFor(
        Guid printerId,
        params (int Index, double? Remaining)[] toolheads)
        => CoverageForUsable(
            printerId,
            [.. toolheads.Select(t => (
                t.Index,
                t.Remaining,
                TotalDemand: (double?)0,
                Available: t.Remaining))]);

    private static FleetFilamentCoverageDto CoverageForUsable(
        Guid printerId,
        params (int Index, double? Remaining, double? TotalDemand, double? Available)[] toolheads)
    {
        List<ToolheadCoverageDto> rows = [.. toolheads.Select(t => new ToolheadCoverageDto(
            ToolheadIndex: t.Index,
            ToolheadName: $"T{t.Index}",
            SpoolId: 1000 + t.Index,
            Material: "PLA",
            FilamentColor: null,
            RemainingGrams: t.Remaining,
            CurrentJobRequiredGrams: null,
            CurrentJobRemainingGrams: null,
            QueuedRequiredGrams: null,
            TotalDemandGrams: t.TotalDemand,
            Status: FilamentCoverageStatus.Covers,
            StatusReason: null,
            PredictedRunoutAt: null,
            PredictedRunoutLayer: null)
        {
            AvailableForNewDemandGrams = t.Available,
        })];

        PrinterFilamentCoverageDto printer = new(
            printerId,
            "Printer",
            FilamentCoverageStatus.Covers,
            rows,
            ActiveJobId: null,
            ActiveJobName: null,
            ActiveJobProgress: null,
            EarliestPredictedRunoutAt: null,
            AssignedQueuedJobCount: 0,
            EvaluatedAtUtc: DateTime.UtcNow);

        return new FleetFilamentCoverageDto([printer], DateTime.UtcNow);
    }

    private static Toolhead AddToolhead(Printer printer, int index, string material)
    {
        Toolhead template = printer.Toolheads.First();
        Toolhead toolhead = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = $"T{index}",
            Index = index,
            NozzleModelId = template.NozzleModelId,
            NozzleModel = template.NozzleModel,
            SupportedMaterials = ["PLA", "PETG", "ABS"],
            ToolheadType = ToolheadType.Physical,
            CurrentMaterial = material,
            UpdatedAt = DateTime.UtcNow,
        };
        printer.Toolheads.Add(toolhead);
        return toolhead;
    }

    private sealed class StubCoverageService(FleetFilamentCoverageDto fleet) : IFilamentCoverageService
    {
        public Task<PrinterFilamentCoverageDto?> GetForPrinterAsync(Guid printerId, CancellationToken ct) =>
            Task.FromResult(fleet.Printers.FirstOrDefault(p => p.PrinterId == printerId));

        public Task<FleetFilamentCoverageDto> GetForFleetAsync(CancellationToken ct) =>
            Task.FromResult(fleet);
    }

    private sealed class ThrowingCoverageService : IFilamentCoverageService
    {
        public Task<PrinterFilamentCoverageDto?> GetForPrinterAsync(Guid printerId, CancellationToken ct) =>
            Task.FromResult<PrinterFilamentCoverageDto?>(null);

        public Task<FleetFilamentCoverageDto> GetForFleetAsync(CancellationToken ct) =>
            throw new InvalidOperationException("coverage unavailable");

        public Task<FilamentCoverageResult<FleetFilamentCoverageDto>> GetForFleetWithOriginAsync(
            CancellationToken ct) =>
            throw new InvalidOperationException("coverage unavailable");
    }

    private sealed class ConstantWatermarkReader(long value) : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(value);
    }
}
