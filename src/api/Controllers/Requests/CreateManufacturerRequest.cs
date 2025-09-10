namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

/// <summary>
/// Request payload for creating a manufacturer.
/// Frontend sends { "name": "Prusa" } so we bind to this shape instead of raw string.
/// </summary>
public record CreateManufacturerRequest(
    [property: Required, MinLength(1), BindRequired]
    string Name);
