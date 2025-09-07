using System.Text.Json.Serialization;

namespace Farm.Web.Shared;

/// <summary>
/// Formal job lifecycle states following the pattern: queued → dispatched → processing → (succeeded | failed | cancelled | dead-letter)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState
{
    /// <summary>
    /// Job has been created and is waiting to be assigned
    /// </summary>
    Queued = 0,
    
    /// <summary>
    /// Job has been assigned to a processor but not yet started
    /// </summary>
    Dispatched = 1,
    
    /// <summary>
    /// Job is actively being processed
    /// </summary>
    Processing = 2,
    
    /// <summary>
    /// Job completed successfully (terminal state)
    /// </summary>
    Succeeded = 3,
    
    /// <summary>
    /// Job failed during processing (terminal state)
    /// </summary>
    Failed = 4,
    
    /// <summary>
    /// Job was cancelled by user or system (terminal state)
    /// </summary>
    Cancelled = 5,
    
    /// <summary>
    /// Job failed in an unrecoverable way and cannot be retried (terminal state)
    /// </summary>
    DeadLetter = 6
}

/// <summary>
/// Exception thrown when an invalid job state transition is attempted
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
}

/// <summary>
/// State machine for managing job lifecycle transitions with validation
/// </summary>
public static class JobStateMachine
{
    /// <summary>
    /// Valid transitions from each state
    /// </summary>
    private static readonly Dictionary<JobState, HashSet<JobState>> ValidTransitions = new()
    {
        [JobState.Queued] = new HashSet<JobState> { JobState.Dispatched, JobState.Cancelled },
        [JobState.Dispatched] = new HashSet<JobState> { JobState.Processing, JobState.Cancelled, JobState.DeadLetter },
        [JobState.Processing] = new HashSet<JobState> { JobState.Succeeded, JobState.Failed, JobState.Cancelled, JobState.DeadLetter },
        // Terminal states have no valid transitions
        [JobState.Succeeded] = new HashSet<JobState>(),
        [JobState.Failed] = new HashSet<JobState>(),
        [JobState.Cancelled] = new HashSet<JobState>(),
        [JobState.DeadLetter] = new HashSet<JobState>()
    };

    /// <summary>
    /// Check if a state transition is valid
    /// </summary>
    /// <param name="fromState">Current state</param>
    /// <param name="toState">Desired state</param>
    /// <returns>True if transition is valid</returns>
    public static bool IsValidTransition(JobState fromState, JobState toState)
    {
        return ValidTransitions.TryGetValue(fromState, out var validStates) && validStates.Contains(toState);
    }

    /// <summary>
    /// Validate a state transition and throw exception if invalid
    /// </summary>
    /// <param name="fromState">Current state</param>
    /// <param name="toState">Desired state</param>
    /// <exception cref="InvalidJobStateTransitionException">Thrown when transition is invalid</exception>
    public static void ValidateTransition(JobState fromState, JobState toState)
    {
        if (!IsValidTransition(fromState, toState))
        {
            throw new InvalidJobStateTransitionException(fromState, toState);
        }
    }

    /// <summary>
    /// Check if a state is terminal (no further transitions possible)
    /// </summary>
    /// <param name="state">State to check</param>
    /// <returns>True if state is terminal</returns>
    public static bool IsTerminal(JobState state)
    {
        return ValidTransitions.TryGetValue(state, out var validStates) && validStates.Count == 0;
    }

    /// <summary>
    /// Get all valid next states from the current state
    /// </summary>
    /// <param name="currentState">Current state</param>
    /// <returns>Collection of valid next states</returns>
    public static IReadOnlyCollection<JobState> GetValidNextStates(JobState currentState)
    {
        return ValidTransitions.TryGetValue(currentState, out var validStates) 
            ? validStates.ToList().AsReadOnly()
            : Array.Empty<JobState>();
    }

    /// <summary>
    /// Get all terminal states
    /// </summary>
    /// <returns>Collection of all terminal states</returns>
    public static IReadOnlyCollection<JobState> GetTerminalStates()
    {
        return ValidTransitions.Where(kvp => kvp.Value.Count == 0)
                              .Select(kvp => kvp.Key)
                              .ToList()
                              .AsReadOnly();
    }

    /// <summary>
    /// Perform a state transition with validation
    /// </summary>
    /// <param name="fromState">Current state</param>
    /// <param name="toState">Desired state</param>
    /// <returns>The new state if transition is valid</returns>
    /// <exception cref="InvalidJobStateTransitionException">Thrown when transition is invalid</exception>
    public static JobState Transition(JobState fromState, JobState toState)
    {
        ValidateTransition(fromState, toState);
        return toState;
    }
}