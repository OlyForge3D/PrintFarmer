using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class OrcaBinaryPathTests
{
    [Fact]
    public void DefaultBinaryPath_DetectorAndPipeline_UseSameExecutable()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerApi:BaseUrl"] = "http://slicer-host:5246",
            })
            .Build();
        OrcaBinaryDetector detector = new(configuration);
        OrcaSlicingPipelineService pipeline = new(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            new WorkerStateService());

        detector.BinaryPath.Should().Be(OrcaBinaryDetector.DefaultBinaryPath);
        pipeline.OrcaSlicerBinaryPath.Should().Be(OrcaBinaryDetector.DefaultBinaryPath);
    }

    [Fact]
    public void TrustedLauncherPath_ReferencesRealBinary_IsRecognized()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"printfarmer-orca-launcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string launcherPath = Path.Combine(directory, "orcaslicer");
        string binaryPath = Path.Combine(directory, "orca-slicer");
        File.WriteAllText(binaryPath, new string('x', 2048));
        File.WriteAllText(launcherPath, "#!/bin/sh\nAPPDIR=\"$(dirname \"$0\")\"\nexec \"$APPDIR/bin/orca-slicer\" \"$@\"\n");
        string relativeBinaryPath = Path.Combine(directory, "bin", "orca-slicer");
        Directory.CreateDirectory(Path.GetDirectoryName(relativeBinaryPath)!);
        File.Move(binaryPath, relativeBinaryPath);

        try
        {
            OrcaBinaryDetector.IsTrustedLauncher(launcherPath, launcherPath, relativeBinaryPath).Should().BeTrue();
            File.WriteAllText(launcherPath, "#!/bin/sh\nAPPDIR=\"$(dirname \"$0\")\"\nexec \"$APPDIR/bin/other-slicer\" \"$@\"\n");
            OrcaBinaryDetector.IsTrustedLauncher(launcherPath, launcherPath, relativeBinaryPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
