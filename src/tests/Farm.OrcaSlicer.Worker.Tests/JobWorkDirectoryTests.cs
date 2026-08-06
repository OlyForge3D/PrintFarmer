using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class JobWorkDirectoryTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"printfarmer-job-work-{Guid.NewGuid():N}");

    [Fact(DisplayName = "Overlapping claims use isolated work directories")]
    public void PrepareJobWorkDirectory_OverlappingClaims_DoNotDeleteOtherAttempt()
    {
        Guid jobId = Guid.NewGuid();
        Guid firstClaimToken = Guid.NewGuid();
        Guid secondClaimToken = Guid.NewGuid();
        string firstAttempt = OrcaSlicingPipelineService.PrepareJobWorkDirectory(
            _workingDirectory,
            jobId,
            firstClaimToken);
        File.WriteAllText(Path.Combine(firstAttempt, ".printfarmer-recovery.json"), "{}");
        File.WriteAllText(Path.Combine(firstAttempt, "stale.gcode"), "stale");

        string secondAttempt = OrcaSlicingPipelineService.PrepareJobWorkDirectory(
            _workingDirectory,
            jobId,
            secondClaimToken);

        _ = firstAttempt.Should().NotBe(secondAttempt);
        _ = File.Exists(Path.Combine(firstAttempt, "stale.gcode")).Should().BeTrue();
        _ = Directory.Exists(secondAttempt).Should().BeTrue();
        _ = Directory.EnumerateFileSystemEntries(secondAttempt).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
