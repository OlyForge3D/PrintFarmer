namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Response returned from the slice-cost per-gram endpoint.
/// </summary>
public sealed record SliceCostResponse
{
    /// <summary>Cost per gram in USD, or <c>null</c> when Spoolman is unreachable or unconfigured.</summary>
    public decimal? CostPerGram { get; init; }

    /// <summary>Currency code for the returned cost value.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Which query parameter was used to resolve the cost.
    /// <c>"spool"</c> when resolved via spoolId, <c>"filament"</c> when resolved via filamentId,
    /// <c>null</c> when the provider returned no data.
    /// </summary>
    public string? Source { get; init; }
}
