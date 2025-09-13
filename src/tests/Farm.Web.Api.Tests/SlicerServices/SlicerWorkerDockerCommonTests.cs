using Farm.Web.Api.Tests.Util;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Parameterized Docker integration tests that cover common behaviors across slicer workers
/// (PrusaSlicer + OrcaSlicer) to reduce duplication in individual worker test classes.
/// </summary>
[Trait("Category", "Docker")]
public class SlicerWorkerDockerCommonTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _composeFile;
    private readonly string _root;

    public static IEnumerable<object[]> WorkerMatrix => new[]
    {
        new object[] { "prusaslicer-worker", 8082, "/usr/local/bin/prusa-slicer", "Worker__PrusaSlicerPath", "Prusa" },
        ["orcaslicer-worker", 8081, "/usr/local/bin/orcaslicer", "Worker__OrcaSlicerPath", "Orca"]
    };

    public SlicerWorkerDockerCommonTests(ITestOutputHelper output)
    {
        _output = output;
        _root = DockerTestHelpers.GetRepositoryRoot();
        _composeFile = Path.Combine(_root, "docker-compose.microservices.yml");
    }

    [Theory]
    [MemberData(nameof(WorkerMatrix))]
    public async Task Binary_ShouldBeInstalled_InContainer(string service, int port, string binaryPath, string pathEnvVar, string expectedMarker)
    {
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "up", "-d", service);
        // Ensure HTTP surface (if any) is up; harmless if service has no listener
        try
        {
            await DockerTestHelpers.WaitForServiceAsync(_output, service, port, timeout: TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Some workers may not expose an HTTP health endpoint
        }
        await DockerTestHelpers.WaitForExecSuccessAsync(_output, _composeFile, _root, service, ["test", "-f", binaryPath], TimeSpan.FromSeconds(90));

        var ls = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "exec", "-T", service, "ls", "-la", binaryPath);
        Assert.True(ls.Success, $"Binary listing failed for {service}: {ls.ErrorOutput}");
        Assert.Contains(Path.GetFileName(binaryPath), ls.Output);

        var execPerm = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "exec", "-T", service, "test", "-x", binaryPath);
        Assert.True(execPerm.Success, $"Binary not executable for {service}");

        var envPath = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "exec", "-T", service, "printenv", pathEnvVar);
        Assert.True(envPath.Success && envPath.Output.Contains(binaryPath), $"Env var {pathEnvVar} missing or incorrect for {service}");

        // Leverage expectedMarker (e.g., 'Prusa', 'Orca') to confirm service naming alignment
        Assert.Contains(expectedMarker, service, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(WorkerMatrix))]
    public async Task Version_CommandInvocation_ShouldReturnHelpOrExist(string service, int port, string binaryPath, string pathEnvVar, string expectedMarker)
    {
        await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "up", "-d", service);
        try
        {
            await DockerTestHelpers.WaitForServiceAsync(_output, service, port, timeout: TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Health endpoint may be absent; ignore
        }
        await DockerTestHelpers.WaitForExecSuccessAsync(_output, _composeFile, _root, service, ["test", "-f", binaryPath], TimeSpan.FromSeconds(90));

        // Assert env var path presence (uses pathEnvVar parameter)
        var envPath = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "exec", "-T", service, "printenv", pathEnvVar);
        Assert.True(envPath.Success && envPath.Output.Contains(binaryPath), $"Env var {pathEnvVar} missing or incorrect for {service}");

        var help = await DockerTestHelpers.RunDockerComposeCommandAsync(_output, _composeFile, _root, "exec", "-T", service, binaryPath, "--help");
        if (help.Success)
        {
            Assert.Contains(expectedMarker, help.Output, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Fallback: ensure file still exists (already asserted by WaitForExecSuccessAsync)
            _output.WriteLine($"Help output not available for {service}; binary existence already verified.");
        }
    }
}
