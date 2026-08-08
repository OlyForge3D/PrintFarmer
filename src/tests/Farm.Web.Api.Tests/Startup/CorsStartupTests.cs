using Farm.Web.Api.Startup;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Verifies the CORS origin-validation policy never reflects an arbitrary origin while
/// credentials are allowed (issue #1254). "ALLOW_LOCAL_NETWORK=true" must widen acceptance
/// only to origins that actually resolve to a private/loopback network address, not to every
/// possible origin.
/// </summary>
public class CorsStartupTests
{
    private static readonly string[] ConfiguredOrigins =
    [
        "http://localhost:3000",
        "https://localhost:3000",
    ];

    [Fact]
    public void ConfiguredOrigin_IsAllowed_RegardlessOfLocalNetworkFlag()
    {
        Assert.True(CorsStartup.IsOriginAllowed("http://localhost:3000", ConfiguredOrigins, allowLocalNetwork: false));
        Assert.True(CorsStartup.IsOriginAllowed("http://localhost:3000", ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Fact]
    public void RandomExternalOrigin_IsRejected_WhenLocalNetworkDisabled()
    {
        Assert.False(CorsStartup.IsOriginAllowed("https://not-configured.example", ConfiguredOrigins, allowLocalNetwork: false));
    }

    [Fact]
    public void RandomExternalOrigin_IsRejected_EvenWhenLocalNetworkEnabled()
    {
        // This is the core regression check for #1254: ALLOW_LOCAL_NETWORK=true must never
        // reflect an arbitrary, non-local origin back as allowed.
        Assert.False(CorsStartup.IsOriginAllowed("https://not-configured.example", ConfiguredOrigins, allowLocalNetwork: true));
        Assert.False(CorsStartup.IsOriginAllowed("https://attacker.example", ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Fact]
    public void PublicIpOrigin_IsRejected_EvenWhenLocalNetworkEnabled()
    {
        Assert.False(CorsStartup.IsOriginAllowed("http://8.8.8.8:3000", ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Theory]
    [InlineData("http://192.168.1.50:3000")]
    [InlineData("http://10.0.0.5:3000")]
    [InlineData("http://172.16.4.1:3000")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("http://localhost:5173")]
    public void LocalNetworkOrigin_IsAllowed_WhenLocalNetworkEnabled(string origin)
    {
        Assert.True(CorsStartup.IsOriginAllowed(origin, ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Theory]
    [InlineData("http://192.168.1.50:3000")]
    [InlineData("http://127.0.0.1:3000")]
    public void LocalNetworkOrigin_IsRejected_WhenLocalNetworkDisabled(string origin)
    {
        Assert.False(CorsStartup.IsOriginAllowed(origin, ConfiguredOrigins, allowLocalNetwork: false));
    }

    [Fact]
    public void MalformedOrigin_IsRejected_WhenLocalNetworkEnabled()
    {
        Assert.False(CorsStartup.IsOriginAllowed("not-a-uri", ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Fact]
    public void UnresolvableLocalHostnameOrigin_IsRejected_WhenLocalNetworkEnabled()
    {
        // A ".local" hostname that cannot be resolved must fail closed rather than being allowed.
        Assert.False(CorsStartup.IsOriginAllowed(
            "http://this-host-does-not-exist.local:3000",
            ConfiguredOrigins,
            allowLocalNetwork: true));
    }

    [Fact]
    public void PublicHostnameOrigin_IsRejected_WhenLocalNetworkEnabled()
    {
        // example.com is a public, non-".local" hostname, so it must never be treated as
        // local-network — this is rejected outright without attempting DNS resolution.
        Assert.False(CorsStartup.IsOriginAllowed("https://example.com", ConfiguredOrigins, allowLocalNetwork: true));
    }

    [Theory]
    [InlineData("http://127.0.0.1.sslip.io:3000")]
    [InlineData("http://127-0-0-1.nip.io:3000")]
    public void DnsRebindingStyleHostname_IsRejected_EvenWhenLocalNetworkEnabled(string origin)
    {
        // Public "DNS rebinding" wildcard services let anyone register an ordinary internet
        // domain that resolves to a private/loopback address. Trusting DNS resolution for
        // arbitrary hostnames would let an attacker-controlled page pass this check, so only
        // the reserved ".local" mDNS TLD (RFC 6762, not delegable in public DNS) is resolved —
        // these non-".local" hostnames must be rejected without ever attempting resolution.
        Assert.False(CorsStartup.IsOriginAllowed(origin, ConfiguredOrigins, allowLocalNetwork: true));
    }
}
