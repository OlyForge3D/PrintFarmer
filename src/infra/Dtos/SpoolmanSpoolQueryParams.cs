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
/// Query parameters for paginated, filtered, and sorted Spoolman filament listing.
/// Maps to Spoolman's /api/v1/filament query parameters.
/// </summary>
public record SpoolmanFilamentQueryParams
{
    /// <summary>Maximum number of items to return (page size).</summary>
    public int? Limit { get; init; }

    /// <summary>Offset into the full result set for pagination.</summary>
    public int? Offset { get; init; }

    /// <summary>Sort expression in Spoolman format, e.g. "name:asc".</summary>
    public string? Sort { get; init; }

    /// <summary>Partial case-insensitive search applied to filament name.</summary>
    public string? Search { get; init; }

    /// <summary>Partial case-insensitive filter by filament material.</summary>
    public string? Material { get; init; }

    /// <summary>Partial case-insensitive filter by vendor name.</summary>
    public string? Vendor { get; init; }
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

/// <summary>
/// Outcome of a read against one of Spoolman's paginated list endpoints. Distinguishes a
/// successful read -- which may legitimately return zero items when the underlying
/// inventory really is empty -- from a failed read (non-success HTTP status, invalid JSON,
/// or a thrown exception), so callers never mistake "the read failed" for "the source is
/// empty" (see issue #2312).
/// </summary>
public enum SpoolmanReadOutcome
{
    /// <summary>The read completed and <see cref="SpoolmanReadResult{T}.Items"/> reflects the actual page contents.</summary>
    Success,

    /// <summary>The read did not complete; the Spoolman source could not be reached or returned an unusable response.</summary>
    SourceUnavailable,
}

/// <summary>
/// Result of a paginated Spoolman list read, carrying an explicit <see cref="SpoolmanReadOutcome"/>
/// alongside the page contents so a failed read is never confused with a genuinely empty page.
/// <see cref="Items"/> is empty and <see cref="TotalCount"/> is zero whenever
/// <see cref="Outcome"/> is <see cref="SpoolmanReadOutcome.SourceUnavailable"/>.
/// Construct via <see cref="SpoolmanReadResult"/>'s static factory methods.
/// </summary>
/// <typeparam name="T">Type of items returned.</typeparam>
public sealed record SpoolmanReadResult<T>(
    SpoolmanReadOutcome Outcome,
    IReadOnlyList<T> Items,
    int TotalCount)
{
    /// <summary>True when <see cref="Outcome"/> is <see cref="SpoolmanReadOutcome.Success"/>.</summary>
    public bool Success => Outcome == SpoolmanReadOutcome.Success;

    /// <summary>Projects this result onto the wire-facing <see cref="SpoolmanPagedResult{T}"/>, discarding the outcome distinction for API responses that intentionally return an empty page either way.</summary>
    public SpoolmanPagedResult<T> ToPagedResult() => new(Items, TotalCount);
}

/// <summary>
/// Non-generic factory methods for <see cref="SpoolmanReadResult{T}"/>. Kept separate from the
/// generic type so it carries no static members (CA1000: static members on generic types force
/// callers to specify the type argument to reach them).
/// </summary>
public static class SpoolmanReadResult
{
    /// <summary>A successful read with the given page contents.</summary>
    /// <typeparam name="T">Type of items returned.</typeparam>
    public static SpoolmanReadResult<T> Ok<T>(IReadOnlyList<T> items, int totalCount) =>
        new(SpoolmanReadOutcome.Success, items, totalCount);

    /// <summary>A successful read of a genuinely empty page (e.g. Spoolman is not configured, or the inventory is empty).</summary>
    /// <typeparam name="T">Type of items returned.</typeparam>
    public static SpoolmanReadResult<T> Empty<T>() => new(SpoolmanReadOutcome.Success, [], 0);

    /// <summary>A failed read: non-success HTTP status, invalid JSON, or a thrown exception.</summary>
    /// <typeparam name="T">Type of items returned.</typeparam>
    public static SpoolmanReadResult<T> Unavailable<T>() => new(SpoolmanReadOutcome.SourceUnavailable, [], 0);
}
