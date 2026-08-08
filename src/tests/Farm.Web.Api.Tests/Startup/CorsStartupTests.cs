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
}
