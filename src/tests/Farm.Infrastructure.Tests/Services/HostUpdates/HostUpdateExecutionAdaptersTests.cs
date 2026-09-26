using Farm.Infrastructure.Services.HostUpdates;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateExecutionAdaptersTests
{
    [Theory]
    [InlineData("docker")]
    [InlineData("docker.")]
    [InlineData("docker.exe")]
    public async Task RunAsync_BareOrMalformedExecutable_FailsBeforeDelegation(string executable)
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(
            executable.EndsWith('.')
                ? $"host_update_executable_not_allowed:{executable}"
                : $"host_update_executable_path_not_configured:{executable}",
            exception.Message);
        Assert.Null(inner.FileName);
    }

    [Theory]
    [InlineData("sh")]
    [InlineData("powershell")]
    [InlineData("DOCKER")]
    [InlineData("docker.bat")]
    public async Task RunAsync_UnapprovedExecutable_FailsBeforeDelegation(string executable)
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal($"host_update_executable_not_allowed:{executable}", exception.Message);
        Assert.Null(inner.FileName);
    }

    [Fact]
    public async Task RunAsync_ExplicitConfiguredExecutable_DelegatesWithoutChangingArguments()
    {
        var inner = new RecordingProcessRunner();
        string executable = Path.Combine(Path.GetTempPath(), "docker.exe");
        var runner = new ConstrainedHostUpdateProcessRunner(inner, new HashSet<string>(StringComparer.Ordinal) { executable });
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

    [Fact]
    public async Task RunAsync_PathDirectoryFromPath_IsNotTrusted()
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);
        string directory = Directory.CreateTempSubdirectory("hu-path-").FullName;
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        string executable = Path.Combine(directory, "docker.exe");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

            Assert.Equal($"host_update_executable_path_not_trusted:{executable}", exception.Message);
            Assert.Null(inner.FileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_TraversalPath_IsNotTrusted()
    {
        var inner = new RecordingProcessRunner();
        var runner = new ConstrainedHostUpdateProcessRunner(inner);
        string executable = Path.Combine(AppContext.BaseDirectory, "..", "docker.exe");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(executable, [], TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal($"host_update_executable_path_not_trusted:{executable}", exception.Message);
        Assert.Null(inner.FileName);
    }

    [Theory]
    [InlineData("missing", null)]
    [InlineData("empty", "")]
    [InlineData("whitespace", "   ")]
    [InlineData("relative", "docker.exe")]
    public void ConfiguredResolver_InvalidMapping_FailsClosed(string key, string? path)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is not null)
        {
            mappings[key] = path;
        }

        var resolver = new ConfiguredHostUpdateExecutableResolver(mappings);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(key));

        Assert.Equal(
            key == "relative"
                ? $"host_update_executable_path_not_configured:{key}"
                : $"host_update_executable_not_configured:{key}",
            exception.Message);
    }

    [Fact]
    public void ConfiguredResolver_RootedMapping_ReturnsCanonicalPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "tools", "..", "docker.exe");
        var resolver = new ConfiguredHostUpdateExecutableResolver(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["docker"] = path });

        Assert.Equal(Path.GetFullPath(path), resolver.Resolve("docker"));
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
