using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class FileHostUpdateAdmissionGateTests
{
    [Fact]
    public async Task CloseAsync_ThenSecondInstanceObservesClosedGate()
    {
        string root = Path.Combine(Path.GetTempPath(), "pf-host-update-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new HostUpdateExecutionOptions { RootDirectory = root };
            var writer = new FileHostUpdateAdmissionGate(options);
            var reader = new FileHostUpdateAdmissionGate(options);

            await writer.CloseAsync(CancellationToken.None);

            (await reader.IsClosedAsync(CancellationToken.None)).Should().BeTrue();

            await reader.OpenAsync(CancellationToken.None);

            (await writer.IsClosedAsync(CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IsClosedAsync_WhenRootUnconfigured_ReturnsOpenForDefaultOffProduction()
    {
        var gate = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions());

        bool closed = await gate.IsClosedAsync(CancellationToken.None);

        closed.Should().BeFalse();
    }

    [Fact]
    public async Task IsClosedAsync_ObservingReplica_TracksHostExecutorMarker()
    {
        string root = Path.Combine(Path.GetTempPath(), "pf-host-update-gate-observe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executor = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions { RootDirectory = root });
            string stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDirectory);
            var replica = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions { AdmissionStateDirectory = stateDirectory });

            (await replica.IsClosedAsync(CancellationToken.None)).Should().BeFalse();

            await executor.CloseAsync(CancellationToken.None);
            (await replica.IsClosedAsync(CancellationToken.None)).Should().BeTrue();

            await executor.OpenAsync(CancellationToken.None);
            (await replica.IsClosedAsync(CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ObservingReplica_CloseAndOpenNeverWriteTheObservedDirectory()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "pf-host-update-gate-ro-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stateDirectory);
            string marker = Path.Combine(stateDirectory, FileHostUpdateAdmissionGate.MarkerFileName);
            await File.WriteAllTextAsync(marker, "held by host executor");
            var replica = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions { AdmissionStateDirectory = stateDirectory });

            await replica.OpenAsync(CancellationToken.None);
            File.Exists(marker).Should().BeTrue();

            File.Delete(marker);
            await replica.CloseAsync(CancellationToken.None);
            File.Exists(marker).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsClosedAsync_ObservedDirectoryMissingOrRelative_FailsClosed(bool relative)
    {
        string directory = relative
            ? "host-update-state"
            : Path.Combine(Path.GetTempPath(), "pf-host-update-gate-missing-" + Guid.NewGuid().ToString("N"));
        var replica = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions { AdmissionStateDirectory = directory });

        (await replica.IsClosedAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsClosedAsync_ObservedDirectoryUnreadable_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (Environment.UserName == "root")
        {
            return;
        }

        string stateDirectory = Path.Combine(Path.GetTempPath(), "pf-host-update-gate-unreadable-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stateDirectory);
            File.SetUnixFileMode(stateDirectory, UnixFileMode.None);
            var replica = new FileHostUpdateAdmissionGate(new HostUpdateExecutionOptions { AdmissionStateDirectory = stateDirectory });

            (await replica.IsClosedAsync(CancellationToken.None)).Should().BeTrue();
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(stateDirectory))
            {
                File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    // Pins the existing fail-closed contract: any durable marker, including malformed content,
    // means admission remains closed.
    public async Task IsClosedAsync_WhenMarkerIsCorrupt_FailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "pf-host-update-gate-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new HostUpdateExecutionOptions { RootDirectory = root };
            Directory.CreateDirectory(options.StateDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.StateDirectory, "admission.closed"), "{truncated");

            bool closed = await new FileHostUpdateAdmissionGate(options).IsClosedAsync(CancellationToken.None);

            closed.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
