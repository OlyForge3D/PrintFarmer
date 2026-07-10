using System.Collections.Generic;

namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Resolves operator feature flags from persisted settings, with a hard-disable environment
/// override for emergency rollback. See <c>docs/OPERATOR_FEATURE_GATES.md</c> and issue #725.
///
/// Resolution order per feature:
/// <list type="number">
///   <item>If the ASP.NET configuration key <c>OperatorFeatures:&lt;flagName&gt;</c> resolves to
///     an explicit <c>false</c>, the feature is disabled regardless of the database value.</item>
///   <item>Otherwise, the runtime database value from <see cref="Farm.Infrastructure.Settings.OperatorFeatureSettings"/>
///     is used.</item>
/// </list>
///
/// Absent or explicitly <c>true</c> environment values do NOT force-enable a feature.
/// </summary>
public interface IOperatorFeatureGate
{
    /// <summary>Returns the effective flags snapshot after applying environment overrides.</summary>
    OperatorFeatureFlagsDto GetEffectiveFlags();

    /// <summary>Returns whether a specific operator feature is enabled for the current request.</summary>
    bool IsEnabled(OperatorFeature feature);

    /// <summary>
    /// Returns whether the given feature is hard-disabled by the environment (an explicit
    /// <c>OperatorFeatures__&lt;flagName&gt;=false</c>). Used by admin/UI surfaces to indicate
    /// that changing the database value alone will not re-enable the feature.
    /// </summary>
    bool IsHardDisabledByEnvironment(OperatorFeature feature);

    /// <summary>
    /// Returns the canonical camelCase flag name (as used on the wire) for a feature.
    /// </summary>
    string GetFlagName(OperatorFeature feature);

    /// <summary>Enumerates all known features with their canonical camelCase flag names.</summary>
    IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures { get; }
}
