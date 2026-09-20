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
