using Farm.Infrastructure.Network;
using Farm.Infrastructure.Settings;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Infrastructure;

public class NetworkValidationServiceTests
{
    [Fact]
    public void ValidateSettings_EmptySubnetsList_IsValid()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string>(),
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSettings_ValidCidr_IsValid()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/24" },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSettings_InvalidCidr_ReportsError()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/invalid" },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Invalid CIDR format"));
    }

    [Fact]
    public void ValidateSettings_TimeoutTooLow_ReportsError()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/24" },
            ClientTimeoutMs = 50,  // Below minimum of 100ms
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("timeout"));
    }

    [Fact]
    public void ValidateSettings_TimeoutTooHigh_ReportsError()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/24" },
            ClientTimeoutMs = 50000,  // Above maximum of 30,000ms
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("timeout"));
    }

    [Fact]
    public void ValidateSettings_MaxConcurrentRequestsTooLow_ReportsError()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/24" },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 0  // Below minimum of 1
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Max concurrent requests"));
    }

    [Fact]
    public void ValidateSettings_MaxConcurrentRequestsTooHigh_ReportsError()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string> { "192.168.1.0/24" },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 200  // Above maximum of 100
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Max concurrent requests"));
    }

    [Fact]
    public void ValidateSettings_MultipleValidCidrs_IsValid()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string>
            {
                "192.168.1.0/24",
                "10.0.0.0/25",
                "172.16.0.0/26"
            },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSettings_EmptyStringsIgnored()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string>
            {
                "192.168.1.0/24",
                "",
                "   "
            },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateSettings_OverlappingNetworks_ReportsWarning()
    {
        var settings = new NetworkDiscoverySettings
        {
            DiscoverySubnets = new List<string>
            {
                "192.168.1.0/24",   // 192.168.1.0 - 192.168.1.255
                "192.168.1.0/25"    // Overlaps: 192.168.1.0 - 192.168.1.127
            },
            ClientTimeoutMs = 5000,
            MaxConcurrentRequests = 50
        };

        NetworkValidationResult result = NetworkValidationService.ValidateSettings(settings);

        // Still valid but with warnings
        result.Warnings.Should().ContainSingle(w => w.Contains("overlap"));
    }

    [Fact]
    public void ValidateCidr_ValidFormats()
    {
        string[] validCidrs = new[]
        {
            "192.168.1.0/24",
            "10.0.0.0/8",
            "172.16.0.0/16",
            "8.8.8.8/32"
        };

        foreach (string? cidr in validCidrs)
        {
            CidrValidationResult result = NetworkValidationService.ValidateCidr(cidr);
            result.IsValid.Should().BeTrue($"CIDR {cidr} should be valid");
        }
    }

    [Fact]
    public void ValidateCidr_InvalidFormats()
    {
        string[] invalidCidrs = new[]
        {
            "192.168.1.0/33",      // Prefix too large
            "192.168.1.0/-1",      // Negative prefix
            "256.168.1.0/24",      // Invalid IP
            "192.168.1.0/",        // Missing prefix
            "192.168.1.0",         // Missing CIDR notation
            "not an ip/24"         // Not an IP
        };

        foreach (string? cidr in invalidCidrs)
        {
            CidrValidationResult result = NetworkValidationService.ValidateCidr(cidr);
            result.IsValid.Should().BeFalse($"CIDR {cidr} should be invalid");
        }
    }
}
