using System.Diagnostics;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression coverage for APNs URL redaction helpers and both production
/// HttpClient instrumentation enrichers.
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
    [Fact]
    public void EnrichApnsHttpRequest_RedactsPathAndRemovesEveryQueryTag()
    {
        const string token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.push.apple.com/3/device/{token}?deviceToken={token}");
        using Activity activity = BuildLeakyActivity(token);

        TelemetryStartup.EnrichApnsHttpRequest(activity, request);

        AssertProductionTagsAreRedacted(activity, token);
    }

    [Fact]
    public void EnrichApnsHttpResponse_ReappliesRedactionAndRemovesEveryQueryTag()
    {
        const string token = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.sandbox.push.apple.com/3/device/{token}?token={token}");
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            RequestMessage = request,
        };
        using Activity activity = BuildLeakyActivity(token);

        TelemetryStartup.EnrichApnsHttpResponse(activity, response);

        AssertProductionTagsAreRedacted(activity, token);
    }

    private static Activity BuildLeakyActivity(string token)
    {
        var activity = new Activity("apns-test");
        _ = activity.Start();
        _ = activity.SetTag("url.full", $"https://api.push.apple.com/3/device/{token}?token={token}");
        _ = activity.SetTag("http.url", $"https://api.push.apple.com/3/device/{token}?token={token}");
        _ = activity.SetTag("url.path", $"/3/device/{token}");
        _ = activity.SetTag("http.request.path", $"/3/device/{token}");
        _ = activity.SetTag("url.query", $"token={token}");
        _ = activity.SetTag("http.request.query", $"token={token}");
        return activity;
    }

    private static void AssertProductionTagsAreRedacted(Activity activity, string token)
    {
        activity.GetTagItem("url.full")!.ToString().Should()
            .EndWith("/3/device/<REDACTED>")
            .And.NotContain("?");
        activity.GetTagItem("http.url")!.ToString().Should()
            .EndWith("/3/device/<REDACTED>")
            .And.NotContain("?");
        activity.GetTagItem("url.path").Should().Be("/3/device/<REDACTED>");
        activity.GetTagItem("http.request.path").Should().Be("/3/device/<REDACTED>");
        activity.GetTagItem("url.query").Should().BeNull();
        activity.GetTagItem("http.request.query").Should().BeNull();
        activity.TagObjects
            .Select(tag => tag.Value?.ToString() ?? string.Empty)
            .Should().OnlyContain(value => !value.Contains(token, StringComparison.Ordinal));
    }

}
