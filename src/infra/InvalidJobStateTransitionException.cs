using System.Text.Json.Serialization;

// Serialization constructors removed (legacy binary serialization not required)
namespace Farm.Infrastructure;

/// <summary>
/// Exception thrown when an invalid job state transition is attempted.
/// </summary>
public class InvalidJobStateTransitionException : InvalidOperationException
{
    public JobState FromState { get; }

    public JobState ToState { get; }

    public InvalidJobStateTransitionException(JobState fromState, JobState toState)
        : base($"Invalid transition from {fromState} to {toState}")
    {
        FromState = fromState;
        ToState = toState;
    }

    // Standard optional exception constructors (for completeness)
    public InvalidJobStateTransitionException()
    {
    }

    public InvalidJobStateTransitionException(string message) : base(message)
    {
    }

    public InvalidJobStateTransitionException(string message, Exception inner) : base(message, inner)
    {
    }
}
