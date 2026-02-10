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
    double? Price = null)
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
