using Farm.Infrastructure.Network;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Infrastructure;

public class UrlSsrfValidatorTests
{
    // ─── Loopback (always rejected) ───────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1:8123")]
    [InlineData("http://127.0.0.2:8123")]
    [InlineData("http://127.255.255.255")]
    [InlineData("https://localhost:8123")]
    [InlineData("http://localhost")]
    [InlineData("http://[::1]:8123")]
    public void Validate_Loopback_Rejected(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("oopback");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8123")]
    [InlineData("https://localhost:8123")]
    [InlineData("http://[::1]:8123")]
    public void Validate_Loopback_RejectedEvenWithOverride(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: true);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("oopback");
    }

    // ─── Link-local (always rejected) ────────────────────────────────────

    [Theory]
    [InlineData("http://169.254.0.1:8123")]
    [InlineData("http://169.254.255.255")]
    public void Validate_LinkLocalIPv4_Rejected(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ink-local");
    }

    [Fact]
    public void Validate_LinkLocalIPv6_Rejected()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(
            "http://[fe80::1]:8123", allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ink-local");
    }

    [Theory]
    [InlineData("http://169.254.1.1:8123")]
    [InlineData("http://[fe80::1]:8123")]
    public void Validate_LinkLocal_RejectedEvenWithOverride(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: true);

        result.IsValid.Should().BeFalse();
    }

    // ─── Private ranges (rejected by default) ─────────────────────────────

    [Theory]
    [InlineData("http://10.0.0.1:8123")]
    [InlineData("http://10.255.255.255:8123")]
    [InlineData("http://172.16.0.1:8123")]
    [InlineData("http://172.31.255.255:8123")]
    [InlineData("http://192.168.0.1:8123")]
    [InlineData("http://192.168.1.100:8123")]
    public void Validate_PrivateIPv4_RejectedByDefault(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("rivate network");
    }

    [Fact]
    public void Validate_UniqueLocalIPv6_RejectedByDefault()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(
            "http://[fd00::1]:8123", allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("nique-local");
    }

    // ─── Private ranges (allowed with override) ───────────────────────────

    [Theory]
    [InlineData("http://10.0.0.1:8123")]
    [InlineData("http://172.16.0.1:8123")]
    [InlineData("http://192.168.1.100:8123")]
    public void Validate_PrivateIPv4_AllowedWithOverride(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: true);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_UniqueLocalIPv6_AllowedWithOverride()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(
            "http://[fd00::1]:8123", allowPrivateNetworkTargets: true);

        result.IsValid.Should().BeTrue();
    }

    // ─── Scheme validation ────────────────────────────────────────────────

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://evil.com")]
    [InlineData("dict://evil.com")]
    public void Validate_NonHttpScheme_Rejected(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("http");
    }

    // ─── Valid URLs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://homeassistant.example.com:8123")]
    [InlineData("https://ha.mydomain.org")]
    [InlineData("http://8.8.8.8:8123")]
    [InlineData("https://203.0.113.1:8123")]
    public void Validate_PublicUrl_Accepted(string url)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://homeassistant.local:8123")]
    [InlineData("http://ha.home:8123")]
    public void Validate_HostnameWithoutIp_Accepted(string url)
    {
        // Hostnames pass through — DNS resolution is not performed at validation time
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: false);

        result.IsValid.Should().BeTrue();
    }

    // ─── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullUrl_Rejected()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(null);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyUrl_Rejected()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate("");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RelativeUrl_Rejected()
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate("/api/states");

        result.IsValid.Should().BeFalse();
    }

    // ─── 172.x boundary cases ─────────────────────────────────────────────

    [Theory]
    [InlineData("http://172.15.255.255:8123", true)]   // Below private range
    [InlineData("http://172.32.0.1:8123", true)]       // Above private range
    public void Validate_172xBoundary_PublicAccepted(string url, bool expectedValid)
    {
        UrlSsrfValidationResult result = UrlSsrfValidator.Validate(url, allowPrivateNetworkTargets: false);

        result.IsValid.Should().Be(expectedValid);
    }
}
