using Farm.Web.Shared;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Comprehensive unit tests for JobStateMachine with 100% transition coverage
/// </summary>
public class JobStateMachineTests
{
    #region Valid Transition Tests

    [Theory]
    [InlineData(JobState.Queued, JobState.Dispatched)]
    [InlineData(JobState.Queued, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.Processing)]
    [InlineData(JobState.Dispatched, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.DeadLetter)]
    [InlineData(JobState.Processing, JobState.Succeeded)]
    [InlineData(JobState.Processing, JobState.Failed)]
    [InlineData(JobState.Processing, JobState.Cancelled)]
    [InlineData(JobState.Processing, JobState.DeadLetter)]
    public void IsValidTransition_ValidTransitions_ShouldReturnTrue(JobState fromState, JobState toState)
    {
        // Act
        var isValid = JobStateMachine.IsValidTransition(fromState, toState);

        // Assert
        isValid.Should().BeTrue($"transition from {fromState} to {toState} should be valid");
    }

    [Theory]
    [InlineData(JobState.Queued, JobState.Dispatched)]
    [InlineData(JobState.Queued, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.Processing)]
    [InlineData(JobState.Dispatched, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.DeadLetter)]
    [InlineData(JobState.Processing, JobState.Succeeded)]
    [InlineData(JobState.Processing, JobState.Failed)]
    [InlineData(JobState.Processing, JobState.Cancelled)]
    [InlineData(JobState.Processing, JobState.DeadLetter)]
    public void ValidateTransition_ValidTransitions_ShouldNotThrow(JobState fromState, JobState toState)
    {
        // Act & Assert
        var action = () => JobStateMachine.ValidateTransition(fromState, toState);
        action.Should().NotThrow($"transition from {fromState} to {toState} should be valid");
    }

    [Theory]
    [InlineData(JobState.Queued, JobState.Dispatched)]
    [InlineData(JobState.Queued, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.Processing)]
    [InlineData(JobState.Dispatched, JobState.Cancelled)]
    [InlineData(JobState.Dispatched, JobState.DeadLetter)]
    [InlineData(JobState.Processing, JobState.Succeeded)]
    [InlineData(JobState.Processing, JobState.Failed)]
    [InlineData(JobState.Processing, JobState.Cancelled)]
    [InlineData(JobState.Processing, JobState.DeadLetter)]
    public void Transition_ValidTransitions_ShouldReturnNewState(JobState fromState, JobState toState)
    {
        // Act
        var resultState = JobStateMachine.Transition(fromState, toState);

        // Assert
        resultState.Should().Be(toState);
    }

    #endregion

    #region Invalid Transition Tests

    [Theory]
    // From Queued - invalid transitions
    [InlineData(JobState.Queued, JobState.Processing)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Queued, JobState.Failed)]
    [InlineData(JobState.Queued, JobState.DeadLetter)]
    // From Dispatched - invalid transitions  
    [InlineData(JobState.Dispatched, JobState.Queued)]
    [InlineData(JobState.Dispatched, JobState.Succeeded)]
    [InlineData(JobState.Dispatched, JobState.Failed)]
    // From Processing - invalid transitions
    [InlineData(JobState.Processing, JobState.Queued)]
    [InlineData(JobState.Processing, JobState.Dispatched)]
    // From terminal states - all transitions invalid
    [InlineData(JobState.Succeeded, JobState.Queued)]
    [InlineData(JobState.Succeeded, JobState.Dispatched)]
    [InlineData(JobState.Succeeded, JobState.Processing)]
    [InlineData(JobState.Succeeded, JobState.Failed)]
    [InlineData(JobState.Succeeded, JobState.Cancelled)]
    [InlineData(JobState.Succeeded, JobState.DeadLetter)]
    [InlineData(JobState.Failed, JobState.Queued)]
    [InlineData(JobState.Failed, JobState.Dispatched)]
    [InlineData(JobState.Failed, JobState.Processing)]
    [InlineData(JobState.Failed, JobState.Succeeded)]
    [InlineData(JobState.Failed, JobState.Cancelled)]
    [InlineData(JobState.Failed, JobState.DeadLetter)]
    [InlineData(JobState.Cancelled, JobState.Queued)]
    [InlineData(JobState.Cancelled, JobState.Dispatched)]
    [InlineData(JobState.Cancelled, JobState.Processing)]
    [InlineData(JobState.Cancelled, JobState.Succeeded)]
    [InlineData(JobState.Cancelled, JobState.Failed)]
    [InlineData(JobState.Cancelled, JobState.DeadLetter)]
    [InlineData(JobState.DeadLetter, JobState.Queued)]
    [InlineData(JobState.DeadLetter, JobState.Dispatched)]
    [InlineData(JobState.DeadLetter, JobState.Processing)]
    [InlineData(JobState.DeadLetter, JobState.Succeeded)]
    [InlineData(JobState.DeadLetter, JobState.Failed)]
    [InlineData(JobState.DeadLetter, JobState.Cancelled)]
    public void IsValidTransition_InvalidTransitions_ShouldReturnFalse(JobState fromState, JobState toState)
    {
        // Act
        var isValid = JobStateMachine.IsValidTransition(fromState, toState);

        // Assert
        isValid.Should().BeFalse($"transition from {fromState} to {toState} should be invalid");
    }

    [Theory]
    // From Queued - invalid transitions
    [InlineData(JobState.Queued, JobState.Processing)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Queued, JobState.Failed)]
    [InlineData(JobState.Queued, JobState.DeadLetter)]
    // From Dispatched - invalid transitions  
    [InlineData(JobState.Dispatched, JobState.Queued)]
    [InlineData(JobState.Dispatched, JobState.Succeeded)]
    [InlineData(JobState.Dispatched, JobState.Failed)]
    // From Processing - invalid transitions
    [InlineData(JobState.Processing, JobState.Queued)]
    [InlineData(JobState.Processing, JobState.Dispatched)]
    // From terminal states - all transitions invalid
    [InlineData(JobState.Succeeded, JobState.Queued)]
    [InlineData(JobState.Failed, JobState.Dispatched)]
    [InlineData(JobState.Cancelled, JobState.Processing)]
    [InlineData(JobState.DeadLetter, JobState.Succeeded)]
    public void ValidateTransition_InvalidTransitions_ShouldThrowException(JobState fromState, JobState toState)
    {
        // Act & Assert
        var action = () => JobStateMachine.ValidateTransition(fromState, toState);
        action.Should().Throw<InvalidJobStateTransitionException>()
              .WithMessage($"Invalid transition from {fromState} to {toState}");
    }

    [Theory]
    [InlineData(JobState.Queued, JobState.Processing)]
    [InlineData(JobState.Dispatched, JobState.Succeeded)]
    [InlineData(JobState.Processing, JobState.Queued)]
    [InlineData(JobState.Succeeded, JobState.Failed)]
    [InlineData(JobState.Failed, JobState.Processing)]
    [InlineData(JobState.Cancelled, JobState.Dispatched)]
    [InlineData(JobState.DeadLetter, JobState.Processing)]
    public void Transition_InvalidTransitions_ShouldThrowException(JobState fromState, JobState toState)
    {
        // Act & Assert
        var action = () => JobStateMachine.Transition(fromState, toState);
        action.Should().Throw<InvalidJobStateTransitionException>()
              .WithMessage($"Invalid transition from {fromState} to {toState}")
              .Which.FromState.Should().Be(fromState);

        action.Should().Throw<InvalidJobStateTransitionException>()
              .Which.ToState.Should().Be(toState);
    }

    #endregion

    #region Terminal State Tests

    [Theory]
    [InlineData(JobState.Succeeded)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.DeadLetter)]
    public void IsTerminal_TerminalStates_ShouldReturnTrue(JobState state)
    {
        // Act
        var isTerminal = JobStateMachine.IsTerminal(state);

        // Assert
        isTerminal.Should().BeTrue($"{state} should be a terminal state");
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Dispatched)]
    [InlineData(JobState.Processing)]
    public void IsTerminal_NonTerminalStates_ShouldReturnFalse(JobState state)
    {
        // Act
        var isTerminal = JobStateMachine.IsTerminal(state);

        // Assert
        isTerminal.Should().BeFalse($"{state} should not be a terminal state");
    }

    [Fact]
    public void GetTerminalStates_ShouldReturnAllTerminalStates()
    {
        // Act
        var terminalStates = JobStateMachine.GetTerminalStates();

        // Assert
        terminalStates.Should().BeEquivalentTo(new[]
        {
            JobState.Succeeded,
            JobState.Failed,
            JobState.Cancelled,
            JobState.DeadLetter
        });
    }

    #endregion

    #region Valid Next States Tests

    [Fact]
    public void GetValidNextStates_FromQueued_ShouldReturnExpectedStates()
    {
        // Act
        var validStates = JobStateMachine.GetValidNextStates(JobState.Queued);

        // Assert
        validStates.Should().BeEquivalentTo(new[] { JobState.Dispatched, JobState.Cancelled });
    }

    [Fact]
    public void GetValidNextStates_FromDispatched_ShouldReturnExpectedStates()
    {
        // Act
        var validStates = JobStateMachine.GetValidNextStates(JobState.Dispatched);

        // Assert
        validStates.Should().BeEquivalentTo(new[] { JobState.Processing, JobState.Cancelled, JobState.DeadLetter });
    }

    [Fact]
    public void GetValidNextStates_FromProcessing_ShouldReturnExpectedStates()
    {
        // Act
        var validStates = JobStateMachine.GetValidNextStates(JobState.Processing);

        // Assert
        validStates.Should().BeEquivalentTo(new[]
        {
            JobState.Succeeded,
            JobState.Failed,
            JobState.Cancelled,
            JobState.DeadLetter
        });
    }

    [Theory]
    [InlineData(JobState.Succeeded)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.DeadLetter)]
    public void GetValidNextStates_FromTerminalStates_ShouldReturnEmptyCollection(JobState terminalState)
    {
        // Act
        var validStates = JobStateMachine.GetValidNextStates(terminalState);

        // Assert
        validStates.Should().BeEmpty($"{terminalState} is terminal and should have no valid next states");
    }

    #endregion

    #region Exception Tests

    [Fact]
    public void InvalidJobStateTransitionException_ShouldContainCorrectStates()
    {
        // Arrange
        var fromState = JobState.Succeeded;
        var toState = JobState.Processing;

        // Act
        var exception = new InvalidJobStateTransitionException(fromState, toState);

        // Assert
        exception.FromState.Should().Be(fromState);
        exception.ToState.Should().Be(toState);
        exception.Message.Should().Be($"Invalid transition from {fromState} to {toState}");
    }

    #endregion

    #region Edge Case Tests

    [Theory]
    [InlineData(JobState.Queued, JobState.Queued)]
    [InlineData(JobState.Dispatched, JobState.Dispatched)]
    [InlineData(JobState.Processing, JobState.Processing)]
    [InlineData(JobState.Succeeded, JobState.Succeeded)]
    [InlineData(JobState.Failed, JobState.Failed)]
    [InlineData(JobState.Cancelled, JobState.Cancelled)]
    [InlineData(JobState.DeadLetter, JobState.DeadLetter)]
    public void IsValidTransition_SameState_ShouldReturnFalse(JobState state, JobState sameState)
    {
        // Act
        var isValid = JobStateMachine.IsValidTransition(state, sameState);

        // Assert
        isValid.Should().BeFalse("transitioning to the same state should be invalid");
    }

    [Fact]
    public void GetValidNextStates_ShouldReturnReadOnlyCollection()
    {
        // Act
        var validStates = JobStateMachine.GetValidNextStates(JobState.Queued);

        // Assert
        validStates.Should().BeAssignableTo<IReadOnlyCollection<JobState>>();
    }

    [Fact]
    public void GetTerminalStates_ShouldReturnReadOnlyCollection()
    {
        // Act
        var terminalStates = JobStateMachine.GetTerminalStates();

        // Assert
        terminalStates.Should().BeAssignableTo<IReadOnlyCollection<JobState>>();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullWorkflow_QueuedToSucceeded_ShouldWork()
    {
        // Arrange
        var currentState = JobState.Queued;

        // Act & Assert - Full successful workflow
        currentState = JobStateMachine.Transition(currentState, JobState.Dispatched);
        currentState.Should().Be(JobState.Dispatched);

        currentState = JobStateMachine.Transition(currentState, JobState.Processing);
        currentState.Should().Be(JobState.Processing);

        currentState = JobStateMachine.Transition(currentState, JobState.Succeeded);
        currentState.Should().Be(JobState.Succeeded);

        // Terminal state - no further transitions possible
        JobStateMachine.IsTerminal(currentState).Should().BeTrue();
    }

    [Fact]
    public void FullWorkflow_QueuedToFailed_ShouldWork()
    {
        // Arrange
        var currentState = JobState.Queued;

        // Act & Assert - Workflow ending in failure
        currentState = JobStateMachine.Transition(currentState, JobState.Dispatched);
        currentState = JobStateMachine.Transition(currentState, JobState.Processing);
        currentState = JobStateMachine.Transition(currentState, JobState.Failed);

        currentState.Should().Be(JobState.Failed);
        JobStateMachine.IsTerminal(currentState).Should().BeTrue();
    }

    [Fact]
    public void FullWorkflow_QueuedToCancelled_ShouldWork()
    {
        // Arrange & Act - Job cancelled while queued
        var currentState = JobState.Queued;
        currentState = JobStateMachine.Transition(currentState, JobState.Cancelled);

        // Assert
        currentState.Should().Be(JobState.Cancelled);
        JobStateMachine.IsTerminal(currentState).Should().BeTrue();
    }

    [Fact]
    public void FullWorkflow_DispatchedToDeadLetter_ShouldWork()
    {
        // Arrange & Act - Job becomes dead letter before processing
        var currentState = JobState.Queued;
        currentState = JobStateMachine.Transition(currentState, JobState.Dispatched);
        currentState = JobStateMachine.Transition(currentState, JobState.DeadLetter);

        // Assert
        currentState.Should().Be(JobState.DeadLetter);
        JobStateMachine.IsTerminal(currentState).Should().BeTrue();
    }

    #endregion
}
