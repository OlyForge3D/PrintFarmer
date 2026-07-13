using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.ShiftPlan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

/// <summary>
/// Unit tests for <see cref="IdleWindowService"/> covering:
/// Fix 1: no idle window when active job present; window ends at 'now' when next-assigned job has no ETA.
/// Fix 2: IsDispatchEligibleNow alignment with real dispatcher gates.
/// </summary>
public class IdleWindowServiceTests
{
    private static readonly Guid PrinterId = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA");

    private readonly Mock<IQueueDataService> _queue = new();
    private readonly Mock<IDispatchScorer> _scorer = new();
    private readonly Mock<IDbContextFactory<AppDbContext>> _dbFactory = new();

    // -------------------------------------------------------------------------
    // Fix 1: active printer → no idle window
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(PrintJobStatus.Printing)]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Paused)]
    public async Task GetIdleWindowsAsync_PrinterHasActiveJob_EmitsNoWindow(PrintJobStatus activeStatus)
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob activeJob = BuildJob(PrinterId, activeStatus, estimateMinutes: null);

        SetupQueueData([printer], [activeJob]);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [], printerReady: false);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.FromMinutes(1));

        Assert.Empty(windows);
    }

    /// <summary>
    /// Fix 1: even if the active job has no estimated completion time, the printer is still busy.
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsAsync_ActiveJobWithNoEta_EmitsNoWindow()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob active = BuildJob(PrinterId, PrintJobStatus.Printing, estimateMinutes: null);

        SetupQueueData([printer], [active]);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [], printerReady: false);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.FromMinutes(1));

        Assert.Empty(windows);
    }

    /// <summary>
    /// Fix 1: a queued next job with no ETA closes the window immediately → window length = 0 →
    /// filtered by minWindow.
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsAsync_QueuedNextJobWithNoEta_WindowEndsNow_FilteredOut()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        // No active job, but a queued job waiting to start with no ETA.
        PrintJob queued = BuildJob(PrinterId, PrintJobStatus.Queued, estimateMinutes: null);

        SetupQueueData([printer], [queued]);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [], printerReady: false);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.FromMinutes(1));

        Assert.Empty(windows);
    }

    /// <summary>
    /// Fix 1: a printer with no jobs at all gets an open-ended idle window.
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsAsync_IdlePrinter_NoJobs_ReturnsOpenWindow()
    {
        Printer printer = BuildPrinter(autoDispatch: false);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: false, AutoDispatchMode.Auto, [], printerReady: false);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.Equal(PrinterId, windows[0].PrinterId);
        Assert.Equal(DateTime.MaxValue, windows[0].EndUtc);
    }

    // -------------------------------------------------------------------------
    // Fix 2: dispatch eligibility alignment
    // -------------------------------------------------------------------------

    /// <summary>Fix 2: global auto-dispatch disabled → eligible = false.</summary>
    [Fact]
    public async Task GetIdleWindowsAsync_GlobalDispatchDisabled_IsDispatchEligibleFalse()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: false, AutoDispatchMode.Auto, [candidate], printerReady: true);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.False(windows[0].IsDispatchEligibleNow);
    }

    /// <summary>Fix 2: manual dispatch mode → eligible = false even if enabled.</summary>
    [Fact]
    public async Task GetIdleWindowsAsync_ManualMode_IsDispatchEligibleFalse()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Manual, [candidate], printerReady: true);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.False(windows[0].IsDispatchEligibleNow);
    }

    /// <summary>Fix 2: per-printer auto-dispatch disabled → eligible = false.</summary>
    [Fact]
    public async Task GetIdleWindowsAsync_PerPrinterDispatchDisabled_IsDispatchEligibleFalse()
    {
        Printer printer = BuildPrinter(autoDispatch: false);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [candidate], printerReady: true);

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.False(windows[0].IsDispatchEligibleNow);
    }

    /// <summary>Fix 2: all gates pass and candidate scores above threshold → eligible = true.</summary>
    [Fact]
    public async Task GetIdleWindowsAsync_AllGatesPass_CandidateScores_IsDispatchEligibleTrue()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [candidate], printerReady: true);

        // Scorer returns a non-eliminated score above the default threshold (0.5).
        _scorer.Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DispatchScore>
            {
                new(PrinterId, "TestPrinter", TotalScore: 80.0, new Dictionary<string, FactorScore>(),
                    Eliminated: false, []),
            });

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.True(windows[0].IsDispatchEligibleNow);
    }

    // -------------------------------------------------------------------------
    // Fix R3-1: scorer failure must not fail open into "not eligible"
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fix R3-1: when the sole candidate's scoring throws, dispatch eligibility is
    /// unknown — the printer must be excluded from the idle-window set entirely
    /// (not reported with IsDispatchEligibleNow = false, which would let a
    /// maintenance source schedule work into a window that might actually be
    /// dispatch-eligible).
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsAsync_ScorerThrowsForSoleCandidate_PrinterExcludedFromWindows()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [candidate], printerReady: true);

        _scorer.Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scorer boom"));

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Empty(windows);
    }

    /// <summary>
    /// Fix R4-1: the same sole-candidate scorer outage that excludes the printer from
    /// the window set must ALSO surface that printer in
    /// <see cref="IdleWindowResult.IndeterminatePrinterIds"/>, so a fail-closed caller
    /// (the maintenance source) can distinguish "eligibility unknown" from "no window".
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsWithIndeterminateAsync_ScorerThrowsForSoleCandidate_PrinterReportedIndeterminate()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [candidate], printerReady: true);

        _scorer.Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scorer boom"));

        IdleWindowService svc = BuildService();
        IdleWindowResult result = await svc.GetIdleWindowsWithIndeterminateAsync(TimeSpan.Zero);

        Assert.Empty(result.Windows);
        Assert.Contains(PrinterId, result.IndeterminatePrinterIds);
    }

    /// <summary>
    /// Fix R4-1: when eligibility IS conclusively determined, the indeterminate set is
    /// empty — the fail-closed path is only for genuine scorer outages.
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsWithIndeterminateAsync_ConclusiveEligibility_IndeterminateSetEmpty()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob candidate = BuildJob(null, PrintJobStatus.Queued);

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [candidate], printerReady: true);

        _scorer.Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DispatchScore>
            {
                new(PrinterId, "TestPrinter", TotalScore: 80.0, new Dictionary<string, FactorScore>(),
                    Eliminated: false, []),
            });

        IdleWindowService svc = BuildService();
        IdleWindowResult result = await svc.GetIdleWindowsWithIndeterminateAsync(TimeSpan.Zero);

        Assert.Single(result.Windows);
        Assert.Empty(result.IndeterminatePrinterIds);
    }

    /// <summary>
    /// Fix R3-1: a scorer failure on one candidate does not tank the whole pass —
    /// if a later candidate scores conclusively above threshold, eligibility is
    /// still reported as true.
    /// </summary>
    [Fact]
    public async Task GetIdleWindowsAsync_ScorerThrowsForOneCandidateButAnotherScoresHigh_IsDispatchEligibleTrue()
    {
        Printer printer = BuildPrinter(autoDispatch: true);
        PrintJob failingCandidate = BuildJob(null, PrintJobStatus.Queued);
        failingCandidate.Priority = 0;
        PrintJob scoringCandidate = BuildJob(null, PrintJobStatus.Queued);
        scoringCandidate.Priority = 1;

        SetupQueueData([printer], []);
        SetupDb(globalEnabled: true, AutoDispatchMode.Auto, [failingCandidate, scoringCandidate], printerReady: true);

        _scorer.Setup(s => s.ScorePrintersForJobAsync(failingCandidate.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scorer boom"));
        _scorer.Setup(s => s.ScorePrintersForJobAsync(scoringCandidate.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DispatchScore>
            {
                new(PrinterId, "TestPrinter", TotalScore: 80.0, new Dictionary<string, FactorScore>(),
                    Eliminated: false, []),
            });

        IdleWindowService svc = BuildService();
        IReadOnlyList<IdleWindow> windows = await svc.GetIdleWindowsAsync(TimeSpan.Zero);

        Assert.Single(windows);
        Assert.True(windows[0].IsDispatchEligibleNow);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Printer BuildPrinter(bool autoDispatch)
        => new()
        {
            Id = PrinterId,
            Name = "TestPrinter",
            ServerUrl = "http://test-printer:7125",
            AutoDispatchEnabled = autoDispatch,
        };

    private static PrintJob BuildJob(Guid? assignedPrinterId, PrintJobStatus status, double? estimateMinutes = 60)
        => new()
        {
            Id = Guid.NewGuid(),
            AssignedPrinterId = assignedPrinterId,
            Status = status,
            EstimatedPrintTime = estimateMinutes.HasValue
                ? TimeSpan.FromMinutes(estimateMinutes.Value)
                : null,
            QueuePosition = 0,
            Priority = 1, // Normal = 1
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
        };

    private void SetupQueueData(List<Printer> printers, List<PrintJob> printerJobs)
    {
        _queue.Setup(q => q.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers);
        _queue.Setup(q => q.GetPrintJobsForPrinterAsync(PrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printerJobs);
    }

    private void SetupDb(
        bool globalEnabled,
        AutoDispatchMode globalMode,
        List<PrintJob> candidates,
        bool printerReady)
    {
        DispatchSettings settings = new()
        {
            AutoDispatchEnabled = globalEnabled,
            AutoDispatchMode = globalMode,
        };

        AppDbContext db = new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"IdleWindowTest_{Guid.NewGuid()}")
                .Options);

        db.DispatchSettings.Add(settings);
        db.PrintJobs.AddRange(candidates);

        // Add the printer and its dispatch state so the include query returns it.
        Printer dbPrinter = new()
        {
            Id = PrinterId,
            Name = "TestPrinter",
            ServerUrl = "http://test-printer:7125",
        };
        db.Printers.Add(dbPrinter);

        db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = PrinterId,
            Printer = dbPrinter,
            AutoDispatchState = printerReady ? AutoDispatchState.Ready : AutoDispatchState.None,
        });

        db.SaveChanges();

        _dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(db);
    }

    private IdleWindowService BuildService()
        => new(
            _queue.Object,
            _scorer.Object,
            _dbFactory.Object,
            NullLogger<IdleWindowService>.Instance);
}

