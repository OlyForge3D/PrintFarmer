using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Controllers.Requests;

public record UpdateModelRequest(
    string Name,
    MotionType? MotionType,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend,
    Guid[]? SupportedFilamentTypeIds,

    // Default capabilities that can be inherited by new printers
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    bool? SupportsAutoLeveling = null,

    // Temperature ranges
    int? MaxBedTemp = null,

    // Speed capabilities
    int? MaxPrintSpeed = null,

    // Cost/energy defaults
    decimal? DefaultWattage = null,

    // Toolhead templates (contains nozzle diameter and max hotend temp)
    PrinterModelToolheadDto[]? Toolheads = null);
