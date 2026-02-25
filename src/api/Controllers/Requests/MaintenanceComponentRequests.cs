using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Request payload for creating a maintenance component (parts inventory item).
/// </summary>
public record CreateMaintenanceComponentRequest(
    [Required, MinLength(1), MaxLength(200)]
    string Name,
    [Required, MinLength(1), MaxLength(100)]
    string Category,
    [MaxLength(100)]
    string? Sku = null,
    [MaxLength(1000)]
    string? Description = null,
    decimal? UnitCost = null,
    [MaxLength(200)]
    string? Supplier = null,
    [Url, MaxLength(500)]
    string? Url = null,
    int InStock = 0,
    int MinimumStock = 0);

/// <summary>
/// Request payload for updating a maintenance component.
/// </summary>
public record UpdateMaintenanceComponentRequest(
    [Required, MinLength(1), MaxLength(200)]
    string Name,
    [Required, MinLength(1), MaxLength(100)]
    string Category,
    [MaxLength(100)]
    string? Sku = null,
    [MaxLength(1000)]
    string? Description = null,
    decimal? UnitCost = null,
    [MaxLength(200)]
    string? Supplier = null,
    [Url, MaxLength(500)]
    string? Url = null,
    int InStock = 0,
    int MinimumStock = 0);
