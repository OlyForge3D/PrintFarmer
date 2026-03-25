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
}
