namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request to update slicer model name aliases for a printer model.
/// Maps slicer-specific names (OrcaSlicer, PrusaSlicer) to a canonical PrinterModel.
/// </summary>
public record UpdateModelAliasesRequest(
    /// <summary>
    /// List of OrcaSlicer model names that should map to this printer model.
    /// </summary>
    List<string>? OrcaSlicerNames = null,

    /// <summary>
    /// List of PrusaSlicer model names that should map to this printer model.
    /// </summary>
    List<string>? PrusaSlicerNames = null
);
