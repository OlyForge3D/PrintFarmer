namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Response returned after successfully adding a completed slice job to the print queue.
/// </summary>
public sealed record AddSliceToQueueResponse
{
    /// <summary>The ID of the newly created print job in the queue.</summary>
    public required Guid PrintJobId { get; init; }

    /// <summary>
    /// Queue position assigned to the job, or null if position could not be determined.
    /// </summary>
    public int? QueuePosition { get; init; }

    /// <summary>Status message describing the outcome.</summary>
    public string? Message { get; init; }
}
