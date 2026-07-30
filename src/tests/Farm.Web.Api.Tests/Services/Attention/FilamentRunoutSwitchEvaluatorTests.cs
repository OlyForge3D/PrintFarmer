using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

/// <summary>
/// Integration tests for <see cref="FilamentRunoutSwitchEvaluator"/> (issue #711, F6, Finding 2).
/// Proves the concrete telemetry grading: no loaded backup → <see cref="RunoutSwitchAssessment.NoBackup"/>;
/// a configured loaded backup without a live switch → <see cref="RunoutSwitchAssessment.BackupAvailable"/>;
/// fresh MMU active-tool/gate telemetry selecting the backup →
/// <see cref="RunoutSwitchAssessment.SwitchConfirmed"/>.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FilamentRunoutSwitchEvaluatorTests : IAsyncLifetime
{
    private const int RunoutSpoolId = 99;
    private const int BackupSpoolId = 42;

    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private AppDbContext _db = null!;
    private IFilamentFallbackGroupService _fallbackService = null!;
    private Mock<IPrinterStatusCacheReader> _statusCache = null!;
    private FilamentRunoutSwitchEvaluator _evaluator = null!;

    public FilamentRunoutSwitchEvaluatorTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _fallbackService = _scope.ServiceProvider.GetRequiredService<IFilamentFallbackGroupService>();
        _statusCache = new Mock<IPrinterStatusCacheReader>(MockBehavior.Strict);
        _statusCache
            .Setup(cache => cache.GetSnapshot(It.IsAny<Guid>()))
            .Returns((PrinterStatusCacheSnapshot?)null);
        _evaluator = new FilamentRunoutSwitchEvaluator(
            _db,
            _fallbackService,
            _statusCache.Object);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    [Fact]
    public async Task AssessAsync_NoLoadedBackup_ReturnsNoBackup()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        // Group is configured, but the backup member has nothing loaded → not an available backup.
        await CreateGroupAsync(p, t0, t1);

        RunoutSwitchAssessment result = await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.NoBackup);
    }

    [Fact]
    public async Task AssessAsync_LoadedBackupButNoLiveSwitch_ReturnsBackupAvailable()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = BackupSpoolId;
        // Printer is still feeding from the runout spool — no switch has happened yet.
        p.CurrentSpoolId = RunoutSpoolId;
        p.CurrentMaterial = "PLA";
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, t0, t1);

        RunoutSwitchAssessment result = await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    [Fact]
    public async Task AssessAsync_ManualSpoolBindingMovedToBackupWithoutTelemetry_ReturnsBackupAvailable()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = BackupSpoolId;
        // These are operator-managed persisted bindings, not live switch telemetry.
        p.CurrentSpoolId = BackupSpoolId;
        p.CurrentMaterial = "PLA";
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, t0, t1);

        RunoutSwitchAssessment result = await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    [Fact]
    public async Task AssessAsync_FreshActiveToolSelectsFallback_ReturnsSwitchConfirmed()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, t0, t1);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: -1,
                FilamentState: "Loaded",
                Action: "Idle",
                NumGates: 0,
                HasBypass: false,
                EndlessSpool: false,
                ClogDetection: false,
                Gates: []));

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.SwitchConfirmed);
    }

    [Fact]
    public async Task AssessAsync_FreshActiveGateSelectsFallback_ReturnsSwitchConfirmed()
    {
        (Printer p, Toolhead sourceGate, Toolhead gate) =
            await SeedAsync(mmuTopology: true);
        gate.CurrentMaterial = "PLA";
        gate.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, sourceGate, gate);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: 1,
                FilamentState: "Loaded",
                Action: "Idle",
                NumGates: 2,
                HasBypass: false,
                EndlessSpool: true,
                ClogDetection: true,
                Gates:
                [
                    new MmuGateDto(0, 0, null, null, null, -1),
                    new MmuGateDto(1, 1, "PLA", null, null, BackupSpoolId)
                ]));

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(
                // The warning carries the 0-based G-code index (issue #711, round-19 M19-2), not
                // the raw 1-based stored gate index.
                RunoutWarning(p, ToolheadIndexMapper.ToGcodeToolIndex(sourceGate) ?? 0),
                CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.SwitchConfirmed);
    }

    // H19-2 (issue #711, round-19): a settled/loaded fallback gate alone must not be classified as
    // SwitchConfirmed unless the printer's fresh status confirms an active print. Paused, idle, and
    // errored printers with the fallback gate selected have NOT proven the print continued — they
    // must downgrade only to BackupAvailable so the operator still sees a deadline.
    [Theory]
    [InlineData("Paused")]
    [InlineData("Idle")]
    [InlineData("Error")]
    public async Task AssessAsync_SettledMmuFallbackGate_PrinterNotConfirmedPrinting_ReturnsBackupAvailable(
        string state)
    {
        (Printer p, Toolhead sourceGate, Toolhead gate) =
            await SeedAsync(mmuTopology: true);
        gate.CurrentMaterial = "PLA";
        gate.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, sourceGate, gate);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: 1,
                FilamentState: "Loaded",
                Action: "Idle",
                NumGates: 2,
                HasBypass: false,
                EndlessSpool: true,
                ClogDetection: true,
                Gates:
                [
                    new MmuGateDto(0, 0, null, null, null, -1),
                    new MmuGateDto(1, 1, "PLA", null, null, BackupSpoolId)
                ]),
            state: state);

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(
                // The warning carries the 0-based G-code index (issue #711, round-19 M19-2), not
                // the raw 1-based stored gate index.
                RunoutWarning(p, ToolheadIndexMapper.ToGcodeToolIndex(sourceGate) ?? 0),
                CancellationToken.None);

        result.Should().Be(
            RunoutSwitchAssessment.BackupAvailable,
            $"a '{state}' printer has not proven printing continued even with a settled fallback gate");
    }

    [Fact]
    public async Task AssessAsync_LiveFallbackGateMaterialMismatch_ReturnsBackupAvailable()
    {
        (Printer p, Toolhead sourceGate, Toolhead gate) =
            await SeedAsync(mmuTopology: true);
        gate.CurrentMaterial = "PLA";
        gate.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, sourceGate, gate);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: 1,
                FilamentState: "Loaded",
                Action: "Idle",
                NumGates: 2,
                HasBypass: false,
                EndlessSpool: false,
                ClogDetection: false,
                Gates:
                [
                    new MmuGateDto(0, 0, null, null, null, -1),
                    new MmuGateDto(1, 1, "PETG", null, null, BackupSpoolId)
                ]));

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(
                // The warning carries the 0-based G-code index (issue #711, round-19 M19-2), not
                // the raw 1-based stored gate index.
                RunoutWarning(p, ToolheadIndexMapper.ToGcodeToolIndex(sourceGate) ?? 0),
                CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    // Finding H3 (a): selected gate + loaded + printing + matching material → SwitchConfirmed.
    [Fact]
    public async Task AssessAsync_SelectedGateLoadedPrintingMaterialMatch_ReturnsSwitchConfirmed()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Printing",
            gateMaterial: "PLA");

        result.Should().Be(RunoutSwitchAssessment.SwitchConfirmed);
    }

    // Finding H3 (b): selected gate reported Unloaded (switch not completed) → BackupAvailable.
    [Fact]
    public async Task AssessAsync_SelectedGateUnloaded_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Unloaded",
            action: "Idle",
            gateMaterial: "PLA");

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    // Finding H3 (c): selected gate mid-Loading (transitional action) → BackupAvailable.
    [Fact]
    public async Task AssessAsync_SelectedGateLoading_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Loading",
            gateMaterial: "PLA");

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    // Finding H3 (d): selected gate reported a failure/error action → BackupAvailable.
    [Fact]
    public async Task AssessAsync_SelectedGateFailed_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Failed",
            gateMaterial: "PLA");

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    // Finding H3 (e): selected gate loaded/settled but live material absent → BackupAvailable.
    [Fact]
    public async Task AssessAsync_SelectedGateLoadedMaterialMissing_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Idle",
            gateMaterial: "");

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    // Finding H3 (f): unknown/empty state is never treated as loaded (conservative) → BackupAvailable.
    [Fact]
    public async Task AssessAsync_SelectedGateUnknownState_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Unknown",
            action: "",
            gateMaterial: "PLA");

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    [Fact]
    public async Task AssessAsync_SelectedGateEmptyWithRetainedLoadedMetadata_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Idle",
            gateMaterial: "PLA",
            gateStatus: 0);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    [Fact]
    public async Task AssessAsync_SelectedGateAvailableWithLoadedMetadata_ReturnsSwitchConfirmed()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Idle",
            gateMaterial: "PLA",
            gateStatus: 1);

        result.Should().Be(RunoutSwitchAssessment.SwitchConfirmed);
    }

    [Fact]
    public async Task AssessAsync_SelectedGateUnknownOrTransitionalStatus_ReturnsBackupAvailable()
    {
        RunoutSwitchAssessment result = await AssessGateAsync(
            filamentState: "Loaded",
            action: "Idle",
            gateMaterial: "PLA",
            gateStatus: 2);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    private async Task<RunoutSwitchAssessment> AssessGateAsync(
        string? filamentState,
        string? action,
        string? gateMaterial,
        int gateStatus = 1)
    {
        (Printer p, Toolhead sourceGate, Toolhead gate) =
            await SeedAsync(mmuTopology: true);
        gate.CurrentMaterial = "PLA";
        gate.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, sourceGate, gate);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: 1,
                FilamentState: filamentState,
                Action: action,
                NumGates: 2,
                HasBypass: false,
                EndlessSpool: false,
                ClogDetection: false,
                Gates:
                [
                    new MmuGateDto(0, 0, null, null, null, -1),
                    new MmuGateDto(
                        1,
                        gateStatus,
                        gateMaterial,
                        null,
                        null,
                        BackupSpoolId)
                ]));

        return await _evaluator.AssessAsync(
            // The warning carries the 0-based G-code index (issue #711, round-19 M19-2), not the
            // raw 1-based stored gate index.
            RunoutWarning(p, ToolheadIndexMapper.ToGcodeToolIndex(sourceGate) ?? 0),
            CancellationToken.None);
    }

    [Fact]
    public async Task AssessAsync_BackendWithoutMmuStatus_ReturnsBackupAvailable()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, t0, t1);
        _statusCache
            .Setup(cache => cache.GetSnapshot(p.Id))
            .Returns(new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(p.Id, IsOnline: true, State: "printing"),
                DateTime.UtcNow));

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    [Fact]
    public async Task AssessAsync_StaleMmuStatus_ReturnsBackupAvailable()
    {
        (Printer p, Toolhead t0, Toolhead t1) = await SeedAsync();
        t1.CurrentMaterial = "PLA";
        t1.CurrentSpoolId = BackupSpoolId;
        await _db.SaveChangesAsync();
        await CreateGroupAsync(p, t0, t1);
        SetStatus(
            p,
            new MmuStatusDto(
                Enabled: true,
                IsHomed: true,
                ActiveTool: 1,
                ActiveGate: 1,
                FilamentState: "Loaded",
                Action: "Idle",
                NumGates: 2,
                HasBypass: false,
                EndlessSpool: false,
                ClogDetection: false,
                Gates: []),
            updatedAtUtc: DateTime.UtcNow - PrinterStatusFreshness.MaximumAge - TimeSpan.FromSeconds(1));

        RunoutSwitchAssessment result =
            await _evaluator.AssessAsync(RunoutWarning(p), CancellationToken.None);

        result.Should().Be(RunoutSwitchAssessment.BackupAvailable);
    }

    private static FilamentRunoutWarningDto RunoutWarning(
        Printer printer,
        int toolheadIndex = 0)
        => new(
            printer.Id,
            printer.Name,
            ToolheadIndex: toolheadIndex,
            SpoolId: RunoutSpoolId,
            Material: "PLA",
            RemainingGrams: 5,
            PredictedRunoutAt: new DateTime(2026, 7, 11, 4, 30, 0, DateTimeKind.Utc),
            Reason: "runout-during-active-job");

    private async Task CreateGroupAsync(Printer printer, Toolhead t0, Toolhead t1)
    {
        await _fallbackService.CreateAsync(
            printer.Id,
            new CreateFilamentFallbackGroupRequest("PLA Chain", "PLA", null, [t0.Id, t1.Id]),
            CancellationToken.None);
    }

    private void SetStatus(
        Printer printer,
        MmuStatusDto mmuStatus,
        DateTime? updatedAtUtc = null,
        string state = "printing")
    {
        _statusCache
            .Setup(cache => cache.GetSnapshot(printer.Id))
            .Returns(new PrinterStatusCacheSnapshot(
                new PrinterStatusDto(
                    printer.Id,
                    IsOnline: true,
                    State: state,
                    MmuStatus: mmuStatus),
                updatedAtUtc ?? DateTime.UtcNow));
    }

    private async Task<(Printer Printer, Toolhead T0, Toolhead T1)> SeedAsync(
        bool mmuTopology = false)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Mfg-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model-{suffix}" };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Runout-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = $"http://10.0.1.{(Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 240) + 2}",
            IsEnabled = true,
        };
        Toolhead physical = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            Name = "T0",
            ToolheadType = ToolheadType.Physical
        };
        Toolhead t0 = mmuTopology
            ? new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Index = 1,
                Name = "Gate 1",
                ToolheadType = ToolheadType.MmuGate
            }
            : physical;
        Toolhead t1 = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = mmuTopology ? 2 : 1,
            Name = mmuTopology ? "Gate 2" : "T1",
            ToolheadType = mmuTopology ? ToolheadType.MmuGate : ToolheadType.Physical
        };

        _db.Manufacturers.Add(mfg);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        if (mmuTopology)
        {
            _db.Toolheads.AddRange(physical, t0, t1);
        }
        else
        {
            _db.Toolheads.AddRange(t0, t1);
        }
        await _db.SaveChangesAsync();

        return (printer, t0, t1);
    }
}
