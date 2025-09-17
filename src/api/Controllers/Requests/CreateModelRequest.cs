namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.ModelBinding;

public record CreateModelRequest(
    [BindRequired] // NOSONAR S6964: Binding is explicit; Guid must be supplied
    Guid ManufacturerId,
    [Required, MinLength(1)]
    string Name,
    PrinterType? Type,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend,
    Guid[]? SupportedFilamentTypeIds);
