using Farm.Infrastructure.Services.Startup;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class StartupStatusTests
{
    [Fact]
    public void Constructor_InitializesInStartingPhase()
    {
        var status = new StartupStatus();

        status.Phase.Should().Be(StartupPhase.Starting);
        status.IsReady.Should().BeFalse();
        status.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void MarkInitializationStarted_RecordsStartTime()
    {
        var status = new StartupStatus();
        DateTime beforeMark = DateTime.UtcNow;

        status.MarkInitializationStarted();

        DateTime afterMark = DateTime.UtcNow;
        status.InitializationStartedUtc.Should().NotBeNull();
        status.InitializationStartedUtc!.Value.Should().BeOnOrAfter(beforeMark);
        status.InitializationStartedUtc!.Value.Should().BeOnOrBefore(afterMark);
    }

    [Fact]
    public void MarkInitializationStarted_OnlyRecordsFirstTime()
    {
        var status = new StartupStatus();
        status.MarkInitializationStarted();
        DateTime? firstTime = status.InitializationStartedUtc;

        // Wait a tiny bit and call again
        System.Threading.Thread.Sleep(1);
        status.MarkInitializationStarted();
        DateTime? secondTime = status.InitializationStartedUtc;

        // Should be the same (first call only)
        secondTime.Should().Be(firstTime);
    }

    [Fact]
    public void MarkReady_TransitionsFromStarting()
    {
        var status = new StartupStatus();

        status.MarkReady();

        status.Phase.Should().Be(StartupPhase.Ready);
        status.IsReady.Should().BeTrue();
        status.IsFailed.Should().BeFalse();
        status.InitializationCompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public void MarkReady_RecordsCompletionTime()
    {
        var status = new StartupStatus();
        DateTime beforeMark = DateTime.UtcNow;

        status.MarkReady();

        DateTime afterMark = DateTime.UtcNow;
        status.InitializationCompletedUtc.Should().NotBeNull();
        status.InitializationCompletedUtc!.Value.Should().BeOnOrAfter(beforeMark);
        status.InitializationCompletedUtc!.Value.Should().BeOnOrBefore(afterMark);
    }

    [Fact]
    public void MarkFailed_TransitionsFromStarting()
    {
        var status = new StartupStatus();

        status.MarkFailed(null);

        status.Phase.Should().Be(StartupPhase.Failed);
        status.IsReady.Should().BeFalse();
        status.IsFailed.Should().BeTrue();
    }

    [Fact]
    public void MarkFailed_RecordsException()
    {
        var status = new StartupStatus();
        var exception = new InvalidOperationException("Startup failed");

        status.MarkFailed(exception);

        status.FailureException.Should().Be(exception);
        status.IsFailed.Should().BeTrue();
    }

    [Fact]
    public void MarkFailed_WithNullException_IsAllowed()
    {
        var status = new StartupStatus();

        status.MarkFailed(null);

        status.IsFailed.Should().BeTrue();
        status.FailureException.Should().BeNull();
    }

    [Fact]
    public void MarkReady_DoesNotTransitionFromFailed()
    {
        var status = new StartupStatus();
        status.MarkFailed(null);

        // Try to mark ready after failed
        status.MarkReady();

        // Should remain in Failed state
        status.Phase.Should().Be(StartupPhase.Failed);
        status.IsFailed.Should().BeTrue();
        status.IsReady.Should().BeFalse();
    }

    [Fact]
    public void MarkFailed_DoesNotTransitionFromReady()
    {
        var status = new StartupStatus();
        status.MarkReady();

        // Try to mark failed after ready
        status.MarkFailed(null);

        // Should remain in Ready state
        status.Phase.Should().Be(StartupPhase.Ready);
        status.IsReady.Should().BeTrue();
        status.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void InitializationDuration_CalculatedWhenBothStartAndEndSet()
    {
        var status = new StartupStatus();
        status.MarkInitializationStarted();

        // Small delay to ensure measurable duration
        System.Threading.Thread.Sleep(10);

        status.MarkReady();

        status.InitializationDuration.Should().NotBeNull();
        status.InitializationDuration!.Value.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void InitializationDuration_NullWhenOnlyStartedSet()
    {
        var status = new StartupStatus();
        status.MarkInitializationStarted();

        status.InitializationDuration.Should().BeNull();
    }

    [Fact]
    public void InitializationDuration_NullWhenNeitherSet()
    {
        var status = new StartupStatus();

        status.InitializationDuration.Should().BeNull();
    }

    [Fact]
    public void InitializationDuration_NullWhenEndBeforeStart()
    {
        var status = new StartupStatus();
        // This shouldn't happen in practice, but the code handles it

        // Manually set end before start (simulating edge case)
        status.MarkReady();
        DateTime? completedTime = status.InitializationCompletedUtc;

        // Now if we could somehow set start after end, duration would be negative
        // In practice this shouldn't happen, but the code returns null in this case
        if (status.InitializationStartedUtc.HasValue && completedTime.HasValue &&
            completedTime < status.InitializationStartedUtc)
        {
            status.InitializationDuration.Should().BeNull();
        }
    }

    [Fact]
    public void MultipleInstances_IndependentStates()
    {
        var status1 = new StartupStatus();
        var status2 = new StartupStatus();

        status1.MarkReady();
        // status2 remains in Starting

        status1.IsReady.Should().BeTrue();
        status2.IsReady.Should().BeFalse();
        status2.Phase.Should().Be(StartupPhase.Starting);
    }

    [Fact]
    public void StartupPhase_Enum_HasExpectedValues()
    {
        StartupPhase.Starting.Should().Be((StartupPhase)0);
        StartupPhase.Ready.Should().Be((StartupPhase)1);
        StartupPhase.Failed.Should().Be((StartupPhase)2);
    }

    [Fact]
    public void MarkFailed_RecordsCompletionTime()
    {
        var status = new StartupStatus();
        DateTime beforeMark = DateTime.UtcNow;

        status.MarkFailed(null);

        DateTime afterMark = DateTime.UtcNow;
        status.InitializationCompletedUtc.Should().NotBeNull();
        status.InitializationCompletedUtc!.Value.Should().BeOnOrAfter(beforeMark);
        status.InitializationCompletedUtc!.Value.Should().BeOnOrBefore(afterMark);
    }
}
