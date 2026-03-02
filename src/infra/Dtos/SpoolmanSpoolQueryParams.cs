namespace Farm.Infrastructure;

/// <summary>
/// Query parameters for paginated, filtered, and sorted Spoolman spool listing.
/// Maps to Spoolman's /api/v1/spool query parameters.
/// </summary>
public record SpoolmanSpoolQueryParams
{
    /// <summary>Maximum number of items to return (page size).</summary>
    public int? Limit { get; init; }

    /// <summary>Offset into the full result set for pagination.</summary>
    public int? Offset { get; init; }

    /// <summary>Sort expression in Spoolman format, e.g. "filament.name:asc".</summary>
    public string? Sort { get; init; }

    /// <summary>Partial case-insensitive search applied to filament name.</summary>
    public string? Search { get; init; }

    /// <summary>Partial case-insensitive filter by filament material.</summary>
    public string? Material { get; init; }

    /// <summary>Partial case-insensitive filter by vendor name.</summary>
    public string? Vendor { get; init; }

    /// <summary>Partial case-insensitive filter by spool location.</summary>
    public string? Location { get; init; }

    /// <summary>Whether to include archived spools. Defaults to false on the Spoolman side.</summary>
    public bool? AllowArchived { get; init; }
}

/// <summary>
/// Paginated result from Spoolman containing items and total count.
/// </summary>
/// <typeparam name="T">Type of items returned.</typeparam>
/// <param name="Items">Items for the current page.</param>
/// <param name="TotalCount">Total number of matching items across all pages.</param>
public record SpoolmanPagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount);
