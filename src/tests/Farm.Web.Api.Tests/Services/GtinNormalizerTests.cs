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
        // U+0660-U+0669), not just ASCII '0'-'9'. If stripping used IsDigit, these characters
        // would survive filtering and get treated as digits with value `c - '0'` (a huge,
        // out-of-range number for a non-ASCII code point), corrupting the checksum arithmetic.
        //
        // This exact string is a deliberately engineered counter-example, not an arbitrary one:
        // it replaces 3 of the 11 payload digits of the valid GTIN-12 "123456789012" with their
        // Arabic-Indic equivalents at positions whose GS1 weights (two weight-1, one weight-3)
        // sum to a multiple of 5. Because the Arabic-Indic code point offset from '0' is 0x630
        // (1584), which is congruent to 4 mod 10, replacing digits whose total weight is a
        // multiple of 5 shifts the mod-10 checksum by a multiple of 10 -- i.e. by zero. So under
        // the old buggy `IsDigit` filter, all 12 characters would count as "digits" (a *valid*
        // length), and the corrupted checksum arithmetic would *still validate* by this
        // engineered coincidence, silently producing a normalized "GTIN" that itself embeds
        // non-ASCII characters. This was verified against the pre-fix implementation.
        //
        // The ASCII-only filter strips the 3 Arabic-Indic characters entirely, leaving only 9
        // ASCII digits ("356789012") -- an invalid length -- so the fixed code correctly rejects
        // this input instead of silently corrupting it.
        const string mixedAsciiAndArabicIndicDigits = "\u0661\u06623\u066456789012";

        GtinNormalizer.Normalize(mixedAsciiAndArabicIndicDigits).Should().BeNull();
    }
}
