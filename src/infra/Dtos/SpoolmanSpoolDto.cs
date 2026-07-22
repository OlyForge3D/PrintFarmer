using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Represents a single filament spool entity retrieved from Spoolman.
/// </summary>
public record SpoolmanSpoolDto(
    int Id,
    string Name,
    string Material,
    double? RemainingWeightG,
    string? ColorHex,
    bool InUse,
    string? FilamentName = null,
    string? Vendor = null,
    DateTime? RegisteredAt = null,
    DateTime? FirstUsedAt = null,
    DateTime? LastUsedAt = null,

    // Newly added extended fields (optional to preserve backward compatibility)
    double? InitialWeightG = null,
    double? UsedWeightG = null,
    double? SpoolWeightG = null,
    double? RemainingLengthMm = null,
    double? UsedLengthMm = null,
    string? Location = null,
    string? LotNumber = null,
    bool? Archived = null,
    double? Price = null,
    string? Comment = null,
    int? FilamentId = null)
{
    public double? UsedPercent
    {
        get
        {
            if (InitialWeightG.HasValue && InitialWeightG.Value > 0)
            {
                if (UsedWeightG.HasValue)
                {
                    return UsedWeightG.Value / InitialWeightG.Value * 100.0;
                }

                if (RemainingWeightG.HasValue)
                {
                    return (InitialWeightG.Value - RemainingWeightG.Value) / InitialWeightG.Value * 100.0;
                }
            }

            return null;
        }
    }

    public double? RemainingPercent
        => InitialWeightG.HasValue && InitialWeightG.Value > 0 && RemainingWeightG.HasValue
            ? RemainingWeightG.Value / InitialWeightG.Value * 100.0
            : null;
}

/// <summary>
/// Request to create or update a spool in Spoolman via its REST API.
/// Only non-null fields are sent (PATCH semantics for updates).
/// </summary>
public record SpoolmanSpoolRequest
{
    /// <summary>ID of the filament product this spool contains (required for create).</summary>
    public int? FilamentId { get; init; }

    /// <summary>Remaining weight of filament on the spool in grams.</summary>
    public double? RemainingWeight { get; init; }

    /// <summary>Initial (full) weight of filament on the spool in grams.</summary>
    public double? InitialWeight { get; init; }

    /// <summary>Weight of the empty spool itself in grams.</summary>
    public double? SpoolWeight { get; init; }

    /// <summary>Physical storage location of the spool.</summary>
    public string? Location { get; init; }

    /// <summary>Manufacturing lot/batch number.</summary>
    public string? LotNumber { get; init; }

    /// <summary>Purchase price of the spool.</summary>
    public double? Price { get; init; }

    /// <summary>Free-form user comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Whether the spool is archived (no longer in active use).</summary>
    public bool? Archived { get; init; }
}

/// <summary>
/// Request to create a Spoolman spool by resolving a retail barcode to a filament articleNumber.
/// </summary>
public record SpoolmanImportSpoolByBarcodeRequest
{
    public string? Barcode { get; init; }

    public double? RemainingWeight { get; init; }

    public double? InitialWeight { get; init; }

    public double? SpoolWeight { get; init; }

    public string? Location { get; init; }

    public string? LotNumber { get; init; }

    public double? Price { get; init; }

    public string? Comment { get; init; }
}

/// <summary>
/// Request to bulk-update a set of spools in Spoolman.
/// Only non-null fields are applied to each spool.
/// </summary>
public record SpoolmanBulkUpdateSpoolsRequest
{
    /// <summary>IDs of spools to update (required).</summary>
    public int[] SpoolIds { get; init; } = [];

    /// <summary>Location to set on all selected spools (null = no change).</summary>
    public string? Location { get; init; }

    /// <summary>Lot number to set (null = no change).</summary>
    public string? LotNumber { get; init; }

    /// <summary>Price to set (null = no change).</summary>
    public double? Price { get; init; }

    /// <summary>Comment to set (null = no change).</summary>
    public string? Comment { get; init; }

    /// <summary>Archived status to set (null = no change).</summary>
    public bool? Archived { get; init; }
}

/// <summary>
/// Request to bulk-delete a set of spools from Spoolman.
/// </summary>
public record SpoolmanBulkDeleteSpoolsRequest
{
    /// <summary>IDs of spools to delete (required).</summary>
    public int[] SpoolIds { get; init; } = [];
}
