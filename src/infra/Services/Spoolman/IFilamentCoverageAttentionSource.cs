using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Narrow seam used by the unified attention feed adapter to consume filament
/// runout warnings without coupling coverage computation to attention DTOs.
/// </summary>
public interface IFilamentCoverageAttentionSource
{
    /// <summary>
    /// Returns runout warnings that satisfy the configured warning lead time
    /// or represent hard "insufficient for the assigned queue" states.
    /// Never emits warnings for slots whose coverage is <see cref="FilamentCoverageStatus.Unknown"/>.
    /// </summary>
    Task<IReadOnlyList<FilamentRunoutWarningDto>> GetRunoutWarningsAsync(CancellationToken ct);
}
