using Farm.Infrastructure.Services.Printers;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class PrinterStateNormalizerTests
{
    [Theory]
    [InlineData("IDLE", "Idle")]
    [InlineData("idle", "Idle")]
    [InlineData("Idle", "Idle")]
    [InlineData("PRINTING", "Printing")]
    [InlineData("printing", "Printing")]
    [InlineData("Printing", "Printing")]
    [InlineData("PAUSED", "Paused")]
    [InlineData("paused", "Paused")]
    [InlineData("Paused", "Paused")]
    [InlineData("ERROR", "Error")]
    [InlineData("OFFLINE", "Offline")]
    [InlineData("CONNECTING", "Connecting")]
    public void NormalizeState_WithValidState_ReturnsPascalCase(string input, string expected)
    {
        var result = PrinterStateNormalizer.NormalizeState(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("a", "A")]
    [InlineData("Z", "Z")]
    [InlineData("z", "Z")]
    [InlineData("1", "1")]
    public void NormalizeState_WithSingleCharacter_NormalizesCorrectly(string input, string expected)
    {
        var result = PrinterStateNormalizer.NormalizeState(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeState_WithNullOrEmpty_ReturnsNullOrEmpty(string? input)
    {
        var result = PrinterStateNormalizer.NormalizeState(input);

        result.Should().Be(input);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void NormalizeState_WithWhitespace_NormalizesAsString(string input)
    {
        var result = PrinterStateNormalizer.NormalizeState(input);

        // Whitespace is treated like any other character
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void NormalizeState_WithMixedCase_ConvertsToProperCase()
    {
        var result = PrinterStateNormalizer.NormalizeState("IdLe");

        result.Should().Be("Idle");
    }

    [Fact]
    public void NormalizeState_WithNumbers_PreservesNumbers()
    {
        var result = PrinterStateNormalizer.NormalizeState("error123");

        result.Should().Be("Error123");
    }

    [Fact]
    public void NormalizeState_WithSpecialCharacters_PreservesCharacters()
    {
        var result = PrinterStateNormalizer.NormalizeState("error-code");

        result.Should().Be("Error-code");
    }

    [Theory]
    [InlineData("READY_TO_PRINT", "Ready_to_print")]
    [InlineData("NOT_RESPONDING", "Not_responding")]
    [InlineData("AUTO_LEVELING", "Auto_leveling")]
    public void NormalizeState_WithUnderscores_PreservesUnderscores(string input, string expected)
    {
        var result = PrinterStateNormalizer.NormalizeState(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizeState_IsStateless()
    {
        // Multiple calls with same input should return same result
        var result1 = PrinterStateNormalizer.NormalizeState("PRINTING");
        var result2 = PrinterStateNormalizer.NormalizeState("PRINTING");

        result1.Should().Be(result2);
    }

    [Fact]
    public void NormalizeState_WithVeryLongState_StillWorks()
    {
        var longState = "VERY_LONG_PRINTER_STATE_STRING_WITH_MANY_WORDS";

        var result = PrinterStateNormalizer.NormalizeState(longState);

        result!.Should().StartWith("V");
        result.Should().NotContain("VERY_LONG");
    }
}
