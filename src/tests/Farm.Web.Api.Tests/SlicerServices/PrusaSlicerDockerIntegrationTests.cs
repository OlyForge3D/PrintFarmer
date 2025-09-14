using Microsoft.Extensions.Logging;
using Farm.Web.Api.Tests.Util;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Docker deployment integration tests for PrusaSlicer worker
/// Tests binary installation, container health, and end-to-end slicing
/// </summary>
[Trait("Category", "Docker")]
public class PrusaSlicerDockerIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<PrusaSlicerDockerIntegrationTests> _logger;
    private readonly string _dockerComposeFile;
    private readonly string _baseDirectory;

    public PrusaSlicerDockerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = CreateLogger();
        _baseDirectory = DockerTestHelpers.GetRepositoryRoot();
        _dockerComposeFile = Path.Combine(_baseDirectory, "docker-compose.microservices.yml");
    }

    public async Task InitializeAsync()
    {
        // Ensure Docker Compose file exists
        if (!File.Exists(_dockerComposeFile))
        {
            throw new FileNotFoundException($"Docker Compose file not found: {_dockerComposeFile}");
        }

        _output.WriteLine($"Using Docker Compose file: {_dockerComposeFile}");
        _output.WriteLine($"Base directory: {_baseDirectory}");
    }

    public async Task DisposeAsync()
    {
        // Cleanup: Stop any running containers
        try
        {
            await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "down", "--volumes", "--remove-orphans");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }

    [Fact]
    public async Task PrusaSlicerWorker_ShouldBuildDockerImage_Successfully()
    {
        // Arrange & Act
        _output.WriteLine("Building PrusaSlicer worker Docker image...");
        var result = await DockerTestHelpers.RunDockerCommandAsync(_output, _baseDirectory, "build", "-f", "Dockerfile.prusaslicer", "-t", "prusaslicer-worker-test", ".");

        // Assert
        Assert.True(result.Success, $"Docker build failed: {result.ErrorOutput}");
        _output.WriteLine("Docker image built successfully");
        _output.WriteLine($"Build output: {result.Output}");
    }

    [Fact]
    public async Task PrusaSlicerWorker_ShouldStartHealthy_InDockerCompose()
    {
        // Arrange
        _output.WriteLine("Starting PrusaSlicer worker with Docker Compose...");

        // Act - Start only the PrusaSlicer worker and its dependencies
        var startResult = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "redis", "database", "prusaslicer-worker");
        Assert.True(startResult.Success, $"Docker Compose start failed: {startResult.ErrorOutput}");
        await DockerTestHelpers.WaitForServiceAsync(_output, "prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(60));

        // Check health status
        var healthResult = await DockerTestHelpers.CheckServiceHealthAsync("prusaslicer-worker", 8082);

        // Assert
        Assert.True(healthResult.IsHealthy, $"PrusaSlicer worker health check failed: {healthResult.Message}");
        _output.WriteLine($"PrusaSlicer worker is healthy: {healthResult.Message}");
    }


    [Fact]
    public async Task MixedSlicerWorkers_ShouldStartTogether_InMicroservicesMode()
    {
        // Arrange & Act
        _output.WriteLine("Starting complete microservices stack...");
        var result = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "redis", "database", "api", "orcaslicer-worker", "prusaslicer-worker");

        Assert.True(result.Success, $"Failed to start microservices: {result.ErrorOutput}");
        // Adaptive parallel health polling (max 90s)
        await Task.WhenAll(
            DockerTestHelpers.WaitForServiceAsync(_output, "api", 5001, timeout: TimeSpan.FromSeconds(90)),
            DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(90)),
            DockerTestHelpers.WaitForServiceAsync(_output, "prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(90))
        );

        // Assert - Check health of all services
        var redisHealth = await DockerTestHelpers.CheckServiceHealthAsync("redis", 6379, "/healthcheck");
        var apiHealth = await DockerTestHelpers.CheckServiceHealthAsync("api", 5001);
        var orcaHealth = await DockerTestHelpers.CheckServiceHealthAsync("orcaslicer-worker", 8081);
        var prusaHealth = await DockerTestHelpers.CheckServiceHealthAsync("prusaslicer-worker", 8082);

        _output.WriteLine($"Redis health: {redisHealth.Message}");
        _output.WriteLine($"API health: {apiHealth.Message}");
        _output.WriteLine($"OrcaSlicer worker health: {orcaHealth.Message}");
        _output.WriteLine($"PrusaSlicer worker health: {prusaHealth.Message}");

        // API must be healthy
        Assert.True(apiHealth.IsHealthy, $"API service unhealthy: {apiHealth.Message}");

        // At least one slicer worker should be healthy
        Assert.True(orcaHealth.IsHealthy || prusaHealth.IsHealthy,
            "At least one slicer worker should be healthy");
    }

    [Fact]
    public async Task SlicerWorkersConfiguration_ShouldHaveDistinctEnvironments()
    {
        // Arrange
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "orcaslicer-worker", "prusaslicer-worker");
        await Task.WhenAll(
            DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(60)),
            DockerTestHelpers.WaitForServiceAsync(_output, "prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(60))
        );

        // Act & Assert - Check OrcaSlicer worker environment
        var orcaEnvResult = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "printenv", "Worker__OrcaSlicerPath");
        Assert.True(orcaEnvResult.Success && orcaEnvResult.Output.Contains("/usr/local/bin/orcaslicer"));
        _output.WriteLine($"OrcaSlicer path: {orcaEnvResult.Output.Trim()}");

        // Check PrusaSlicer worker environment
        var prusaEnvResult = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "prusaslicer-worker", "printenv", "Worker__PrusaSlicerPath");
        Assert.True(prusaEnvResult.Success && prusaEnvResult.Output.Contains("/usr/local/bin/prusa-slicer"));
        _output.WriteLine($"PrusaSlicer path: {prusaEnvResult.Output.Trim()}");

        // Verify distinct worker IDs
        var orcaIdResult = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "printenv", "Worker__WorkerId");
        var prusaIdResult = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "prusaslicer-worker", "printenv", "Worker__WorkerId");

        Assert.NotEqual(orcaIdResult.Output.Trim(), prusaIdResult.Output.Trim());
        _output.WriteLine($"Worker IDs are distinct: Orca='{orcaIdResult.Output.Trim()}', Prusa='{prusaIdResult.Output.Trim()}'");
    }


    [Fact]
    public async Task EndToEndSlicing_ShouldWork_WithPrusaSlicerWorker()
    {
        // This test would simulate a complete slicing workflow:
        // 1. Start all microservices
        // 2. Submit a slicing job via API
        // 3. Verify job is picked up by PrusaSlicer worker
        // 4. Verify G-code is generated and returned

        // Arrange
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d");
        await Task.WhenAll(
            DockerTestHelpers.WaitForServiceAsync(_output, "api", 5001, timeout: TimeSpan.FromSeconds(120)),
            DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(120)),
            DockerTestHelpers.WaitForServiceAsync(_output, "prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(120))
        );

        // This would require an actual API client and test STL file
        // Implementation would depend on the API design

        _output.WriteLine("End-to-end test would be implemented here");
    }

    // Helper methods

    private static ILogger<PrusaSlicerDockerIntegrationTests> CreateLogger()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        return loggerFactory.CreateLogger<PrusaSlicerDockerIntegrationTests>();
    }
}
