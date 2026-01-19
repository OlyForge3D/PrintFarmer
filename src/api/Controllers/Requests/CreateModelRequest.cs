using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Farm.Web.Api.Controllers.Requests;
/// <summary>
/// Request object for creating a printer model.
/// Note: Nozzle diameter and max hotend temp are now defined per-toolhead.
/// </summary>
public record CreateModelRequest(
    [property: System.Text.Json.Serialization.JsonRequired]
    [BindRequired]
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
