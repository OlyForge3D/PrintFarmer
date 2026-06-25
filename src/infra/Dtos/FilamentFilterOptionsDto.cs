namespace Farm.Infrastructure;

/// <summary>
/// Distinct material and vendor values across all filament types.
/// Used by the frontend to populate filter dropdowns without relying on paginated data.
/// </summary>
public record FilamentFilterOptionsDto(
    IReadOnlyList<string> Materials,
    IReadOnlyList<string> Vendors);
