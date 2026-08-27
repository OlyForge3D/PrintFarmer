using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Domain;

public sealed class PrintJobOccupancyTests
{
    [Theory]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Printing)]
    [InlineData(PrintJobStatus.Paused)]
    public void OccupiesPrinter_OnBedStatus_ReturnsTrue(PrintJobStatus status)
    {
        status.OccupiesPrinter().Should().BeTrue();
    }

    [Theory]
    [InlineData(PrintJobStatus.Queued)]
    [InlineData(PrintJobStatus.Assigned)]
    [InlineData(PrintJobStatus.Completed)]
    [InlineData(PrintJobStatus.Failed)]
    [InlineData(PrintJobStatus.Cancelled)]
    public void OccupiesPrinter_OffBedStatus_ReturnsFalse(PrintJobStatus status)
    {
        status.OccupiesPrinter().Should().BeFalse();
    }
}
