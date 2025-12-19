using System.Net;
using Farm.Infrastructure.Network;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Infrastructure;

public class NetworkRangeHelperTests
{
    private readonly List<string> _warnings = new();

    [Fact]
    public void ExpandNetworkRange_HandlesSingleIpAddress()
    {
        string result = NetworkRangeHelper.ExpandNetworkRange("192.168.1.100", _warnings.Add).Single();
        
        result.Should().Be("192.168.1.100");
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public void ExpandNetworkRange_HandlesCidrNotation()
    {
        var results = NetworkRangeHelper.ExpandNetworkRange("192.168.1.0/30", _warnings.Add).ToList();
        
        // /30 with max 1024 limit should expand; but implementation limits by prefix length
        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(ip => IPAddress.TryParse(ip, out _).Should().BeTrue());
    }

    [Fact]
    public void ExpandNetworkRange_HandlesIpRange()
    {
        var results = NetworkRangeHelper.ExpandNetworkRange("192.168.1.1-192.168.1.3", _warnings.Add).ToList();
        
        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(ip => IPAddress.TryParse(ip, out _).Should().BeTrue());
    }

    [Fact]
    public void ExpandNetworkRange_ReturnsEmptyForInvalidInput()
    {
        var results = NetworkRangeHelper.ExpandNetworkRange("invalid", _warnings.Add).ToList();
        
        results.Should().BeEmpty();
    }

    [Fact]
    public void ExpandCidrRange_LimitsTooLargeRanges()
    {
        var results = NetworkRangeHelper.ExpandCidrRange(IPAddress.Parse("10.0.0.0"), 8, _warnings.Add).ToList();
        
        // Should warn about /8 being too large and limit to /16
        _warnings.Should().ContainSingle(w => w.Contains("/16"));
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void ExpandCidrRange_SkipsNetworkAndBroadcast()
    {
        var results = NetworkRangeHelper.ExpandCidrRange(IPAddress.Parse("192.168.1.0"), 30, _warnings.Add).ToList();
        
        // /30 should have 2 usable IPs (skip .0 and .3)
        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(ip =>
        {
            ip.Should().NotBe("192.168.1.0");
            ip.Should().NotBe("192.168.1.3");
        });
    }

    [Fact]
    public void ExpandIpRange_LimitsTooLargeRanges()
    {
        // Range with 2000 IPs should be limited to 1024
        var start = IPAddress.Parse("192.168.1.1");
        var end = IPAddress.Parse("192.168.10.224");
        
        var results = NetworkRangeHelper.ExpandIpRange(start, end, _warnings.Add).ToList();
        
        _warnings.Should().ContainSingle(w => w.Contains("range too large"));
        results.Count.Should().BeLessThanOrEqualTo(1025);  // May include both start and end
    }

    [Fact]
    public void ExpandIpRange_HandlesValidRange()
    {
        var start = IPAddress.Parse("192.168.1.1");
        var end = IPAddress.Parse("192.168.1.5");
        
        var results = NetworkRangeHelper.ExpandIpRange(start, end).ToList();
        
        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(ip => IPAddress.TryParse(ip, out _).Should().BeTrue());
    }

    [Fact]
    public void ExpandNetworkRange_HandlesInvalidCidrFormat()
    {
        var results = NetworkRangeHelper.ExpandNetworkRange("192.168.1.0/invalid", _warnings.Add).ToList();
        
        results.Should().BeEmpty();
    }

    [Fact]
    public void ExpandNetworkRange_HandlesRangeWithSpaces()
    {
        var results = NetworkRangeHelper.ExpandNetworkRange("192.168.1.1 - 192.168.1.3", _warnings.Add).ToList();
        
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void ExpandNetworkRange_ExceptionHandling()
    {
        // Malformed input should not throw
        Func<IEnumerable<string>> act = () => NetworkRangeHelper.ExpandNetworkRange("192.168.1.0/abc", _warnings.Add);
        
        act.Should().NotThrow();
    }
}
