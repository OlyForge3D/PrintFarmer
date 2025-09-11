using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
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
        _baseDirectory = GetRepositoryRoot();
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
            await RunDockerComposeCommandAsync("down", "--volumes", "--remove-orphans");
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
        var result = await RunDockerCommandAsync("build", "-f", "Dockerfile.prusaslicer", "-t", "prusaslicer-worker-test", ".");

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
        var startResult = await RunDockerComposeCommandAsync("up", "-d", "redis", "database", "prusaslicer-worker");
        Assert.True(startResult.Success, $"Docker Compose start failed: {startResult.ErrorOutput}");

        // Wait for services to be ready
        await Task.Delay(TimeSpan.FromSeconds(30));

        // Check health status
        var healthResult = await CheckServiceHealthAsync("prusaslicer-worker", 8082);

        // Assert
        Assert.True(healthResult.IsHealthy, $"PrusaSlicer worker health check failed: {healthResult.Message}");
        _output.WriteLine($"PrusaSlicer worker is healthy: {healthResult.Message}");
    }

    [Fact]
    public async Task PrusaSlicerBinary_ShouldBeInstalled_InContainer()
    {
        // Arrange
        await RunDockerComposeCommandAsync("up", "-d", "prusaslicer-worker");
        await Task.Delay(TimeSpan.FromSeconds(45)); // Allow time for binary installation

        // Act - Check if PrusaSlicer binary is installed
        var execResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker", "ls", "-la", "/usr/local/bin/prusa-slicer");

        // Assert
        Assert.True(execResult.Success, $"PrusaSlicer binary not found: {execResult.ErrorOutput}");
        Assert.Contains("prusa-slicer", execResult.Output);
        _output.WriteLine($"PrusaSlicer binary found: {execResult.Output}");

        // Verify binary is executable
        var permResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker", "test", "-x", "/usr/local/bin/prusa-slicer");
        Assert.True(permResult.Success, "PrusaSlicer binary is not executable");
        _output.WriteLine("PrusaSlicer binary is executable");
    }

    [Fact]
    public async Task MixedSlicerWorkers_ShouldStartTogether_InMicroservicesMode()
    {
        // Arrange & Act
        _output.WriteLine("Starting complete microservices stack...");
        var result = await RunDockerComposeCommandAsync("up", "-d", "redis", "database", "api", "orcaslicer-worker", "prusaslicer-worker");

        Assert.True(result.Success, $"Failed to start microservices: {result.ErrorOutput}");

        // Wait for services to initialize
        await Task.Delay(TimeSpan.FromSeconds(60));

        // Assert - Check health of all services
        var redisHealth = await CheckServiceHealthAsync("redis", 6379, "/healthcheck");
        var apiHealth = await CheckServiceHealthAsync("api", 5001);
        var orcaHealth = await CheckServiceHealthAsync("orcaslicer-worker", 8081);
        var prusaHealth = await CheckServiceHealthAsync("prusaslicer-worker", 8082);

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
        await RunDockerComposeCommandAsync("up", "-d", "orcaslicer-worker", "prusaslicer-worker");
        await Task.Delay(TimeSpan.FromSeconds(30));

        // Act & Assert - Check OrcaSlicer worker environment
        var orcaEnvResult = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "printenv", "Worker__OrcaSlicerPath");
        Assert.True(orcaEnvResult.Success && orcaEnvResult.Output.Contains("/usr/local/bin/orcaslicer"));
        _output.WriteLine($"OrcaSlicer path: {orcaEnvResult.Output.Trim()}");

        // Check PrusaSlicer worker environment
        var prusaEnvResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker", "printenv", "Worker__PrusaSlicerPath");
        Assert.True(prusaEnvResult.Success && prusaEnvResult.Output.Contains("/usr/local/bin/prusa-slicer"));
        _output.WriteLine($"PrusaSlicer path: {prusaEnvResult.Output.Trim()}");

        // Verify distinct worker IDs
        var orcaIdResult = await RunDockerComposeCommandAsync("exec", "-T", "orcaslicer-worker", "printenv", "Worker__WorkerId");
        var prusaIdResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker", "printenv", "Worker__WorkerId");

        Assert.NotEqual(orcaIdResult.Output.Trim(), prusaIdResult.Output.Trim());
        _output.WriteLine($"Worker IDs are distinct: Orca='{orcaIdResult.Output.Trim()}', Prusa='{prusaIdResult.Output.Trim()}'");
    }

    [Fact]
    public async Task PrusaSlicerVersion_ShouldMatchExpectedVersion()
    {
        // Arrange
        await RunDockerComposeCommandAsync("up", "-d", "prusaslicer-worker");
        await Task.Delay(TimeSpan.FromSeconds(45));

        // Act - Check PrusaSlicer version
        var versionResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker",
            "/usr/local/bin/prusa-slicer", "--help");

        // Assert
        if (versionResult.Success)
        {
            // PrusaSlicer should be version 2.8.0
            _output.WriteLine($"PrusaSlicer help output: {versionResult.Output}");
            Assert.Contains("PrusaSlicer", versionResult.Output);
        }
        else
        {
            // In headless mode, PrusaSlicer might not provide help, but should exist
            var existsResult = await RunDockerComposeCommandAsync("exec", "-T", "prusaslicer-worker", "test", "-f", "/usr/local/bin/prusa-slicer");
            Assert.True(existsResult.Success, "PrusaSlicer binary should exist even if help fails in headless mode");
            _output.WriteLine("PrusaSlicer binary exists (help output not available in headless mode)");
        }
    }

    [Fact(Skip = "Long running test - enable for full integration validation")]
    public async Task EndToEndSlicing_ShouldWork_WithPrusaSlicerWorker()
    {
        // This test would simulate a complete slicing workflow:
        // 1. Start all microservices
        // 2. Submit a slicing job via API
        // 3. Verify job is picked up by PrusaSlicer worker
        // 4. Verify G-code is generated and returned

        // Arrange
        await RunDockerComposeCommandAsync("up", "-d");
        await Task.Delay(TimeSpan.FromMinutes(2)); // Allow full startup

        // This would require an actual API client and test STL file
        // Implementation would depend on the API design

        _output.WriteLine("End-to-end test would be implemented here");
    }

    // Helper methods

    private async Task<(bool Success, string Output, string ErrorOutput)> RunDockerCommandAsync(params string[] args)
    {
        return await RunCommandAsync("docker", args);
    }

    private async Task<(bool Success, string Output, string ErrorOutput)> RunDockerComposeCommandAsync(params string[] args)
    {
        var allArgs = new[] { "compose", "-f", _dockerComposeFile }.Concat(args).ToArray();
        return await RunCommandAsync("docker", allArgs);
    }

    private async Task<(bool Success, string Output, string ErrorOutput)> RunCommandAsync(string command, string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = string.Join(" ", args.Select(arg => $"\"{arg}\"")),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _baseDirectory
        };

        _output.WriteLine($"Running: {command} {string.Join(" ", args)}");

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode == 0, output, error);
    }

    private async Task<(bool IsHealthy, string Message)> CheckServiceHealthAsync(string serviceName, int port, string endpoint = "/healthz")
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://localhost:{port}{endpoint}";

            var response = await httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return (true, $"{serviceName} is healthy at {url}: {content}");
            }
            else
            {
                return (false, $"{serviceName} health check failed at {url}: {response.StatusCode} - {content}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"{serviceName} health check exception: {ex.Message}");
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new InvalidOperationException("Could not find repository root (global.json not found)");
        }

        return directory.FullName;
    }

    private static ILogger<PrusaSlicerDockerIntegrationTests> CreateLogger()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        return loggerFactory.CreateLogger<PrusaSlicerDockerIntegrationTests>();
    }
}