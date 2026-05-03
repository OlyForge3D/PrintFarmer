namespace Farm.Infrastructure;

/// <summary>
/// Distinct material, vendor, and location values across all spools.
/// Used by the frontend to populate filter dropdowns without relying on paginated data.
/// </summary>
public record SpoolFilterOptionsDto(
    IReadOnlyList<string> Materials,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Locations);
