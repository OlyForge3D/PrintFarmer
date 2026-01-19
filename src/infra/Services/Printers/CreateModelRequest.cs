using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Printers;
/// <summary>
/// Request object for creating a printer model.
/// Infrastructure version (no ASP.NET binding attributes).
/// Note: Nozzle diameter and max hotend temp are now defined per-toolhead.
/// </summary>
public record CreateModelRequest(
    Guid ManufacturerId,
    [Required, MinLength(1)]
    string Name,
    MotionType? Type,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend,
    Guid[]? SupportedFilamentTypeIds,

    // Default capabilities that can be inherited by new printers
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    bool SupportsAutoLeveling = false,

    // Temperature ranges (nozzle/hotend temps are on toolheads)
    int? MaxBedTemp = 120,

    // Speed capabilities
    int? MaxPrintSpeed = 150);
