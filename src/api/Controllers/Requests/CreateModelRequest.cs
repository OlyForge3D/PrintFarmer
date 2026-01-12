namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

public record CreateModelRequest(
    [BindRequired] // NOSONAR S6964: Binding is explicit; Guid must be supplied
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
