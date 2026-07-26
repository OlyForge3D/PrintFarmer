using System.Text.Json;
using Farm.Infrastructure;

namespace Farm.Web.Api.Tests;

public class DiscoveryDtoContractTests
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DiscoveryProgressDto_DoesNotSerializeNetworkTargets()
    {
        DiscoveryProgressDto dto = new DiscoveryProgressDto(
            SessionId: "sess-1",
            CurrentNetwork: "192.168.1.0/24",
            CurrentIp: "192.168.1.10",
            TotalIps: 254,
            ScannedIps: 10,
            PrintersFound: 2,
            PrintersExcluded: 1,
            ProgressPercentage: 3.93,
            Status: DiscoveryStatus.Scanning,
            Message: null,
            NetworkRanges: new[] { "192.168.1.0/24" },
            AutoDetectedNetworks: true
        );

        string json = JsonSerializer.Serialize(dto, _jsonOptions);
        _ = json.Should().NotContain("192.168.1");
        _ = json.Should().NotContain("\"networkRanges\"");
        _ = json.Should().NotContain("\"currentNetwork\"");
        _ = json.Should().NotContain("\"currentIp\"");
        _ = json.Should().Contain("\"autoDetectedNetworks\":true");
    }

    [Fact]
    public void DiscoveryCompletedDto_DoesNotSerializeNetworkRanges()
    {
        DiscoveryCompletedDto dto = new DiscoveryCompletedDto(
            SessionId: "sess-2",
            TotalPrintersFound: 5,
            TotalPrintersExcluded: 2,
            Duration: TimeSpan.FromSeconds(12),
            WasCancelled: false,
            NetworkRanges: new[] { "10.0.0.0/24", "192.168.0.0/24" },
            AutoDetectedNetworks: false
        );

        string json = JsonSerializer.Serialize(dto, _jsonOptions);
        _ = json.Should().NotContain("10.0.0.0");
        _ = json.Should().NotContain("192.168.0.0");
        _ = json.Should().NotContain("\"networkRanges\"");
        _ = json.Should().Contain("\"autoDetectedNetworks\":false");
    }

    [Fact]
    public void DiscoveryPrinterFoundDto_ContainsOnlyOpaqueIdentityAndSafeMetadata()
    {
        DiscoveryPrinterFoundDto dto = new(
            "sess-3",
            new DiscoveredPrinterSummaryDto(
                Guid.NewGuid(),
                "Test Printer",
                Farm.Infrastructure.Domain.PrinterBackend.Moonraker,
                "Test Manufacturer",
                "Test Model",
                DateTime.UtcNow,
                true));

        string json = JsonSerializer.Serialize(dto, _jsonOptions);

        _ = json.Should().Contain("\"discoveryId\"");
        _ = json.Should().NotContain("serverUrl");
        _ = json.Should().NotContain("ipAddress");
        _ = json.Should().NotContain("camera");
    }
}
