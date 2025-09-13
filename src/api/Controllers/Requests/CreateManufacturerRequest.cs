namespace Farm.Web.Api.Controllers.Requests;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request payload for creating a manufacturer.
/// Frontend sends { "name": "Prusa" } so we bind to this shape instead of raw string.
/// </summary>
public record CreateManufacturerRequest(
    // Validation attributes must be applied to the primary constructor parameter (not the generated property) for records.
    [Required, MinLength(1)]
    string Name);
