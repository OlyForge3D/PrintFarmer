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
    double? DefaultNozzleDiameter = null,
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    int? NumberOfExtruders = null,
    bool? SupportsAutoLeveling = null,

    // Temperature ranges
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,

    // Speed capabilities
    int? MaxPrintSpeed = null);
