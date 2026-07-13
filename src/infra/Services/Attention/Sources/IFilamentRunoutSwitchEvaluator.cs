using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// The evidence tier backing a filament-runout severity decision (issue #711, F6).
/// Ordered from weakest to strongest so callers can compare tiers if needed.
/// </summary>
public enum RunoutSwitchAssessment
{
    /// <summary>
    /// No configured backup with a loaded compatible spool is available for the runout
    /// toolhead. The runout is unmitigated and must stay <c>Critical</c>.
    /// </summary>
    NoBackup = 0,

    /// <summary>
    /// A configured fallback member currently holds a loaded compatible spool, but there is
    /// no live telemetry proving a switch actually happened. Operator awareness is still
    /// required, so severity is downgraded no further than <c>Warning</c>. Configuration
    /// existence alone must never yield a stronger downgrade than this.
    /// </summary>
    BackupAvailable = 1,

    /// <summary>
    /// Live printer telemetry confirms the active spool moved off the runout spool onto a
    /// configured backup of the same material — i.e. the auto-switch is proven, not inferred.
    /// Only this tier permits an informational downgrade.
    /// </summary>
    SwitchConfirmed = 2,
}

/// <summary>
/// Resolves whether a filament-runout warning has a configured backup and/or telemetry-confirmed
/// auto-switch, so <see cref="FilamentRunoutAttentionSource"/> can downgrade severity only when
/// justified. Deliberately separated from the coverage source: coverage predicts the runout,
/// this seam judges the mitigation evidence (issue #711, F6 remediation).
/// </summary>
public interface IFilamentRunoutSwitchEvaluator
{
    /// <summary>
    /// Assesses the mitigation evidence for a single active-runout warning. Implementations must
    /// never return <see cref="RunoutSwitchAssessment.SwitchConfirmed"/> from configuration alone;
    /// that tier requires live telemetry evidence that the switch occurred.
    /// </summary>
    Task<RunoutSwitchAssessment> AssessAsync(FilamentRunoutWarningDto warning, CancellationToken cancellationToken);
}
