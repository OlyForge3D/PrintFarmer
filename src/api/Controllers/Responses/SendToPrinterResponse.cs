namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Response returned after sending a completed slice job's gcode to a printer.
/// </summary>
public sealed record SendToPrinterResponse
{
    /// <summary>The slice job ID whose gcode was sent.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The target printer ID.</summary>
    public required Guid PrinterId { get; init; }

    /// <summary>The filename of the uploaded gcode file.</summary>
    public required string FileName { get; init; }

    /// <summary>Whether the print was started immediately after upload.</summary>
    public bool PrintStarted { get; init; }

    /// <summary>Optional status message.</summary>
    public string? Message { get; init; }
}
