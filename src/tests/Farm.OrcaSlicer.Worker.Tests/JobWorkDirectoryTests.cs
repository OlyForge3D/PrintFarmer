using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core;
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

    [Fact(DisplayName = "Expired recovery cleanup preserves active and recent attempts")]
    public void CleanupRecoveryDirectories_QuotaExceeded_PreservesActiveAndRecentAttempts()
    {
        Guid activeJobId = Guid.NewGuid();
        Guid oldJobId = Guid.NewGuid();
        Guid recentJobId = Guid.NewGuid();
        string activeAttempt = CreateRecoveryAttempt(activeJobId, "active", DateTime.UtcNow.AddDays(-10), 32);
        string oldAttempt = CreateRecoveryAttempt(oldJobId, "old", DateTime.UtcNow.AddDays(-10), 32);
        string recentAttempt = CreateRecoveryAttempt(recentJobId, "recent", DateTime.UtcNow.AddMinutes(-5), 32);

        System.Reflection.MethodInfo cleanupMethod = typeof(HttpJobPollerService).GetMethod(
            "CleanupRecoveryDirectories",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CleanupRecoveryDirectories is missing.");
        IReadOnlyList<string> deleted = (IReadOnlyList<string>)cleanupMethod.Invoke(
            null,
            [_workingDirectory, DateTime.UtcNow, TimeSpan.FromHours(1), 32L, activeJobId, null])!;

        _ = deleted.Should().Contain(oldAttempt);
        _ = Directory.Exists(activeAttempt).Should().BeTrue();
        _ = Directory.Exists(recentAttempt).Should().BeTrue();
    }

    [Fact(DisplayName = "Unmarked orphan attempts are quota-cleaned while active attempts are retained")]
    public void CleanupRecoveryDirectories_OrphanAttemptWithoutMarker_IsCleaned()
    {
        Guid orphanJobId = Guid.NewGuid();
        Guid activeJobId = Guid.NewGuid();
        string orphanAttempt = CreateAttempt(orphanJobId, Guid.NewGuid().ToString(), DateTime.UtcNow.AddDays(-10), 32, marker: false);
        string activeAttempt = CreateAttempt(activeJobId, Guid.NewGuid().ToString(), DateTime.UtcNow.AddDays(-10), 32, marker: false);

        System.Reflection.MethodInfo cleanupMethod = typeof(HttpJobPollerService).GetMethod(
            "CleanupRecoveryDirectories",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CleanupRecoveryDirectories is missing.");
        IReadOnlyList<string> deleted = (IReadOnlyList<string>)cleanupMethod.Invoke(
            null,
            [_workingDirectory, DateTime.UtcNow, TimeSpan.FromHours(1), 32L, null, new[] { activeAttempt }])!;

        _ = deleted.Should().Contain(orphanAttempt);
        _ = Directory.Exists(activeAttempt).Should().BeTrue();
    }

    private string CreateRecoveryAttempt(Guid jobId, string name, DateTime markerTimeUtc, int payloadSize)
        => CreateAttempt(jobId, name, markerTimeUtc, payloadSize, marker: true);

    private string CreateAttempt(Guid jobId, string name, DateTime lastWriteUtc, int payloadSize, bool marker)
    {
        string attempt = Path.Combine(_workingDirectory, jobId.ToString(), name);
        _ = Directory.CreateDirectory(attempt);
        if (marker)
        {
            File.WriteAllText(Path.Combine(attempt, ".printfarmer-recovery.json"), "{}");
        }
        File.WriteAllBytes(Path.Combine(attempt, "result.gcode"), new byte[payloadSize]);
        File.SetLastWriteTimeUtc(
            marker
                ? Path.Combine(attempt, ".printfarmer-recovery.json")
                : Path.Combine(attempt, "result.gcode"),
            lastWriteUtc);
        Directory.SetLastWriteTimeUtc(attempt, lastWriteUtc);
        return attempt;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
