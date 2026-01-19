using System.Text.Json.Serialization;

// Serialization constructors removed (legacy binary serialization not required)
namespace Farm.Infrastructure;

/// <summary>
/// Formal job lifecycle states following the pattern: queued → dispatched → processing → (succeeded | failed | cancelled | dead-letter).
/// </summary>
// Single JsonConverter attribute (duplicate removed)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState
{
    /// <summary>
    /// Job has been created and is waiting to be assigned.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// Job has been assigned to a processor but not yet started.
    /// </summary>
    Dispatched = 1,

    /// <summary>
    /// Job is actively being processed.
    /// </summary>
    Processing = 2,

    /// <summary>
    /// Job completed successfully (terminal state).
    /// </summary>
    Succeeded = 3,

    /// <summary>
    /// Job failed during processing (terminal state).
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Job was cancelled by user or system (terminal state).
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// Job failed in an unrecoverable way and cannot be retried (terminal state).
    /// </summary>
    DeadLetter = 6
}
