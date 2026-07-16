using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

/// <summary>
/// Tests for <see cref="MaintenanceIdleWindowShiftPlanTaskSource"/> covering Fix 11:
/// dispatch-eligible windows are skipped, under-lead windows are dropped,
/// and alert message flows into the spec description.
/// </summary>
public class MaintenanceIdleWindowShiftPlanTaskSourceTests
{
    private static readonly Guid PrinterId = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");
    private static readonly Guid AlertId = Guid.Parse("B1B1B1B1-B1B1-B1B1-B1B1-B1B1B1B1B1B1");

    private readonly Mock<IMaintenanceAlertRepository> _alertsRepo = new();
    private readonly Mock<IIdleWindowService> _idleWindows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IOperatorFeatureGate> _featureGate = new();

    public MaintenanceIdleWindowShiftPlanTaskSourceTests()
    {
        // Default: multi-slot fallback enabled so per-tool alerts flow through.
        // Individual tests flip this to exercise the gate-off filter (Finding H5).
        _featureGate.Setup(g => g.IsEnabled(It.IsAny<OperatorFeature>())).Returns(true);
    }

    private void SetupSettings(int minIdleMinutes = 10, int leadMinutes = 5)
    {
        _settings.Setup(s => s.Get<ShiftPlanSettings>()).Returns(new ShiftPlanSettings
        {
            MinIdleWindowMinutes = minIdleMinutes,
            MaintenanceLeadMinutes = leadMinutes,
        });
    }

    private MaintenanceIdleWindowShiftPlanTaskSource BuildSource()
        => new(
            _alertsRepo.Object,
            _idleWindows.Object,
            _settings.Object,
            _featureGate.Object,
            NullLogger<MaintenanceIdleWindowShiftPlanTaskSource>.Instance);

    private static MaintenanceAlert BuildAlert(string title = "Check nozzle", string message = "Nozzle needs cleaning.", Guid? toolheadId = null)
        => new()
        {
            Id = AlertId,
            PrinterId = PrinterId,
            Title = title,
            Message = message,
            Severity = 2,
            Status = MaintenanceAlertStatus.Active,
            ToolheadId = toolheadId,
        };

    // -------------------------------------------------------------------------
    // Fix 11: dispatch-eligible window → skipped
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fix 11: if the printer's idle window is dispatch-eligible (a job would be sent
    /// there), the maintenance task is not emitted so the compiler does not compete
    /// with the dispatcher.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_DispatchEligibleWindow_SkipsAlert()
    {
        SetupSettings(minIdleMinutes: 10, leadMinutes: 0);

        DateTime now = DateTime.UtcNow;
        IdleWindow eligibleWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddHours(2),
            IsDispatchEligibleNow: true); // dispatcher would fill this slot

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert() });
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow> { eligibleWindow }, new HashSet<Guid>()));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        Assert.Empty(specs);
    }

    // -------------------------------------------------------------------------
    // Fix 11: window shorter than MinIdleWindow after lead-buffer is dropped
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fix 11: if the window minus lead-minutes is shorter than MinIdleWindowMinutes,
    /// the spec is not emitted (the window is effectively too short to schedule work).
    /// </summary>
    [Fact]
    public async Task ProduceAsync_WindowTooShortAfterLead_DropsSpec()
    {
        // MinIdleWindow=10min, Lead=5min; window is only 12 min wide.
        // After subtracting lead: 12-5=7 min remaining < 10 min → dropped.
        SetupSettings(minIdleMinutes: 10, leadMinutes: 5);

        DateTime now = DateTime.UtcNow;
        IdleWindow shortWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddMinutes(12),
            IsDispatchEligibleNow: false);

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert() });
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow> { shortWindow }, new HashSet<Guid>()));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        Assert.Empty(specs);
    }

    // -------------------------------------------------------------------------
    // Fix 11: alert message flows into task description
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fix 11: the alert's <c>Message</c> is surfaced as the spec's
    /// <see cref="ShiftPlanTaskSpec.Description"/> so operators see the detail.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_ValidWindow_AlertMessageFlowsIntoDescription()
    {
        SetupSettings(minIdleMinutes: 5, leadMinutes: 0);

        DateTime now = DateTime.UtcNow;
        IdleWindow goodWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddHours(2),
            IsDispatchEligibleNow: false);

        const string expectedMessage = "Nozzle is clogged — replace before next print.";
        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert(message: expectedMessage) });
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow> { goodWindow }, new HashSet<Guid>()));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(specs);
        Assert.Equal(expectedMessage, spec.Description);
        Assert.Equal(UserTaskSourceKind.Maintenance, spec.SourceKind);
        Assert.Equal($"maintenancealert:{AlertId}", spec.SourceId);
    }

    /// <summary>
    /// Fix A (issue #713 round 2): a repository failure must propagate out of
    /// ProduceAsync rather than being swallowed into an empty spec set. If it were
    /// swallowed, the compiler would treat Maintenance as successfully evaluated and
    /// auto-complete every open maintenance task. Propagation lets the compiler
    /// isolate the failure and suppress auto-complete for this pass.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_AlertRepositoryThrows_PropagatesInsteadOfReturningEmpty()
    {
        SetupSettings();
        InvalidOperationException boom = new("alert store offline");
        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(boom);

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));
        Assert.Same(boom, thrown);
    }

    /// <summary>
    /// Fix R4-1 (issue #713 round 4): when dispatch eligibility is indeterminate for a
    /// printer that has an active maintenance alert (every scorer threw, so
    /// IdleWindowService excluded it and reported it via IndeterminatePrinterIds),
    /// ProduceAsync must FAIL CLOSED by throwing. If it instead returned successfully
    /// with the printer merely absent from the window set, the compiler would treat
    /// Maintenance as a successful (spec-less) source and auto-complete the still-active
    /// maintenance task — then recreate a duplicate once scoring recovered (flapping).
    /// </summary>
    [Fact]
    public async Task ProduceAsync_AlertedPrinterIndeterminate_ThrowsToPreserveTasks()
    {
        SetupSettings();

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert() });

        // Scorer outage: the alerted printer is reported indeterminate (and thus absent
        // from Windows) rather than conclusively idle/busy.
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(
                new List<IdleWindow>(),
                new HashSet<Guid> { PrinterId }));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ProduceAsync(CancellationToken.None));
        Assert.Contains("indeterminate", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fix R4-1: indeterminate eligibility for an UNALERTED printer must NOT trip the
    /// fail-closed throw — only an alerted printer's outage risks the spurious
    /// auto-complete. The alerted printer still has a valid window, so its spec is
    /// emitted normally.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_UnrelatedPrinterIndeterminate_DoesNotThrow()
    {
        SetupSettings(minIdleMinutes: 5, leadMinutes: 0);

        Guid otherPrinterId = Guid.Parse("CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC");
        DateTime now = DateTime.UtcNow;
        IdleWindow goodWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddHours(2),
            IsDispatchEligibleNow: false);

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert() });

        // A different printer is indeterminate; the alerted printer has a real window.
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(
                new List<IdleWindow> { goodWindow },
                new HashSet<Guid> { otherPrinterId }));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(specs);
        Assert.Equal(UserTaskSourceKind.Maintenance, spec.SourceKind);
    }

    /// <summary>
    /// Fix 4: OwnedKinds declares exactly [Maintenance], used by the compiler
    /// for source-failure isolation.
    /// </summary>
    [Fact]
    public void OwnedKinds_ContainsMaintenance()
    {
        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        Assert.Equal([UserTaskSourceKind.Maintenance], source.OwnedKinds);
    }

    // -------------------------------------------------------------------------
    // Finding H5 (issue #711): per-tool maintenance is gated out of the shift plan
    // -------------------------------------------------------------------------

    /// <summary>
    /// Finding H5: when the MultiSlotFallback feature is OFF, an alert scoped to a
    /// specific toolhead must not be projected into a shift-plan task. The gate-off
    /// path only surfaces printer-wide maintenance.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_GateOff_PerToolAlertFilteredFromProjection()
    {
        SetupSettings(minIdleMinutes: 5, leadMinutes: 0);
        _featureGate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        _featureGate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        DateTime now = DateTime.UtcNow;
        IdleWindow goodWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddHours(2),
            IsDispatchEligibleNow: false);

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert(toolheadId: Guid.NewGuid()) });
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow> { goodWindow }, new HashSet<Guid>()));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        Assert.Empty(specs);
    }

    /// <summary>
    /// Finding H5: when the MultiSlotFallback feature is ON, a per-toolhead alert is
    /// projected normally. The gate only suppresses per-tool rows while disabled.
    /// </summary>
    [Fact]
    public async Task ProduceAsync_GateOn_PerToolAlertIncludedInProjection()
    {
        SetupSettings(minIdleMinutes: 5, leadMinutes: 0);
        _featureGate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        _featureGate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        DateTime now = DateTime.UtcNow;
        IdleWindow goodWindow = new(
            PrinterId,
            "TestPrinter",
            StartUtc: now,
            EndUtc: now.AddHours(2),
            IsDispatchEligibleNow: false);

        _alertsRepo.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MaintenanceAlert> { BuildAlert(toolheadId: Guid.NewGuid()) });
        _idleWindows.Setup(s => s.GetIdleWindowsWithIndeterminateAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdleWindowResult(new List<IdleWindow> { goodWindow }, new HashSet<Guid>()));

        MaintenanceIdleWindowShiftPlanTaskSource source = BuildSource();
        IReadOnlyList<ShiftPlanTaskSpec> specs = await source.ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(specs);
        Assert.Equal(UserTaskSourceKind.Maintenance, spec.SourceKind);
    }
}
