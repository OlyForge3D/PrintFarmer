using System.Text.Json;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests;

public class DiscoveryDtoContractTests
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DiscoveryProgressDto_should_serialize_with_new_fields()
    {
        var dto = new DiscoveryProgressDto(
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

        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        json.Should().Contain("\"networkRanges\"");
        json.Should().Contain("\"autoDetectedNetworks\":true");
    }

    [Fact]
    public void DiscoveryCompletedDto_should_serialize_with_new_fields()
    {
        var dto = new DiscoveryCompletedDto(
            SessionId: "sess-2",
            TotalPrintersFound: 5,
            TotalPrintersExcluded: 2,
            Duration: TimeSpan.FromSeconds(12),
            WasCancelled: false,
            NetworkRanges: new[] { "10.0.0.0/24", "192.168.0.0/24" },
            AutoDetectedNetworks: false
        );

        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        json.Should().Contain("\"networkRanges\"");
        json.Should().Contain("\"autoDetectedNetworks\":false");
    }
}
