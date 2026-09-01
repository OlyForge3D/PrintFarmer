using Farm.Infrastructure.Normalization;

namespace Farm.Infrastructure.Tests.Infrastructure;

public class UrlCredentialRedactorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_ReturnsInputUnchanged_ForNullOrBlank(string? input)
    {
        UrlCredentialRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_LeavesSchemeUrlWithoutCredentialsUnchanged()
    {
        const string input = "http://printer.local:7125/status";

        UrlCredentialRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_LeavesSchemeLessHostWithoutCredentialsUnchanged()
    {
        const string input = "printer.local:7125";

        UrlCredentialRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_StripsUserInfo_ForBaselineSchemeUrl()
    {
        const string password = "s3cr3t";
        string input = $"http://admin:{password}@printer.local:7125/status";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("http://printer.local:7125/status");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_StripsUserInfo_ForSchemeLessUrlWhereUsernameLooksLikeAScheme()
    {
        // "admin" satisfies URI scheme grammar, so Uri.TryCreate parses this whole string as
        // an opaque URI with an empty Host/UserInfo -- exactly the fall-through this fix closes.
        const string password = "s3cr3t";
        string input = $"admin:{password}@printer.local";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("printer.local");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_StripsEntireUserInfo_WhenPasswordContainsAnAtSign()
    {
        // RFC 3986 userinfo is "everything up to the LAST unescaped '@'"; a password
        // containing a literal '@' must not be mistaken for the userinfo/host separator.
        const string password = "pa@ss@word";
        string input = $"admin:{password}@printer.local";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("printer.local");
        result.Should().NotContain(password);
        result.Should().NotContain("pa@ss");
        result.Should().NotContain("word@");
    }

    [Theory]
    [InlineData("admin:s3cr3t@[::1]", "[::1]")]
    [InlineData("admin:s3cr3t@[::1]:8080", "[::1]:8080")]
    [InlineData("admin:s3cr3t@[2001:db8::1]:7125", "[2001:db8::1]:7125")]
    public void Redact_StripsUserInfo_ForIpv6LiteralHosts(string input, string expected)
    {
        const string password = "s3cr3t";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be(expected);
        result.Should().NotContain(password);
    }

    [Theory]
    [InlineData("admin:s3cr3t@1.2.3.4", "1.2.3.4")]
    [InlineData("admin:s3cr3t@1.2.3.4:7125", "1.2.3.4:7125")]
    [InlineData("admin:s3cr3t@8printer.local", "8printer.local")]
    public void Redact_StripsUserInfo_ForHostsBeginningWithADigit(string input, string expected)
    {
        const string password = "s3cr3t";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be(expected);
        result.Should().NotContain(password);
    }

    [Theory]
    [InlineData("1.2.3.4:7125")]
    [InlineData("8printer.local:7125")]
    [InlineData("[::1]:7125")]
    public void Redact_LeavesSchemeLessNoCredentialHostsUnchanged(string input)
    {
        UrlCredentialRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_PreservesPathQueryAndFragment_ForSchemeLessInput()
    {
        const string password = "s3cr3t";
        string input = $"admin:{password}@printer.local/api/v1?x=1#frag";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("printer.local/api/v1?x=1#frag");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_PreservesSchemePathQueryAndFragment_ForSchemeUrl()
    {
        const string password = "s3cr3t";
        string input = $"https://admin:{password}@printer.local:7125/api/v1?x=1#frag";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("https://printer.local:7125/api/v1?x=1#frag");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_StripsCredential_ForUnparseableGarbageWithMultipleAtSigns()
    {
        // The password is followed by several stray '@' characters before the real host;
        // per RFC 3986 semantics everything up to the LAST '@' is userinfo, so all of it --
        // password and the stray separators alike -- must be discarded.
        const string password = "s3cr3t";
        string input = $"admin:{password}@@@@printer.local";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("printer.local");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_NeverThrows_AndReturnsPlaceholder_ForDegenerateAllAtSignInput()
    {
        string? result = UrlCredentialRedactor.Redact("@@@@");

        result.Should().Be("<redacted>");
    }
}
