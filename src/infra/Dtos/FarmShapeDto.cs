namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Reports the coarse shape of the farm (bare counts only, never identities) so any
/// authenticated caller can gauge scale without requiring admin permissions. See issue #2411.
/// </summary>
public record FarmShapeDto
{
    /// <summary>Gets the total number of user accounts on this farm. Not admin-gated by design.</summary>
    public int AccountCount { get; init; }

    /// <summary>Gets the total number of locations visible to the caller.</summary>
    public int LocationCount { get; init; }

    /// <summary>Gets the number of printers the caller may access.</summary>
    public int PrinterCount { get; init; }
}
