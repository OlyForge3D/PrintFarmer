namespace Farm.Slicer.Module.Api.Controllers.Requests;

/// <summary>
/// Request to update slicer model name aliases for a printer model.
/// Maps slicer-specific names (OrcaSlicer, PrusaSlicer) to a canonical PrinterModel.
/// </summary>
public record UpdateModelAliasesRequest(
    List<string>? OrcaSlicerNames = null,
    List<string>? PrusaSlicerNames = null);
