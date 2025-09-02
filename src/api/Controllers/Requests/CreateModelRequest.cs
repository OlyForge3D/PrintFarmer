namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Farm.Web.Shared;

public record CreateModelRequest(
    [property: Required, BindRequired] // NOSONAR S6964: Binding is explicit; value type required with [BindRequired]
    Guid ManufacturerId,
    [property: Required, MinLength(1)]
    string Name,
    double? MaxX,
    double? MaxY,
    double? MaxZ,
    PrinterBackend? DefaultBackend);
