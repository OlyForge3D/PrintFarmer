namespace Farm.Infrastructure.Domain;

/// <summary>
/// Stores a committed, process-independent watermark for task mutations.
/// </summary>
public sealed class MutationCounter
{
    /// <summary>
    /// Singleton row identifier.
    /// </summary>
    public const int GlobalId = 1;

    /// <summary>
    /// Gets or sets the singleton row identifier.
    /// </summary>
    public int Id { get; set; } = GlobalId;

    /// <summary>
    /// Gets or sets the latest committed mutation sequence.
    /// </summary>
    public long Value { get; set; }
}
