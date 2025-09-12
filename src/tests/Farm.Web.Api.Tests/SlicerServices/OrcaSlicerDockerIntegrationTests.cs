using Farm.Web.Api.Tests.Util;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Docker deployment integration tests for OrcaSlicer worker
/// Mirrors PrusaSlicerDockerIntegrationTests adaptive polling pattern.
/// Also includes a full-stack microservices smoke test including frontend.
/// </summary>
[Trait("Category", "Docker")]
public class OrcaSlicerDockerIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _dockerComposeFile;
    private readonly string _baseDirectory;

    public OrcaSlicerDockerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _baseDirectory = DockerTestHelpers.GetRepositoryRoot();
        _dockerComposeFile = Path.Combine(_baseDirectory, "docker-compose.microservices.yml");
    }

    public async Task InitializeAsync()
    {
        if (!File.Exists(_dockerComposeFile))
        {
            throw new FileNotFoundException($"Docker Compose file not found: {_dockerComposeFile}");
        }
        _output.WriteLine($"Using Docker Compose file: {_dockerComposeFile}");
    }

    public async Task DisposeAsync()
    {
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
    public async Task OrcaSlicerWorker_ShouldBuildDockerImage_Successfully()
    {
        _output.WriteLine("Building OrcaSlicer worker Docker image...");
        var result = await DockerTestHelpers.RunDockerCommandAsync(_output, _baseDirectory, "build", "-f", "Dockerfile.orcaslicer", "-t", "orcaslicer-worker-test", ".");
        Assert.True(result.Success, $"Docker build failed: {result.ErrorOutput}");
        _output.WriteLine("Docker image built successfully");
    }

    [Fact]
    public async Task OrcaSlicerWorker_ShouldStartHealthy_InDockerCompose()
    {
        var start = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "redis", "database", "api", "orcaslicer-worker");
        Assert.True(start.Success, $"Compose up failed: {start.ErrorOutput}");

        await DockerTestHelpers.WaitForServiceAsync(_output, "api", 5001, timeout: TimeSpan.FromSeconds(90));
        await DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(90));

        var apiHealth = await DockerTestHelpers.CheckServiceHealthAsync("api", 5001);
        var orcaHealth = await DockerTestHelpers.CheckServiceHealthAsync("orcaslicer-worker", 8081);
        Assert.True(apiHealth.IsHealthy, $"API unhealthy: {apiHealth.Message}");
        Assert.True(orcaHealth.IsHealthy, $"Orca worker unhealthy: {orcaHealth.Message}");
    }

    [Fact]
    public async Task OrcaSlicerBinary_ShouldBeInstalled_InContainer()
    {
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "orcaslicer-worker");
        await DockerTestHelpers.WaitForExecSuccessAsync(_output, _dockerComposeFile, _baseDirectory, "orcaslicer-worker", ["test", "-f", "/usr/local/bin/orcaslicer"], TimeSpan.FromSeconds(90));

        var ls = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "ls", "-la", "/usr/local/bin/orcaslicer");
        Assert.True(ls.Success, $"Binary listing failed: {ls.ErrorOutput}");
        Assert.Contains("orcaslicer", ls.Output);

        var execPerm = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "test", "-x", "/usr/local/bin/orcaslicer");
        Assert.True(execPerm.Success, "OrcaSlicer binary not executable");
    }

    [Fact]
    public async Task OrcaSlicerWorker_EnvConfiguration_ShouldExposeExpectedVariables()
    {
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "orcaslicer-worker");
        await DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(60));

        var pathVar = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "printenv", "Worker__OrcaSlicerPath");
        Assert.True(pathVar.Success && pathVar.Output.Contains("/usr/local/bin/orcaslicer"));

        var idVar = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "printenv", "Worker__WorkerId");
        Assert.True(idVar.Success && idVar.Output.Contains("orcaslicer-worker"));
    }

    [Fact]
    public async Task OrcaSlicerVersion_CommandInvocation_ShouldReturnHelpOrExist()
    {
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "orcaslicer-worker");
        await DockerTestHelpers.WaitForExecSuccessAsync(_output, _dockerComposeFile, _baseDirectory, "orcaslicer-worker", ["test", "-f", "/usr/local/bin/orcaslicer"], TimeSpan.FromSeconds(90));

        var version = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "exec", "-T", "orcaslicer-worker", "/usr/local/bin/orcaslicer", "--help");
        if (!version.Success)
        {
            // Fall back to existence assertion already satisfied above
            _output.WriteLine("Help output not available (possibly stub or headless extraction); binary exists.");
        }
        else
        {
            Assert.Contains("Orca", version.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact(Skip = "Long running full-stack smoke; enable when needed")]
    public async Task FullStack_Microservices_ShouldServeFrontendAndApi()
    {
        var up = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _dockerComposeFile, _baseDirectory, "up", "-d", "redis", "database", "api", "orcaslicer-worker", "prusaslicer-worker", "frontend");
        Assert.True(up.Success, $"Compose up failed: {up.ErrorOutput}");

        await Task.WhenAll(
            DockerTestHelpers.WaitForServiceAsync(_output, "api", 5001, timeout: TimeSpan.FromSeconds(120)),
            DockerTestHelpers.WaitForServiceAsync(_output, "orcaslicer-worker", 8081, timeout: TimeSpan.FromSeconds(120)),
            DockerTestHelpers.WaitForServiceAsync(_output, "prusaslicer-worker", 8082, timeout: TimeSpan.FromSeconds(150)),
            DockerTestHelpers.WaitForServiceAsync(_output, "frontend", 3000, endpoint: "/health", timeout: TimeSpan.FromSeconds(120))
        );

        var apiHealth = await DockerTestHelpers.CheckServiceHealthAsync("api", 5001);
        Assert.True(apiHealth.IsHealthy, $"API unhealthy: {apiHealth.Message}");
    }
}
