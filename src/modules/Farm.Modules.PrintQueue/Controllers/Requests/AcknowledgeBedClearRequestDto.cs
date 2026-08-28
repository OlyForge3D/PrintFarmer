namespace Farm.Modules.PrintQueue.Controllers.Requests;

/// <summary>
/// Request body for <c>POST /api/job-queue/{jobId}/acknowledge-bed-clear-and-start</c>.
/// </summary>
public sealed class AcknowledgeBedClearRequestDto
{
    /// <summary>
    /// Printer the job is assigned to. Must match <c>PrintJob.AssignedPrinterId</c>.
    /// </summary>
    public Guid PrinterId { get; init; }

    /// <summary>
    /// Stable caller-supplied idempotency key for this acknowledgement.
    /// Can also be supplied as the <c>Idempotency-Key</c> HTTP header;
    /// the header takes precedence if both are provided.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Printer configuration revision current at request time.
    /// Dispatch rejects the acknowledgement if the printer has advanced beyond this value.
    /// </summary>
    public long? ExpectedPrinterConfigRevision { get; init; }
}
