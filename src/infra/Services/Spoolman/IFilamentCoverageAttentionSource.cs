using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Narrow seam for the attention feed (#707) to consume filament runout
/// warnings without importing attention-specific types. #707 wraps the returned
/// <see cref="FilamentRunoutWarningDto"/> values into whatever attention entry
/// type it standardizes on when it lands. This keeps issue #709's backend
/// closed against an unmerged attention contract.
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
