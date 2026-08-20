using Farm.Infrastructure.Normalization;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class GtinNormalizerTests
{
    // ── Valid inputs, each length, padded to 14 digits ──────────────────
    [Theory]
    [InlineData("40123455", "00000040123455")] // GTIN-8
    [InlineData("123456789012", "00123456789012")] // GTIN-12 (UPC-A)
    [InlineData("4006381333931", "04006381333931")] // GTIN-13 (EAN-13)
    [InlineData("04006381333931", "04006381333931")] // GTIN-14
    public void Normalize_ValidBarcode_ReturnsPadded14DigitGtin(string input, string expected)
    {
        GtinNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_Upc12AndEquivalentEan13_ProduceSameNormalizedValue()
    {
        // 850078714923 (UPC-12) is the same product as 0850078714923 (EAN-13, zero-padded);
        // leading zeros never change the GS1 mod-10 check digit.
        const string upc12 = "123456789012";
        const string ean13 = "0123456789012";

        string? normalizedUpc = GtinNormalizer.Normalize(upc12);
        string? normalizedEan = GtinNormalizer.Normalize(ean13);

        normalizedUpc.Should().NotBeNull();
        normalizedEan.Should().NotBeNull();
        normalizedUpc.Should().Be(normalizedEan);
    }

    [Fact]
    public void Normalize_StripsNonDigitCharacters()
    {
        GtinNormalizer.Normalize("123-456-789-012").Should().Be("00123456789012");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrWhitespace_ReturnsNull(string? input)
    {
        GtinNormalizer.Normalize(input).Should().BeNull();
    }

    [Theory]
    [InlineData("123")] // too short (3 digits)
    [InlineData("123456789")] // 9 digits: not 8/12/13/14
    [InlineData("123456789012345")] // 15 digits: too long
    public void Normalize_InvalidLength_ReturnsNull(string input)
    {
        GtinNormalizer.Normalize(input).Should().BeNull();
    }

    [Fact]
    public void Normalize_InvalidCheckDigit_ReturnsNull()
    {
        // Real example from production data: not a valid GTIN-8 (bad check digit) and must be
        // rejected rather than persisted.
        GtinNormalizer.Normalize("04850807Z").Should().BeNull();
    }

    [Fact]
    public void Normalize_NonAsciiDecimalDigits_AreNotTreatedAsDigits()
    {
        // char.IsDigit(char) matches any Unicode "Nd" decimal digit (e.g. Arabic-Indic
        // U+0660-U+0669), not just ASCII '0'-'9'. Filtering on IsDigit would let these through
        // and then corrupt the checksum arithmetic (which assumes `c - '0'` is 0-9). Stripping
        // must be ASCII-only, so an Arabic-Indic rendering of a valid barcode should strip down
        // to nothing usable and be rejected, not silently accepted as if it were ASCII digits.
        const string arabicIndicDigits = "\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669\u0660\u0661\u0662"; // "123456789012" in Arabic-Indic

        GtinNormalizer.Normalize(arabicIndicDigits).Should().BeNull();
    }
}
