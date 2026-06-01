using System;
using Farm.Infrastructure.Services.Cameras;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cameras;

/// <summary>
/// Unit tests for <see cref="CameraUrlValidator.IsUrlSafeForProbing"/>.
/// <para>
/// This validator is shared by <see cref="CameraHealthMonitorService"/> and
/// <see cref="CameraSnapshotService"/> to prevent SSRF attacks.
/// Private IPs (10.x, 192.168.x, 172.16-31.x) are intentionally allowed because
/// this application manages printers on a local network.
/// </para>
/// </summary>
public class CameraHealthMonitorServiceTests
{
    private static bool IsUrlSafe(string url) => CameraUrlValidator.IsUrlSafeForProbing(url);

    [Fact]
    public void IsUrlSafeForProbing_WithHttpUrl_ReturnsTrue()
    {
        IsUrlSafe("http://192.168.1.100/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithHttpsUrl_ReturnsTrue()
    {
        IsUrlSafe("https://192.168.1.100/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithRtspUrl_ReturnsTrue()
    {
        IsUrlSafe("rtsp://192.168.1.100:8554/stream").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithRtspUppercaseScheme_ReturnsTrue()
    {
        IsUrlSafe("RTSP://192.168.1.100:8554/stream").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithPrivateClass10Ip_ReturnsTrue()
    {
        IsUrlSafe("http://10.0.0.1/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithPrivateClass192Ip_ReturnsTrue()
    {
        IsUrlSafe("http://192.168.10.50/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithPrivate172Ip_ReturnsTrue()
    {
        IsUrlSafe("http://172.20.0.5/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithLocalhostByName_ReturnsFalse()
    {
        IsUrlSafe("http://localhost/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithLocalhostUppercase_ReturnsFalse()
    {
        IsUrlSafe("http://LOCALHOST/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_With127_0_0_1_ReturnsFalse()
    {
        IsUrlSafe("http://127.0.0.1/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4Loopback127_x_x_x_ReturnsFalse()
    {
        IsUrlSafe("http://127.255.255.255/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv6LoopbackShort_ReturnsFalse()
    {
        IsUrlSafe("http://::1/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv6LoopbackBracketed_ReturnsFalse()
    {
        IsUrlSafe("http://[::1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithLinkLocal169_254_ReturnsFalse()
    {
        IsUrlSafe("http://169.254.169.254/latest/meta-data/").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithLinkLocal169_254_OtherAddress_ReturnsFalse()
    {
        IsUrlSafe("http://169.254.0.1/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4CompatibleIpv6Loopback_ReturnsFalse()
    {
        IsUrlSafe("http://[::127.0.0.1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4CompatibleIpv6LinkLocal_ReturnsFalse()
    {
        IsUrlSafe("http://[::169.254.1.1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4CompatibleIpv6PrivateClass10Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[::10.0.0.1]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4CompatibleIpv6PrivateClass192Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[::192.168.1.1]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithNat64Loopback_ReturnsFalse()
    {
        IsUrlSafe("http://[64:ff9b::7f00:1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithNat64LinkLocal_ReturnsFalse()
    {
        IsUrlSafe("http://[64:ff9b::a9fe:0101]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithNat64PrivateClass10Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[64:ff9b::0a00:0001]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithNat64PrivateClass192Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[64:ff9b::c0a8:0101]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_With6to4Loopback_ReturnsFalse()
    {
        IsUrlSafe("http://[2002:7f00:0001::1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_With6to4LinkLocal_ReturnsFalse()
    {
        IsUrlSafe("http://[2002:a9fe:0101::1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_With6to4PrivateClass10Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[2002:0a00:0001::1]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_With6to4PrivateClass192Ip_ReturnsTrue()
    {
        IsUrlSafe("http://[2002:c0a8:0101::1]/snapshot.jpg").Should().BeTrue();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithFileScheme_ReturnsFalse()
    {
        IsUrlSafe("file:///etc/passwd").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithFtpScheme_ReturnsFalse()
    {
        IsUrlSafe("ftp://192.168.1.1/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithMalformedUrl_ReturnsFalse()
    {
        IsUrlSafe("not-a-url").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithEmptyString_ReturnsFalse()
    {
        IsUrlSafe(string.Empty).Should().BeFalse();
    }

    // IPv6 SSRF coverage (#266)

    [Fact]
    public void IsUrlSafeForProbing_WithIpv4MappedLoopback_ReturnsFalse()
    {
        // ::ffff:127.0.0.1 maps to 127.0.0.1 — must be blocked.
        IsUrlSafe("http://[::ffff:127.0.0.1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv6LinkLocal_ReturnsFalse()
    {
        // fe80::/10 link-local addresses must be blocked.
        IsUrlSafe("http://[fe80::1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv6UniqueLocal_ReturnsFalse()
    {
        // fd00::/8 unique-local (ULA) addresses must be blocked.
        IsUrlSafe("http://[fd00::1]/snapshot.jpg").Should().BeFalse();
    }

    [Fact]
    public void IsUrlSafeForProbing_WithIpv6Unspecified_ReturnsFalse()
    {
        // :: (IPv6Any / unspecified) must be blocked.
        IsUrlSafe("http://[::]/snapshot.jpg").Should().BeFalse();
    }
}
