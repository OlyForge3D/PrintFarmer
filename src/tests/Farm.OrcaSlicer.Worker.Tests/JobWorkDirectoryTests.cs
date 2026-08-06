using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class JobWorkDirectoryTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"printfarmer-job-work-{Guid.NewGuid():N}");

    [Fact(DisplayName = "A new claim removes stale output from an earlier attempt")]
    public void PrepareJobWorkDirectory_ExistingAttempt_RemovesStaleOutput()
    {
        Guid jobId = Guid.NewGuid();
        string jobDirectory = Path.Combine(_workingDirectory, jobId.ToString());
        _ = Directory.CreateDirectory(Path.Combine(jobDirectory, "output"));
        File.WriteAllText(Path.Combine(jobDirectory, ".printfarmer-recovery.json"), "{}");
        File.WriteAllText(Path.Combine(jobDirectory, "output", "stale.gcode"), "stale");

        string prepared = OrcaSlicingPipelineService.PrepareJobWorkDirectory(
            _workingDirectory,
            jobId);

        _ = prepared.Should().Be(jobDirectory);
        _ = Directory.Exists(prepared).Should().BeTrue();
        _ = Directory.EnumerateFileSystemEntries(prepared).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
