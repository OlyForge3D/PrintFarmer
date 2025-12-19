namespace Farm.Infrastructure.Services.Printers;

using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;

/// <summary>
/// Request object for creating a printer model.
/// Infrastructure version (no ASP.NET binding attributes).
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
    double? DefaultNozzleDiameter = 0.4,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    bool SupportsAutoLeveling = false,

    // Temperature ranges
    int? MinHotendTemp = 0,
    int? MaxHotendTemp = 300,
    int? MinBedTemp = 0,
    int? MaxBedTemp = 120,

    // Speed capabilities
    int? MaxPrintSpeed = 150);
