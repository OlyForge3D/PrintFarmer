using Farm.Web.Api.Startup;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression test for Bishop v3 recommendation and v2 blocker B1: the APNs
/// URL redaction MUST be applied for hosts that match the APNs
/// production/sandbox/development endpoints, and MUST leave non-APNs hosts
/// untouched. This unit-tests the helpers directly; a fuller end-to-end
/// OTel-exporter test exists as future work but is not required to lock the
/// redaction contract.
/// </summary>
public sealed class TelemetryApnsRedactionTests
{
    [Theory]
    [InlineData("api.push.apple.com", true)]
    [InlineData("api.sandbox.push.apple.com", true)]
    [InlineData("api.development.push.apple.com", true)]
    [InlineData("API.PUSH.APPLE.COM", true)]
    [InlineData("example.com", false)]
    [InlineData("push.apple.com", false)]
    [InlineData("", false)]
    public void IsApnsHost_MatchesOnlyKnownApnsHostsCaseInsensitively(string host, bool expected)
    {
        TelemetryStartup.IsApnsHost(host).Should().Be(expected);
    }

    [Theory]
    [InlineData(
        "/3/device/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
        "/3/device/<REDACTED>")]
    [InlineData("/3/device/tok", "/3/device/<REDACTED>")]
    [InlineData("/3/device/", "/3/device/")]
    [InlineData("/other/path", "/other/path")]
    // Hicks v3 blocker 1: tokens containing embedded slashes MUST be fully
    // redacted, not partially. A greedy tail-match ensures nothing after
    // `/3/device/` leaks. AbsolutePath cannot contain `?` or `#`, so the
    // input surface for this helper is safe.
    [InlineData("/3/device/AAAA/BBBB", "/3/device/<REDACTED>")]
    [InlineData("/3/device/tok/extra/segments", "/3/device/<REDACTED>")]
    public void RedactApnsTokenPath_RewritesTokenSegment(string input, string expected)
    {
        TelemetryStartup.RedactApnsTokenPath(input).Should().Be(expected);
    }

    [Fact]
    public void RedactApnsTokenPath_FullyRedactsEvenIfSuffixLooksLikeQuery()
    {
        // If a caller ever passes a raw URL fragment that includes what looks
        // like a query suffix (shouldn't happen — helper is only called with
        // Uri.AbsolutePath) the greedy match still scrubs everything.
        TelemetryStartup.RedactApnsTokenPath("/3/device/tokenabc?extra=1")
            .Should().Be("/3/device/<REDACTED>");
    }
}
