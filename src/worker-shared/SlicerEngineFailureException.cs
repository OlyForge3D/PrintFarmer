using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Worker.Core;

/// <summary>
/// A slicing failure that carries a redacted, client-safe <see cref="SliceFailureReason"/> alongside
/// the verbatim admin-only diagnostic in <see cref="Exception.Message"/> (issue #1811).
/// </summary>
/// <remarks>
/// Before this existed the exception message was the only channel from a pipeline to
/// <c>HttpJobPollerService</c>, so the reason a job failed could only be recovered by parsing prose.
/// Carrying the classification structurally means the API never has to infer it from text, and the
/// client-safe channel cannot accidentally inherit a container path or model filename from the
/// message.
/// </remarks>
public sealed class SlicerEngineFailureException : InvalidOperationException
{
    /// <summary>Initializes a new instance with an unclassified reason.</summary>
    public SlicerEngineFailureException()
        : this(SliceFailureReason.SlicerFailed, "The slicing engine failed.")
    {
    }

    /// <summary>Initializes a new instance with an unclassified reason and a diagnostic message.</summary>
    /// <param name="message">The verbatim, admin-only diagnostic.</param>
    public SlicerEngineFailureException(string message)
        : this(SliceFailureReason.SlicerFailed, message)
    {
    }

    /// <summary>Initializes a new instance with an unclassified reason, message and inner exception.</summary>
    /// <param name="message">The verbatim, admin-only diagnostic.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SlicerEngineFailureException(string message, Exception? innerException)
        : base(message, innerException) => Reason = SliceFailureReason.SlicerFailed;

    /// <summary>Initializes a new instance carrying an explicit classification.</summary>
    /// <param name="reason">The redacted, client-safe classification.</param>
    /// <param name="message">The verbatim, admin-only diagnostic.</param>
    /// <param name="innerException">The underlying failure, when there is one.</param>
    public SlicerEngineFailureException(
        SliceFailureReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    /// <summary>
    /// The redacted classification reported to every caller. Never derived from job-supplied text.
    /// </summary>
    public SliceFailureReason Reason { get; }
}
