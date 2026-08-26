using Farm.Modules.Abstractions.Normalization;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Abstractions.Tests;

public sealed class CatalogNameNormalizerTests
{
    [Theory]
    [InlineData("prusa", "Prusa")]
    [InlineData("PRUSA RESEARCH", "Prusa")]
    [InlineData("flash forge", "FlashForge")]
    [InlineData("bambu lab", "BambuLab")]
    [InlineData("e-sun", "eSun")]
    public void NormalizeManufacturer_KnownAlias_ReturnsCanonicalStylization(string input, string expected)
    {
        CatalogNameNormalizer.NormalizeManufacturer(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeManufacturer_UnknownName_CapitalizesFirstLetterOnly()
    {
        CatalogNameNormalizer.NormalizeManufacturer("acme printers").Should().Be("Acme printers");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeManufacturer_BlankInput_ReturnsEmpty(string? input)
    {
        CatalogNameNormalizer.NormalizeManufacturer(input).Should().Be(string.Empty);
    }

    [Fact]
    public void NormalizeModel_CapitalizesFirstLetterOnly()
    {
        CatalogNameNormalizer.NormalizeModel("mk4s").Should().Be("Mk4s");
    }

    [Fact]
    public void Normalize_DelegatesToNormalizeModel()
    {
        CatalogNameNormalizer.Normalize("mk4s").Should().Be(CatalogNameNormalizer.NormalizeModel("mk4s"));
    }
}
