using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.OperatorFeatures;

/// <summary>
/// Default <see cref="IOperatorFeatureGate"/> implementation.
///
/// Reads the runtime <see cref="OperatorFeatureSettings"/> instance from the scoped
/// <see cref="ISettingsService"/> and applies the environment hard-disable override from
/// <see cref="IConfiguration"/>. Registered scoped so it observes DB updates on the next
/// request without needing to invalidate a singleton cache.
/// </summary>
public sealed class OperatorFeatureGate : IOperatorFeatureGate
{
    private readonly ISettingsService _settings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatorFeatureGate> _logger;

    private sealed record FeatureDescriptor(
        OperatorFeature Feature,
        string FlagName,
        Func<OperatorFeatureSettings, bool> Read,
        bool Default);

    private static readonly IReadOnlyList<FeatureDescriptor> Descriptors =
    [
        new(OperatorFeature.Attention, "attentionEnabled", s => s.AttentionEnabled, true),
        new(OperatorFeature.NativePush, "nativePushEnabled", s => s.NativePushEnabled, false),
        new(OperatorFeature.FilamentCoverage, "filamentCoverageEnabled", s => s.FilamentCoverageEnabled, true),
        new(OperatorFeature.GuidedSwap, "guidedSwapEnabled", s => s.GuidedSwapEnabled, true),
        new(OperatorFeature.MultiSlotFallback, "multiSlotFallbackEnabled", s => s.MultiSlotFallbackEnabled, true),
        new(OperatorFeature.ShiftPlan, "shiftPlanEnabled", s => s.ShiftPlanEnabled, true),
        new(OperatorFeature.PrintedPartsInventory, "printedPartsInventoryEnabled", s => s.PrintedPartsInventoryEnabled, true),
        new(OperatorFeature.OfflineWriteReplay, "offlineWriteReplayEnabled", s => s.OfflineWriteReplayEnabled, true),
    ];

    private static readonly IReadOnlyList<(OperatorFeature Feature, string FlagName)> FeatureNames =
        Descriptors.Select(d => (d.Feature, d.FlagName)).ToArray();

    public OperatorFeatureGate(
        ISettingsService settings,
        IConfiguration configuration,
        ILogger<OperatorFeatureGate> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures => FeatureNames;

    public string GetFlagName(OperatorFeature feature) => FindDescriptor(feature).FlagName;

    public bool IsEnabled(OperatorFeature feature)
    {
        FeatureDescriptor descriptor = FindDescriptor(feature);
        return Resolve(descriptor, LoadSettings());
    }

    public bool IsHardDisabledByEnvironment(OperatorFeature feature)
        => IsEnvironmentHardDisable(FindDescriptor(feature).FlagName);

    public OperatorFeatureFlagsDto GetEffectiveFlags()
    {
        OperatorFeatureSettings s = LoadSettings();
        return new OperatorFeatureFlagsDto
        {
            AttentionEnabled = Resolve(Descriptors[0], s),
            NativePushEnabled = Resolve(Descriptors[1], s),
            FilamentCoverageEnabled = Resolve(Descriptors[2], s),
            GuidedSwapEnabled = Resolve(Descriptors[3], s),
            MultiSlotFallbackEnabled = Resolve(Descriptors[4], s),
            ShiftPlanEnabled = Resolve(Descriptors[5], s),
            PrintedPartsInventoryEnabled = Resolve(Descriptors[6], s),
            OfflineWriteReplayEnabled = Resolve(Descriptors[7], s),
        };
    }

    private OperatorFeatureSettings LoadSettings()
    {
        try
        {
            return _settings.Get<OperatorFeatureSettings>();
        }
        catch (InvalidOperationException ex)
        {
            // SettingsService can throw on cold start / mis-registration. Fall back to the
            // section defaults rather than 500-ing every operator endpoint.
            _logger.LogWarning(ex, "OperatorFeatureSettings unavailable, using defaults");
            return new OperatorFeatureSettings();
        }
    }

    private bool Resolve(FeatureDescriptor descriptor, OperatorFeatureSettings settings)
    {
        if (IsEnvironmentHardDisable(descriptor.FlagName))
        {
            return false;
        }

        return descriptor.Read(settings);
    }

    private bool IsEnvironmentHardDisable(string flagName)
    {
        // Environment/config values named OperatorFeatures__<flagName>=false hard-disable the
        // feature regardless of the database value. Any other value (missing, "true", junk)
        // falls through to the database value — explicit-false is the only override.
        string? raw = _configuration[$"{OperatorFeatureSettings.SectionName}:{flagName}"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out bool parsed) && !parsed;
    }

    private static FeatureDescriptor FindDescriptor(OperatorFeature feature)
    {
        foreach (FeatureDescriptor d in Descriptors)
        {
            if (d.Feature == feature)
            {
                return d;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown operator feature.");
    }
}
