using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Unit tests for <see cref="PrintersService.FormatRtspHost"/>.
/// Verifies that bare IPv6 addresses are bracketed for RFC 3986-compliant RTSP URL construction
/// (fixes Bishop blocker A / Hicks blocker from PR #428).
/// </summary>
public class PrintersServiceRtspHostTests
{
    [Theory]
    [InlineData("2001:db8::1",      "[2001:db8::1]")]
    [InlineData("::1",              "[::1]")]
    [InlineData("2001:db8:85a3::8a2e:370:7334", "[2001:db8:85a3::8a2e:370:7334]")]
    public void FormatRtspHost_BareIpv6_ReturnsBracketed(string input, string expected)
    {
        string result = PrintersService.FormatRtspHost(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("[2001:db8::1]")]
    [InlineData("[::1]")]
    public void FormatRtspHost_AlreadyBracketed_PassesThrough(string input)
    {
        string result = PrintersService.FormatRtspHost(input);
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    public void FormatRtspHost_Ipv4_ReturnsUnchanged(string input)
    {
        string result = PrintersService.FormatRtspHost(input);
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("myprinter.local")]
    [InlineData("cam.example.com")]
    public void FormatRtspHost_Hostname_ReturnsUnchanged(string input)
    {
        string result = PrintersService.FormatRtspHost(input);
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("2001:db8::1",      "rtsp://[2001:db8::1]:554/live/")]
    [InlineData("192.168.1.50",     "rtsp://192.168.1.50:554/live/")]
    [InlineData("[2001:db8::1]",    "rtsp://[2001:db8::1]:554/live/")]
    [InlineData("myprinter.local",  "rtsp://myprinter.local:554/live/")]
    public void FormatRtspHost_RtspUrlConstruction_ProducesValidUrl(string host, string expectedUrl)
    {
        string rtspUrl = $"rtsp://{PrintersService.FormatRtspHost(host)}:554/live/";
        rtspUrl.Should().Be(expectedUrl);
    }
}
