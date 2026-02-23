using Farm.Infrastructure.Services.Monitoring;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Monitoring;

public class MonitoringSessionServiceTests
{
    private readonly IMonitoringSessionService _service;

    public MonitoringSessionServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "ThisIsATestKeyThatIsAtLeast32Characters!",
                ["Jwt:Issuer"] = "PrintFarmer-Test",
            })
            .Build();

        var logger = Mock.Of<ILogger<MonitoringSessionService>>();
        _service = new MonitoringSessionService(config, logger);
    }

    [Fact]
    public void CreateMonitoringToken_ValidUsername_ReturnsNonEmptyToken()
    {
        var token = _service.CreateMonitoringToken("admin");

        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateMonitoringTokenAsync_ValidToken_ReturnsValid()
    {
        var token = _service.CreateMonitoringToken("testuser");

        var result = await _service.ValidateMonitoringTokenAsync(token);

        result.IsValid.Should().BeTrue();
        result.Username.Should().Be("testuser");
    }

    [Fact]
    public async Task ValidateMonitoringTokenAsync_TamperedToken_ReturnsInvalid()
    {
        var token = _service.CreateMonitoringToken("testuser");
        var tampered = token + "tampered";

        var result = await _service.ValidateMonitoringTokenAsync(tampered);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateMonitoringTokenAsync_EmptyToken_ReturnsInvalid()
    {
        var result = await _service.ValidateMonitoringTokenAsync("");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateMonitoringTokenAsync_RandomString_ReturnsInvalid()
    {
        var result = await _service.ValidateMonitoringTokenAsync("not-a-jwt-token");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateMonitoringTokenAsync_DifferentSigningKey_ReturnsInvalid()
    {
        var token = _service.CreateMonitoringToken("admin");

        // Create a second service with a different key
        var otherConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "ADifferentKeyThatIsAlsoAtLeast32Characters!",
                ["Jwt:Issuer"] = "PrintFarmer-Test",
            })
            .Build();
        var otherService = new MonitoringSessionService(otherConfig, Mock.Of<ILogger<MonitoringSessionService>>());

        var result = await otherService.ValidateMonitoringTokenAsync(token);

        result.IsValid.Should().BeFalse();
    }
}
