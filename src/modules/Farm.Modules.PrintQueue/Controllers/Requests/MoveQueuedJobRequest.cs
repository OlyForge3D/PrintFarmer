namespace Farm.Modules.PrintQueue.Controllers.Requests;

/// <summary>
/// Identifies the queued job that the moved job should be placed next to.
/// Exactly one neighbour must be supplied.
/// </summary>
public sealed class MoveQueuedJobRequest
{
    /// <summary>The queued job the moved job should immediately precede.</summary>
    public Guid? BeforeJobId { get; init; }

    /// <summary>The ETag of the queued job the moved job should immediately precede.</summary>
    public string? BeforeJobETag { get; init; }

    /// <summary>The queued job the moved job should immediately follow.</summary>
    public Guid? AfterJobId { get; init; }

    /// <summary>The ETag of the queued job the moved job should immediately follow.</summary>
    public string? AfterJobETag { get; init; }
}
