using System.Net;
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
    public async Task CheckAsync_DirectIpDestination_PinsTheSameAddressForReuse()
    {
        // The vetted IP must be surfaced on the result so callers can pin the real outbound
        // connection to it rather than letting it be re-resolved independently at connect time.
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync("http://192.168.1.50:3333/");

        result.ResolvedAddress.Should().Be(IPAddress.Parse("192.168.1.50"));
    }

    [Fact]
    public async Task CheckAsync_LoopbackDestination_WithMatchingAllowedRange_IsAllowed()
    {
        EgressGuard guard = CreateGuard(allowedRanges: "127.0.0.1/32");

        EgressCheckResult result = await guard.CheckAsync("http://127.0.0.1:8080/");

        result.IsAllowed.Should().BeTrue();
        result.ResolvedAddress.Should().Be(IPAddress.Loopback);
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
    public async Task CheckAsync_UnresolvableHostname_FailsClosedRatherThanFailingOpen()
    {
        // A hostname that has never existed (reserved by RFC 2606) will not resolve in any
        // environment. A security guard must fail CLOSED on DNS resolution failure — letting an
        // unresolvable host through would let an attacker whose domain resolves intermittently
        // bypass vetting entirely.
        EgressGuard guard = CreateGuard();

        EgressCheckResult result = await guard.CheckAsync("http://this-host-does-not-exist.invalid:3333/");

        result.IsAllowed.Should().BeFalse();
        result.DenyReason.Should().NotBeNullOrWhiteSpace();
        result.ResolvedAddress.Should().BeNull();
    }

    [Fact]
    public void CreatePinnedUri_RewritesHostToLiteralIpAndPreservesRest()
    {
        var original = new Uri("http://printer.local:8080/api/v1/status?foo=bar");

        Uri pinned = EgressGuard.CreatePinnedUri(original, IPAddress.Parse("192.168.1.50"));

        pinned.Host.Should().Be("192.168.1.50");
        pinned.Port.Should().Be(8080);
        pinned.Scheme.Should().Be("http");
        pinned.PathAndQuery.Should().Be("/api/v1/status?foo=bar");
    }

    [Fact]
    public void CreatePinnedUri_BracketsIPv6Addresses()
    {
        var original = new Uri("https://printer.local:443/status");

        Uri pinned = EgressGuard.CreatePinnedUri(original, IPAddress.Parse("::1"));

        pinned.Host.Should().Be("[::1]");
        pinned.ToString().Should().StartWith("https://[::1]/");
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
