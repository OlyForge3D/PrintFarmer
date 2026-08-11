using Farm.Infrastructure.Logging;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Infrastructure;

public class LogSanitizerTests
{
    [Fact]
    public void Sanitize_StripsCarriageReturnAndLineFeed()
    {
        string result = LogSanitizer.Sanitize("line1\r\nline2")!;

        result.Should().Be("line1\\r\\nline2");
        result.Should().NotContain("\r");
        result.Should().NotContain("\n");
    }

    [Fact]
    public void Sanitize_StripsOtherControlCharacters()
    {
        string result = LogSanitizer.Sanitize("value\twith\0control\u001bchars")!;

        result.Should().Be("valuewithcontrolchars");
    }

    [Fact]
    public void Sanitize_StripsC1ControlCharacters()
    {
        // C1 controls (U+0080-U+009F, e.g. NEL U+0085) are Cc-category control characters
        // just like the ASCII C0 range, and some log viewers/terminals treat them as line
        // breaks — they must be stripped the same way as the ASCII control characters.
        string result = LogSanitizer.Sanitize("value\u0085with\u009fc1chars")!;

        result.Should().Be("valuewithc1chars");
    }

    [Fact]
    public void Sanitize_LeavesNormalTextUnchanged()
    {
        const string input = "Printer-01: status=online, temp=210.5C";

        string? result = LogSanitizer.Sanitize(input);

        result.Should().Be(input);
        result.Should().BeSameAs(input);
    }

    [Fact]
    public void Sanitize_LeavesNonAsciiPrintableTextUnchanged()
    {
        const string input = "Café ☕ résumé";

        LogSanitizer.Sanitize(input).Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_ReturnsInputUnchanged_ForNullOrEmpty(string? input)
    {
        LogSanitizer.Sanitize(input).Should().Be(input);
    }

    [Fact]
    public void Sanitize_PreventsLogLineInjection()
    {
        const string maliciousInput = "user123\r\n2024-01-01 ERROR Fake log entry injected";

        string result = LogSanitizer.Sanitize(maliciousInput)!;

        result.Should().NotContain("\r\n");
        result.Should().Contain("\\r\\n");
    }
}
