using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Printer model catalog entry including optional build volume and defaults.
/// </summary>
public record PrinterModelDto(
    Guid Id,
    string Name,
    Guid ManufacturerId,
    MotionType? MotionType = null,
    double? MaxX = null,
    double? MaxY = null,
    double? MaxZ = null,
    PrinterBackend? DefaultBackend = null,
    string[]? SupportedFilamentTypes = null,

    // Default capabilities that can be inherited by new printers
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    bool SupportsAutoLeveling = false,

    // Temperature ranges
    int? MaxBedTemp = null,

    // Speed capabilities
    int? MaxPrintSpeed = null,

    // Default power consumption
    decimal? DefaultWattage = null,

    // Default machine hourly rate
    decimal? DefaultHourlyRate = null,

    // Toolhead templates for multi-toolhead printers
    PrinterModelToolheadDto[]? Toolheads = null);
