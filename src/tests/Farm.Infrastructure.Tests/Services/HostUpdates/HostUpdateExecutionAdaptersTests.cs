using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutionAdaptersTests
{
    [Theory]
    [InlineData("docker")]
    [InlineData("sqlite3")]
    [InlineData("pg_dump")]
    [InlineData("pg_restore")]
    [InlineData("sqlcmd")]
    [InlineData("docker.exe")]
    public async Task RunAsync_ApprovedExecutable_DelegatesWithoutChangingArguments(string executable)
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);
        IReadOnlyList<string> arguments = ["--version"];

        HostUpdateProcessResult result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(executable, inner.FileName);
        Assert.Same(arguments, inner.Arguments);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("powershell")]
    [InlineData("DOCKER")]
    [InlineData("docker.bat")]
    public async Task RunAsync_UnapprovedOrPathQualifiedExecutable_FailsBeforeDelegation(string executable)
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal($"host_update_executable_not_allowed:{executable}", exception.Message);
        Assert.Null(inner.FileName);
    }

    [Fact]
    public async Task RunAsync_RootedExecutableOutsideTrustedDirectory_FailsBeforeDelegation()
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);
        string executable = Path.Combine(Path.GetTempPath(), "docker");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal($"host_update_executable_path_not_trusted:{executable}", exception.Message);
        Assert.Null(inner.FileName);
    }

    private sealed class RecordingProcessRunner : IHostUpdateProcessRunner
    {
        public string? FileName { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(new HostUpdateProcessResult(0, string.Empty, string.Empty));
        }
    }
}
