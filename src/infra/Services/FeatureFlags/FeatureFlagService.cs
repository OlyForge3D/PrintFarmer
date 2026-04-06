using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Services.FeatureFlags;

/// <summary>
/// Service for managing feature flags that control phased rollout of features.
/// </summary>
public interface IFeatureFlagService
{
    /// <summary>
    /// Checks if a specific feature flag is enabled.
    /// </summary>
    /// <param name="featureKey">The feature key to check.</param>
    /// <returns>True if the feature is enabled, false otherwise.</returns>
    bool IsEnabled(string featureKey);

    /// <summary>
    /// Gets all feature flags and their enabled states.
    /// </summary>
    /// <returns>Dictionary of feature keys and their enabled states.</returns>
    Dictionary<string, bool> GetAllFlags();
}

/// <summary>
/// Implementation of feature flag service that reads flags from configuration.
/// </summary>
public class FeatureFlagService : IFeatureFlagService
{
    private readonly IConfiguration _configuration;

    public FeatureFlagService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsEnabled(string featureKey)
    {
        // All flags default to true (features are ON by default since they're
        // behind the slicer capability gate already). Can be overridden via
        // appsettings.json or environment variables (PFARM__FeatureFlags__orca.handcraftedEditors=false)
        return _configuration.GetValue($"FeatureFlags:{featureKey}", true);
    }

    public Dictionary<string, bool> GetAllFlags()
    {
        var flags = new Dictionary<string, bool>();
        var featureFlagsSection = _configuration.GetSection("FeatureFlags");

        // Define all known feature flags
        var knownFlags = new[]
        {
            "orca.handcraftedEditors",
            "orca.schemaEditor",
            "orca.profileComparison",
            "orca.inheritanceDiff",
            "orca.importConflictResolver",
            "orca.expandedDtos"
        };

        foreach (var flag in knownFlags)
        {
            flags[flag] = IsEnabled(flag);
        }

        return flags;
    }
}
