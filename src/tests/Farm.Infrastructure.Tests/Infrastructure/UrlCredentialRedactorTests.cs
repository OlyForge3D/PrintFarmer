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

    [Fact]
    public void Redact_ReturnsPlaceholder_ForSchemeOnlyInputWithNoAuthority()
    {
        // Regression: the empty-remainder guard must inspect the *host* portion, not just
        // whether the whole redacted string is empty -- "http://" alone is a non-empty string
        // even though the authority it names is empty.
        string? result = UrlCredentialRedactor.Redact("http://@@@@");

        result.Should().Be("<redacted>");
    }

    [Fact]
    public void Redact_StripsCredential_WhenPathContainsASchemeLikeSeparator()
    {
        // Regression: a scheme separator must only be honored at the very start of the
        // string. Searching for "://" anywhere in the value let a later path/query segment
        // that itself contains "://" move the authority boundary past the real credentials,
        // leaking the password verbatim.
        const string password = "s3cr3t";
        string input = $"admin:{password}@printer.local/a://b";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("printer.local/a://b");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_ReturnsPlaceholder_WhenAStraySchemeLikeSeparatorHidesALaterAtSign()
    {
        // Regression: when a later "@" appears at or beyond the first path/query/fragment
        // delimiter (here inside "x://y@z"), the real userinfo boundary can no longer be
        // trusted -- attempting a partial strip risks landing in the wrong place (e.g.
        // stripping far more of the path than intended, or leaving part of the password
        // behind). Fail closed to the placeholder instead.
        const string password = "s3cr3t";
        string input = $"admin:{password}@printer.local/x://y@z";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("<redacted>");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_LeavesSchemeUrlUnaffected_ByLaterSchemeLikeSeparatorInQuery()
    {
        // A well-formed leading scheme must still be honored normally even when a later query
        // parameter itself contains "://" (e.g. an embedded callback URL).
        const string password = "s3cr3t";
        string input = $"http://admin:{password}@printer.local/api?redirect=http://evil.example";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("http://printer.local/api?redirect=http://evil.example");
        result.Should().NotContain(password);
    }

    [Fact]
    public void Redact_ReturnsPlaceholder_WhenEarlierStrayAtSignPrecedesADelimiterBeforeTheRealSeparator()
    {
        // Regression: a naive "search only up to the first delimiter" scan would find the
        // stray '@' inside "pa@ss" and treat it as the real separator, leaving "ss/word@..."
        // -- including part of the password -- in the output. The true last '@' (the one
        // before "printer.local") sits past the '/' delimiter, so this must fail closed
        // instead of stripping to the wrong '@'.
        const string password = "pa@ss";
        string input = $"admin:{password}/word@printer.local";

        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("<redacted>");
        result.Should().NotContain(password);
        result.Should().NotContain("ss/word");
    }

    [Theory]
    [InlineData("admin:pa/ss@printer.local")]
    [InlineData("admin:pa?ss@printer.local")]
    [InlineData("admin:pa#ss@printer.local")]
    public void Redact_ReturnsPlaceholder_WhenUnescapedDelimiterAppearsInsideUserInfo(string input)
    {
        // Regression: an unescaped '/', '?', or '#' inside malformed userinfo used to stop the
        // authority scan before it ever reached the real '@', so the whole value -- password
        // included -- was returned unchanged. Since the true host boundary can no longer be
        // trusted once this happens, the helper must fail closed to a placeholder rather than
        // ever echo the raw value back.
        string? result = UrlCredentialRedactor.Redact(input);

        result.Should().Be("<redacted>");
        result.Should().NotContain("pa");
    }
}
