using Farm.Web.IntegrationTests.Util;
using Xunit.Abstractions;

namespace Farm.Web.IntegrationTests.SlicerServices;

[Trait("Category", "Docker")]
public class SlicerWorkerDockerCommonTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _composeFile;
    private readonly string _root;

    public SlicerWorkerDockerCommonTests(ITestOutputHelper output)
    {
        _output = output;
        _root = DockerTestHelpers.GetRepositoryRoot();
        _composeFile = Path.Combine(_root, "docker-compose.microservices.yml");
    }

    // ... tests that call DockerTestHelpers as in original file
}
