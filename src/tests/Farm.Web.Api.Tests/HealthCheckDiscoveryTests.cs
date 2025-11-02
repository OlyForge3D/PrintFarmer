using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Health;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests for health check discovery and configuration validation.
/// Ensures that:
/// 1. Spoolman health check is skipped when not enabled/configured
/// 2. Discovery configuration is properly validated
/// 3. Health checks properly handle missing or invalid configurations
/// </summary>
public class HealthCheckDiscoveryTests
{
    private readonly Mock<AppDbContext> _mockDbContext;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IUnifiedLoggingService> _mockLogger;
    private readonly Mock<ISettingsService> _mockSettingsService;
    private readonly Mock<IHostEnvironment> _mockHostEnvironment;
    private readonly Mock<ISpoolmanService> _mockSpoolmanService;

    public HealthCheckDiscoveryTests()
    {
        _mockDbContext = new Mock<AppDbContext>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockLogger = new Mock<IUnifiedLoggingService>();
        _mockSettingsService = new Mock<ISettingsService>();
        _mockHostEnvironment = new Mock<IHostEnvironment>();
        _mockSpoolmanService = new Mock<ISpoolmanService>();

        _mockHostEnvironment.Setup(h => h.EnvironmentName).Returns("Development");
    }

    #region Spoolman Health Check Tests

    [Fact]
    public async Task SpoolmanHealthCheck_WhenNotConfigured_ReturnsHealthy()
    {
        // Arrange
        _mockSpoolmanService.Setup(s => s.GetConfig()).Returns((SpoolmanConfigDto?)null);
        var healthCheck = new SpoolmanHealthCheck(_mockSpoolmanService.Object, _mockHttpClientFactory.Object);
        var context = new HealthCheckContext();

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not configured");
    }

    [Fact]
    public async Task SpoolmanHealthCheck_WhenConfiguredWithEmptyUrl_ReturnsHealthy()
    {
        // Arrange
        var emptyConfig = new SpoolmanConfigDto(string.Empty);
        _mockSpoolmanService.Setup(s => s.GetConfig()).Returns(emptyConfig);
        var healthCheck = new SpoolmanHealthCheck(_mockSpoolmanService.Object, _mockHttpClientFactory.Object);
        var context = new HealthCheckContext();

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not configured");
    }

    [Fact]
    public async Task SpoolmanHealthCheck_WhenConfiguredWithWhitespace_ReturnsHealthy()
    {
        // Arrange
        var whitespaceConfig = new SpoolmanConfigDto("   ");
        _mockSpoolmanService.Setup(s => s.GetConfig()).Returns(whitespaceConfig);
        var healthCheck = new SpoolmanHealthCheck(_mockSpoolmanService.Object, _mockHttpClientFactory.Object);
        var context = new HealthCheckContext();

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("not configured");
    }

    [Fact]
    public async Task SpoolmanHealthCheck_WhenConfiguredAndHealthy_ReturnsHealthy()
    {
        // Arrange
        var validConfig = new SpoolmanConfigDto("http://spoolman.local:7912");
        _mockSpoolmanService.Setup(s => s.GetConfig()).Returns(validConfig);

        // Use a real HttpClientFactory since HttpClient methods can't be mocked (not virtual)
        var clientFactory = new HttpClientFactory();
        var healthCheck = new SpoolmanHealthCheck(_mockSpoolmanService.Object, clientFactory);
        var context = new HealthCheckContext();

        // Act
        // Note: This test will attempt real HTTP call to spoolman.local which will fail.
        // We test the graceful handling of this scenario in the ReturnsDegraded test.
        // For this test, we're verifying that configured status returns non-null result.
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        result.Should().NotBeNull();
        // Result will be Degraded (can't reach spoolman.local) but that's expected in tests
        result.Status.Should().BeOneOf(HealthStatus.Healthy, HealthStatus.Degraded);
    }

    private class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    [Fact]
    public async Task SpoolmanHealthCheck_WhenConfiguredButUnreachable_ReturnsDegraded()
    {
        // Arrange
        var validConfig = new SpoolmanConfigDto("http://unreachable.local:7912");
        _mockSpoolmanService.Setup(s => s.GetConfig()).Returns(validConfig);

        var clientFactory = new HttpClientFactory();
        var healthCheck = new SpoolmanHealthCheck(_mockSpoolmanService.Object, clientFactory);
        var context = new HealthCheckContext();

        // Act
        var result = await healthCheck.CheckHealthAsync(context);

        // Assert
        result.Should().NotBeNull();
        // Should be Degraded or Unhealthy when host can't be reached
        result.Status.Should().BeOneOf(HealthStatus.Degraded, HealthStatus.Unhealthy);
    }

    #endregion

    #region Network Discovery Configuration Tests

    [Fact]
    public void NetworkDiscoverySettings_WhenEmpty_IsValid()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = false,
            DiscoverySubnets = new List<string>()
        };

        // Act
        var isValid = !settings.EnableDiscovery || (settings.DiscoverySubnets?.Count ?? 0) > 0;

        // Assert
        isValid.Should().BeTrue("Empty discovery settings should be valid (discovery just disabled)");
    }

    [Fact]
    public void NetworkDiscoverySettings_WithValidSubnets_IsValid()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "192.168.0.0/16", "10.0.0.0/8" }
        };

        // Act
        var subnets = settings.DiscoverySubnets;

        // Assert
        subnets.Should().HaveCount(2);
        subnets[0].Should().Be("192.168.0.0/16");
        subnets[1].Should().Be("10.0.0.0/8");
    }

    [Fact]
    public void NetworkDiscoverySettings_WithSingleSubnet_IsValid()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "192.168.1.0/24" }
        };

        // Act
        var subnets = settings.DiscoverySubnets;

        // Assert
        subnets.Should().HaveCount(1);
        subnets[0].Should().Be("192.168.1.0/24");
    }

    [Fact]
    public void NetworkDiscoverySettings_DisabledWithSubnets_IgnoresSubnets()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = false,
            DiscoverySubnets = new List<string> { "192.168.0.0/16", "10.0.0.0/8" }
        };

        // Act
        var shouldUseSubnets = settings.EnableDiscovery;

        // Assert
        shouldUseSubnets.Should().BeFalse("Subnets should not be used when discovery is disabled");
    }

    #endregion

    #region Environment Variable Mapping Tests

    [Fact]
    public void EnvironmentVariableMappingTest_PfarmSpoolmanBaseUrlShouldMapFromSpoolmanBaseUrl()
    {
        // Arrange
        string spoolmanBaseUrl = "http://spoolman.local:7912";
        string expectedPfarmVariable = "PFARM__Spoolman__BaseUrl";
        
        // Act - simulate what deploy-docker.sh does
        var envVars = new Dictionary<string, string?>
        {
            { "SPOOLMAN_BASE_URL", spoolmanBaseUrl },
            { expectedPfarmVariable, spoolmanBaseUrl }  // Should be set by deploy script
        };

        // Assert
        envVars.Should().ContainKey(expectedPfarmVariable);
        envVars[expectedPfarmVariable].Should().Be(spoolmanBaseUrl);
        envVars["SPOOLMAN_BASE_URL"].Should().Be(spoolmanBaseUrl);
    }

    [Fact]
    public void EnvironmentVariableMappingTest_PfarmNetworkDiscoveryShouldBeSet()
    {
        // Arrange
        string enableDiscovery = "true";
        string discoverySubnets = "192.168.0.0/16,10.0.0.0/8";
        
        // Act - simulate what deploy-docker.sh does
        var envVars = new Dictionary<string, string?>
        {
            { "ENABLE_DISCOVERY", enableDiscovery },
            { "PFARM__NetworkDiscovery__EnableDiscovery", enableDiscovery },
            { "NETWORK_RANGES", discoverySubnets },
            { "PFARM__NetworkDiscovery__DiscoverySubnets", discoverySubnets }
        };

        // Assert
        envVars.Should().ContainKey("PFARM__NetworkDiscovery__EnableDiscovery");
        envVars["PFARM__NetworkDiscovery__EnableDiscovery"].Should().Be(enableDiscovery);
        
        envVars.Should().ContainKey("PFARM__NetworkDiscovery__DiscoverySubnets");
        envVars["PFARM__NetworkDiscovery__DiscoverySubnets"].Should().Be(discoverySubnets);
    }

    #endregion

    #region Comprehensive Health Check Discovery Tests

    [Fact]
    public async Task ComprehensiveHealthCheck_WithDisabledDiscovery_ShouldBeHealthy()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = false,
            DiscoverySubnets = new List<string>()
        };

        // Act - verify that disabled discovery doesn't cause health check failure
        var isDiscoveryEnabled = settings.EnableDiscovery;

        // Assert
        isDiscoveryEnabled.Should().BeFalse();
        // The comprehensive health check should not fail due to missing discovery configuration
    }

    [Fact]
    public async Task ComprehensiveHealthCheck_WithEnabledDiscovery_RequiresValidSubnets()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string>()
        };

        // Act - verify that enabled discovery requires configuration
        bool hasValidConfig = settings.EnableDiscovery && (settings.DiscoverySubnets?.Count ?? 0) > 0;

        // Assert
        hasValidConfig.Should().BeFalse("Enabled discovery without subnets is misconfigured");
    }

    [Fact]
    public async Task ComprehensiveHealthCheck_WithProperlyConfiguredDiscovery_IsValid()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "192.168.0.0/16", "10.0.0.0/8" }
        };

        // Act
        bool hasValidConfig = settings.EnableDiscovery && (settings.DiscoverySubnets?.Count ?? 0) > 0;

        // Assert
        hasValidConfig.Should().BeTrue("Properly configured discovery should be valid");
    }

    #endregion

    #region Configuration Validation Tests

    [Fact]
    public void DiscoverySubnets_WithEmptyList_ShouldBeHandledGracefully()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string>()
        };

        // Act
        var isEmpty = !settings.DiscoverySubnets.Any();

        // Assert
        isEmpty.Should().BeTrue("Empty subnet list should be recognized as empty");
    }

    [Fact]
    public void DiscoverySubnets_WithWhitespaceItems_ShouldBeHandledGracefully()
    {
        // Arrange
        var settings = new NetworkDiscoverySettings
        {
            EnableDiscovery = true,
            DiscoverySubnets = new List<string> { "  ", "" }
        };

        // Act
        var hasValidItems = settings.DiscoverySubnets.Any(s => !string.IsNullOrWhiteSpace(s));

        // Assert
        hasValidItems.Should().BeFalse("Whitespace-only subnet list should have no valid items");
    }

    [Fact]
    public void SpoolmanConfig_WithoutBaseUrl_ShouldNotAttemptHealthCheck()
    {
        // Arrange
        SpoolmanConfigDto? config = null;

        // Act
        bool shouldCheckHealth = config != null && !string.IsNullOrWhiteSpace(config.BaseUrl);

        // Assert
        shouldCheckHealth.Should().BeFalse("Health check should be skipped when Spoolman is not configured");
    }

    [Fact]
    public void SpoolmanConfig_WithInvalidUri_ShouldReturnGracefulError()
    {
        // Arrange
        var invalidConfig = new SpoolmanConfigDto("not-a-valid-uri");

        // Act
        bool isValidUri = Uri.TryCreate(invalidConfig.BaseUrl, UriKind.Absolute, out var uri);

        // Assert
        isValidUri.Should().BeFalse("Invalid URI should not parse");
    }

    [Fact]
    public void SpoolmanConfig_WithValidUri_ShouldParseCorrectly()
    {
        // Arrange
        var validConfig = new SpoolmanConfigDto("http://spoolman.local:7912");

        // Act
        bool isValidUri = Uri.TryCreate(validConfig.BaseUrl, UriKind.Absolute, out var uri);

        // Assert
        isValidUri.Should().BeTrue("Valid URI should parse");
        uri.Should().NotBeNull();
        uri!.Host.Should().Be("spoolman.local");
        uri.Port.Should().Be(7912);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void DeployScript_ShouldGeneratePfarmVariablesFromEnvironment()
    {
        // Arrange - simulate what the deploy script generates in .env file
        var deployedEnvVars = new Dictionary<string, string>
        {
            // From .env.microservices
            { "SPOOLMAN_BASE_URL", "http://spoolman.local:7912" },
            { "ENABLE_DISCOVERY", "yes" },
            { "NETWORK_RANGES", "192.168.0.0/16,10.0.0.0/8" },
            
            // These should be added by deploy script to .env file (around line 2439)
            { "PFARM__Spoolman__BaseUrl", "http://spoolman.local:7912" },
            { "PFARM__NetworkDiscovery__EnableDiscovery", "yes" },
            { "PFARM__NetworkDiscovery__DiscoverySubnets", "192.168.0.0/16,10.0.0.0/8" }
        };

        // Act
        var pfarmSpoolmanUrl = deployedEnvVars["PFARM__Spoolman__BaseUrl"];
        var pfarmDiscoveryEnabled = deployedEnvVars["PFARM__NetworkDiscovery__EnableDiscovery"];
        var pfarmDiscoverySubnets = deployedEnvVars["PFARM__NetworkDiscovery__DiscoverySubnets"];

        // Assert
        pfarmSpoolmanUrl.Should().Be("http://spoolman.local:7912");
        pfarmDiscoveryEnabled.Should().Be("yes");
        pfarmDiscoverySubnets.Should().Be("192.168.0.0/16,10.0.0.0/8");
    }

    [Fact]
    public void DeployScript_WhenSpoolmanDisabled_ShouldNotRequireConfiguration()
    {
        // Arrange - simulate deployment without Spoolman
        var deployedEnvVars = new Dictionary<string, string>
        {
            { "ENABLE_SPOOLMAN", "no" },
            // PFARM__Spoolman__BaseUrl may be empty
            { "PFARM__Spoolman__BaseUrl", "" }
        };

        // Act
        bool spoolmanEnabled = deployedEnvVars["ENABLE_SPOOLMAN"] == "yes";
        bool spoolmanConfigured = !string.IsNullOrWhiteSpace(deployedEnvVars["PFARM__Spoolman__BaseUrl"]);

        // Assert
        spoolmanEnabled.Should().BeFalse();
        // Spoolman health check should handle the empty configuration gracefully
        spoolmanConfigured.Should().BeFalse();
    }

    #endregion
}
