using Farm.Infrastructure.Network;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Network;

public class EgressGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]:8080/")]
    public async Task CheckAsync_LoopbackOrLinkLocalDestination_IsDenied(string url)
    {
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync(url);

        result.IsAllowed.Should().BeFalse();
        result.DenyReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CheckAsync_LanPrivateAddress_IsAllowed()
    {
        // RFC1918 private ranges are intentionally NOT blocked — PrintFarmer legitimately
        // talks to LAN printer/integration hosts.
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync("http://192.168.1.50:3333/");

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_LoopbackDestination_WithMatchingAllowedRange_IsAllowed()
    {
        EgressGuard guard = CreateGuard(allowedRanges: "127.0.0.1/32");

        EgressCheckResult result = await guard.CheckAsync("http://127.0.0.1:8080/");

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_LinkLocalDestination_WithoutMatchingAllowedRange_IsStillDenied()
    {
        EgressGuard guard = CreateGuard(allowedRanges: "10.0.0.0/8");

        EgressCheckResult result = await guard.CheckAsync("http://169.254.169.254/");

        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_NonHttpScheme_IsDenied()
    {
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync("ftp://example.com/file");

        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_UnresolvableHostname_FailsOpenRatherThanUsingDnsAsOracle()
    {
        // obico.local does not resolve in CI/sandbox environments. The guard must not treat
        // DNS resolution failure as a security decision — it allows the request through and
        // lets the real HTTP call fail naturally.
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync("http://obico.local:3333/");

        result.IsAllowed.Should().BeTrue();
    }

    private static EgressGuard CreateGuard(string? allowedRanges = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                allowedRanges is null
                    ? []
                    : new Dictionary<string, string?> { ["ALLOWED_NETWORK_RANGES"] = allowedRanges })
            .Build();

        return new EgressGuard(configuration, NullLogger<EgressGuard>.Instance);
    }
}
