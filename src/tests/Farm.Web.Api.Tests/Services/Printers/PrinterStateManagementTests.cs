using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

public class PrinterStateManagementTests
{
    [Fact]
    public void NormalizeState_ReturnsNull_WhenInputIsNull()
    {
        string? result = PrinterStateNormalizer.NormalizeState(null);

        result.Should().BeNull();
    }

    [Fact]
    public void NormalizeState_ReturnsEmptyString_WhenInputIsEmpty()
    {
        string? result = PrinterStateNormalizer.NormalizeState(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeState_ConvertsMixedCaseToPascalCase()
    {
        string? result = PrinterStateNormalizer.NormalizeState("pRiNtInG");

        result.Should().Be("Printing");
    }

    [Fact]
    public void NormalizeState_LeavesPascalCaseUnchanged()
    {
        string? result = PrinterStateNormalizer.NormalizeState("Idle");

        result.Should().Be("Idle");
    }

    [Fact]
    public void NormalizeState_TrimsWhitespaceBeforeMapping()
    {
        string input = " printing";

        string? result = PrinterStateNormalizer.NormalizeState(input);

        result.Should().Be("Printing");
    }
}
