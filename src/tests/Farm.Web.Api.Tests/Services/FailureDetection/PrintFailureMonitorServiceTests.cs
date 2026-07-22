using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.FailureDetection;

public class PrintFailureMonitorServiceTests
{
    [Fact]
    public void EvaluateMonitoringWindow_WhenPrinterIsNotReportingPrinting_ReturnsIdle()
    {
        var status = new PrinterStatusDto(Guid.NewGuid(), true, "Idle");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: null,
            activeJobStartedAtUtc: null,
            utcNow: DateTime.UtcNow);

        result.ShouldMonitor.Should().BeFalse();
        result.IdleReason.Should().Be("Printer is not actively printing.");
    }

    [Fact]
    public void EvaluateMonitoringWindow_WhenTrackedJobIsStarting_ReturnsWarmupIdle()
    {
        DateTime utcNow = DateTime.UtcNow;
        var status = new PrinterStatusDto(Guid.NewGuid(), true, "Printing");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: PrintJobStatus.Starting,
            activeJobStartedAtUtc: utcNow,
            utcNow: utcNow);

        result.ShouldMonitor.Should().BeFalse();
        result.IdleReason.Should().Be("Print is starting — monitoring will begin after warmup.");
    }

    [Fact]
    public void EvaluateMonitoringWindow_WhenTrackedPrintJustStarted_ReturnsWarmupIdle()
    {
        DateTime utcNow = DateTime.UtcNow;
        var status = new PrinterStatusDto(Guid.NewGuid(), true, "Printing");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: PrintJobStatus.Printing,
            activeJobStartedAtUtc: utcNow.AddSeconds(-30),
            utcNow: utcNow);

        result.ShouldMonitor.Should().BeFalse();
        result.IdleReason.Should().Be("Print just started — monitoring will begin shortly.");
    }

    [Fact]
    public void EvaluateMonitoringWindow_WhenTrackedPrintIsOlderThanGracePeriod_ReturnsMonitoringReady()
    {
        DateTime utcNow = DateTime.UtcNow;
        var status = new PrinterStatusDto(Guid.NewGuid(), true, "Printing");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: PrintJobStatus.Printing,
            activeJobStartedAtUtc: utcNow.AddMinutes(-3),
            utcNow: utcNow);

        result.ShouldMonitor.Should().BeTrue();
        result.IdleReason.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateMonitoringWindow_WhenPrintIsUntrackedButPrinterReportsPrinting_ReturnsMonitoringReady()
    {
        var status = new PrinterStatusDto(Guid.NewGuid(), true, "Printing");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: null,
            activeJobStartedAtUtc: null,
            utcNow: DateTime.UtcNow);

        result.ShouldMonitor.Should().BeTrue();
        result.IdleReason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Printing")]
    [InlineData("Heating")]
    [InlineData("Pausing")]
    [InlineData("Paused")]
    [InlineData("Resuming")]
    [InlineData("printing")] // case-insensitive guard
    [InlineData("paused")]
    public void EvaluateMonitoringWindow_WhenStateIsActivePrintingJob_ReturnsMonitoringReady(string state)
    {
        var status = new PrinterStatusDto(Guid.NewGuid(), true, state);

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: null,
            activeJobStartedAtUtc: null,
            utcNow: DateTime.UtcNow);

        result.ShouldMonitor.Should().BeTrue();
        result.IdleReason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Idle")]
    [InlineData("Complete")]
    [InlineData("Cancelled")]
    [InlineData("Cancelling")]
    [InlineData("Error")]
    [InlineData("Offline")]
    [InlineData("Shutdown")]
    [InlineData("Disconnected")]
    public void EvaluateMonitoringWindow_WhenStateIsNotActivePrintingJob_ReturnsIdle(string state)
    {
        var status = new PrinterStatusDto(Guid.NewGuid(), true, state);

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: null,
            activeJobStartedAtUtc: null,
            utcNow: DateTime.UtcNow);

        result.ShouldMonitor.Should().BeFalse();
        result.IdleReason.Should().Be("Printer is not actively printing.");
    }

    [Fact]
    public void EvaluateMonitoringWindow_WhenPrinterIsOffline_ReturnsIdleEvenIfStateIsPrinting()
    {
        var status = new PrinterStatusDto(Guid.NewGuid(), IsOnline: false, "Printing");

        var result = PrintFailureMonitorService.EvaluateMonitoringWindow(
            status,
            activeJobStatus: null,
            activeJobStartedAtUtc: null,
            utcNow: DateTime.UtcNow);

        result.ShouldMonitor.Should().BeFalse();
        result.IdleReason.Should().Be("Printer is not actively printing.");
    }

    [Fact]
    public void ResolveJobContext_WhenLiveStatusIncludesNormalizedNames_PrefersLiveStatus()
    {
        var printerStatus = new PrinterStatusDto(
            Guid.NewGuid(),
            true,
            "Printing",
            JobName: "folder/active-print.gcode",
            FileName: "active-print.gcode");

        var result = PrintFailureMonitorService.ResolveJobContext(printerStatus, activeJobName: "Queued job");

        result.JobName.Should().Be("folder/active-print.gcode");
        result.FileName.Should().Be("active-print.gcode");
    }

    [Fact]
    public void ResolveJobContext_WhenLiveStatusMissingFileName_DerivesFileNameFromJobName()
    {
        var printerStatus = new PrinterStatusDto(
            Guid.NewGuid(),
            true,
            "Printing",
            JobName: ".cache/spaghetti-test.gcode");

        var result = PrintFailureMonitorService.ResolveJobContext(printerStatus, activeJobName: null);

        result.JobName.Should().Be(".cache/spaghetti-test.gcode");
        result.FileName.Should().Be("spaghetti-test.gcode");
    }

    [Fact]
    public void ResolveJobContext_WhenNoLiveStatus_FallsBackToTrackedJobName()
    {
        var result = PrintFailureMonitorService.ResolveJobContext(
            printerStatus: null,
            activeJobName: "Queued display name.gcode");

        result.JobName.Should().Be("Queued display name.gcode");
        result.FileName.Should().Be("Queued display name.gcode");
    }
}
