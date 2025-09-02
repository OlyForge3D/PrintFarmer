namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;
using Farm.Web.Shared;

public record CreateModelRequest(
    [property: Required]
    Guid ManufacturerId,
    [property: Required, MinLength(1)]
    string Name,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend);
