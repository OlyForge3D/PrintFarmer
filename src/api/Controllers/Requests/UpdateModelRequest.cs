namespace Farm.Web.Api.Controllers.Requests;

using Farm.Infrastructure;

public record UpdateModelRequest(
    string Name,
    MotionType? Type,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend,
    Guid[]? SupportedFilamentTypeIds,

    // Default capabilities that can be inherited by new printers
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    int? NumberOfExtruders = null,
    bool? SupportsAutoLeveling = null,

    // Temperature ranges
    int? MaxBedTemp = null,

    // Speed capabilities
    int? MaxPrintSpeed = null,

    // Toolhead templates (contains nozzle diameter and max hotend temp)
    PrinterModelToolheadDto[]? Toolheads = null);
