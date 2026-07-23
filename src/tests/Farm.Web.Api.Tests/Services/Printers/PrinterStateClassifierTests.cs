using Farm.Infrastructure.Services.Printers;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Printers;

public class PrinterStateClassifierTests
{
    [Theory]
    [InlineData("Printing")]
    [InlineData("Heating")]
    [InlineData("Pausing")]
    [InlineData("Paused")]
    [InlineData("Resuming")]
    [InlineData("printing")]
    [InlineData("PAUSED")]
    [InlineData("  Heating  ")]
    public void IsActivePrintingJob_ReturnsTrue_ForActiveStates(string state)
    {
        PrinterStateClassifier.IsActivePrintingJob(state).Should().BeTrue();
    }

    [Theory]
    [InlineData("Idle")]
    [InlineData("Complete")]
    [InlineData("Cancelled")]
    [InlineData("Cancelling")]
    [InlineData("Error")]
    [InlineData("Offline")]
    [InlineData("Shutdown")]
    [InlineData("Halted")]
    [InlineData("Disconnected")]
    [InlineData("Connecting")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsActivePrintingJob_ReturnsFalse_ForInactiveStates(string? state)
    {
        PrinterStateClassifier.IsActivePrintingJob(state).Should().BeFalse();
    }
}
