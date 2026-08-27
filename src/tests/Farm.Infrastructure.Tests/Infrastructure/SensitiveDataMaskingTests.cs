using Farm.Infrastructure.Logging;
using FluentAssertions;

namespace Farm.Infrastructure.Tests.Infrastructure;

public class SensitiveDataMaskingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskEmail_WithNullOrWhitespace_ReturnsUnknown(string? email)
    {
        SensitiveDataMasking.MaskEmail(email).Should().Be("unknown");
    }

    [Fact]
    public void MaskEmail_WithNoAtSign_MasksAsGenericIdentifier()
    {
        SensitiveDataMasking.MaskEmail("notanemail").Should().Be("no***");
    }

    [Fact]
    public void MaskEmail_WithShortGenericValue_MasksFully()
    {
        SensitiveDataMasking.MaskEmail("ab").Should().Be("***");
    }

    [Theory]
    [InlineData("a@example.com")]
    [InlineData("ab@example.com")]
    public void MaskEmail_WithShortLocalPart_MasksLocalPartFully(string email)
    {
        SensitiveDataMasking.MaskEmail(email).Should().Be("***@example.com");
    }

    [Fact]
    public void MaskEmail_WithNormalLocalPart_KeepsFirstAndLastCharOnly()
    {
        SensitiveDataMasking.MaskEmail("jsmith@example.com").Should().Be("j***h@example.com");
    }

    [Fact]
    public void MaskEmail_WithThreeCharLocalPart_KeepsFirstAndLastChar()
    {
        SensitiveDataMasking.MaskEmail("abc@example.com").Should().Be("a***c@example.com");
    }

    [Fact]
    public void MaskEmail_NeverIncludesFullLocalPartInOutput()
    {
        string masked = SensitiveDataMasking.MaskEmail("jsmith@example.com");
        masked.Should().NotContain("jsmith");
        masked.Should().Contain("example.com");
    }

    [Fact]
    public void MaskIfEmail_WithEmailValue_Masks()
    {
        SensitiveDataMasking.MaskIfEmail("jsmith@example.com").Should().Be("j***h@example.com");
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("some-rate-limit-key")]
    public void MaskIfEmail_WithNonEmailValue_ReturnsUnchanged(string value)
    {
        SensitiveDataMasking.MaskIfEmail(value).Should().Be(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MaskIfEmail_WithNullOrEmpty_ReturnsUnknown(string? value)
    {
        SensitiveDataMasking.MaskIfEmail(value).Should().Be("unknown");
    }

    [Fact]
    public void MaskEmail_WithControlCharactersInDomain_SanitizesDomain()
    {
        // A malicious domain containing CR/LF must not be able to forge a new log line via
        // MaskEmail's output (cs/log-forging).
        string masked = SensitiveDataMasking.MaskEmail("jsmith@evil.com\r\nFAKE LOG LINE");
        masked.Should().Be("j***h@evil.com\\r\\nFAKE LOG LINE");
        masked.Should().NotContain("\r");
        masked.Should().NotContain("\n");
    }

    [Fact]
    public void MaskEmail_WithControlCharactersInLocalPart_SanitizesLocalPart()
    {
        // Local part must be longer than 2 characters to exercise the branch that
        // interpolates local[0]/local[^1] directly (the ">2" branch); a local part
        // of length <= 2 collapses to "***" and never emits the raw characters.
        string masked = SensitiveDataMasking.MaskEmail("\rbc@example.com");
        masked.Should().Be("\\r***c@example.com");
        masked.Should().NotContain("\r");
    }

    [Fact]
    public void MaskIfEmail_WithControlCharactersInNonEmailValue_SanitizesValue()
    {
        string masked = SensitiveDataMasking.MaskIfEmail("some-key\r\ninjected");
        masked.Should().Be("some-key\\r\\ninjected");
    }
}
