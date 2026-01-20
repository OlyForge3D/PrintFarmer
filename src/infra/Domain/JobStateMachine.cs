using Farm.Infrastructure.Domain;

// Serialization constructors removed (legacy binary serialization not required)
namespace Farm.Infrastructure;

/// <summary>
/// State machine for managing job lifecycle transitions with validation.
/// </summary>
public static class JobStateMachine
{
    /// <summary>
    /// Valid transitions from each state.
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
    /// Check if a state transition is valid.
    /// </summary>
    /// <param name="fromState">Current state.</param>
    /// <param name="toState">Desired state.</param>
    /// <returns>True if transition is valid.</returns>
    public static bool IsValidTransition(JobState fromState, JobState toState)
    {
        return ValidTransitions.TryGetValue(fromState, out HashSet<JobState>? validStates) && validStates.Contains(toState);
    }

    /// <summary>
    /// Validate a state transition and throw exception if invalid.
    /// </summary>
    /// <param name="fromState">Current state.</param>
    /// <param name="toState">Desired state.</param>
    /// <exception cref="InvalidJobStateTransitionException">Thrown when transition is invalid.</exception>
    public static void ValidateTransition(JobState fromState, JobState toState)
    {
        if (!IsValidTransition(fromState, toState))
        {
            throw new InvalidJobStateTransitionException(fromState, toState);
        }
    }

    /// <summary>
    /// Check if a state is terminal (no further transitions possible).
    /// </summary>
    /// <param name="state">State to check.</param>
    /// <returns>True if state is terminal.</returns>
    public static bool IsTerminal(JobState state)
    {
        return ValidTransitions.TryGetValue(state, out HashSet<JobState>? validStates) && validStates.Count == 0;
    }

    /// <summary>
    /// Get all valid next states from the current state.
    /// </summary>
    /// <param name="currentState">Current state.</param>
    /// <returns>Collection of valid next states.</returns>
    public static IReadOnlyCollection<JobState> GetValidNextStates(JobState currentState)
    {
        return ValidTransitions.TryGetValue(currentState, out HashSet<JobState>? validStates)
            ? validStates.ToList().AsReadOnly()
            : Array.Empty<JobState>();
    }

    /// <summary>
    /// Get all terminal states.
    /// </summary>
    /// <returns>Collection of all terminal states.</returns>
    public static IReadOnlyCollection<JobState> GetTerminalStates()
    {
        return ValidTransitions.Where(kvp => kvp.Value.Count == 0)
                              .Select(kvp => kvp.Key)
                              .ToList()
                              .AsReadOnly();
    }

    /// <summary>
    /// Perform a state transition with validation.
    /// </summary>
    /// <param name="fromState">Current state.</param>
    /// <param name="toState">Desired state.</param>
    /// <returns>The new state if transition is valid.</returns>
    /// <exception cref="InvalidJobStateTransitionException">Thrown when transition is invalid.</exception>
    public static JobState Transition(JobState fromState, JobState toState)
    {
        ValidateTransition(fromState, toState);
        return toState;
    }
}
