namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request body for sending a completed slice job's gcode to a printer.
/// </summary>
public sealed record SendToPrinterRequest
{
    /// <summary>The ID of the target printer to send the gcode to.</summary>
    public required Guid PrinterId { get; init; }

    /// <summary>Whether to start printing immediately after upload completes.</summary>
    public bool StartPrint { get; init; }
}
