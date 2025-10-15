using Farm.Web.IntegrationTests.Util;
using Xunit.Abstractions;

namespace Farm.Web.IntegrationTests.SlicerServices;

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

    // ... other tests kept identical (omitted for brevity in patch)
}
