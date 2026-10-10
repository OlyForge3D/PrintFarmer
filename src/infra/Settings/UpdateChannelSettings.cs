using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Persisted setting selecting which release channel the host discovers and evaluates
/// production release readiness for (issue #2757). Uses the existing generic settings store
/// (<see cref="ISettingsService"/>/<c>UnifiedSettingsController</c>) — this is a plain
/// <see cref="IAppSetting"/>, not a bespoke settings surface.
/// <para>
/// This setting ONLY controls which channel <see cref="Farm.Infrastructure.Services.HostUpdates.VerifiedReleaseDiscoveryMonitorService"/>
/// discovers/caches verified release evidence for, and which channel
/// <c>SystemInfoService</c>/<c>ServiceInventoryEvaluator</c> select for readiness evaluation. It
/// never stages, downloads, applies, or triggers any update — selecting "insider" here only
/// widens which signed release the discovery/readiness path considers current.
/// </para>
/// </summary>
[AppSetting(UpdateChannelSettings.SectionName)]
[SettingDisplay(Name = "Update Channel", Description = "Selects which signed release channel is discovered and evaluated for readiness. Insider requires explicit acknowledgement.", Icon = "pf-icon-refresh", Group = "System", Order = 7)]
public class UpdateChannelSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "UpdateChannel";

    /// <summary>The only two values this setting accepts. Values are literal, lowercase, and
    /// match the channel strings already produced by <c>ServiceInventoryEvaluator</c> and
    /// <c>SignedUpdateManifestValidator</c> — never PascalCase enum names.</summary>
    public const string StableChannel = "stable";

    /// <summary>See <see cref="StableChannel"/>.</summary>
    public const string InsiderChannel = "insider";

    public static string SectionKey => SectionName;

    /// <summary>
    /// The selected release channel. Must be exactly <c>"stable"</c> or <c>"insider"</c>.
    /// </summary>
    [JsonPropertyName("channel")]
    [SettingDisplay(Name = "Channel", Description = "Release channel to discover and evaluate.", InputType = SettingInputType.Select, AllowedValues = ["stable", "insider"], Order = 1)]
    public string Channel { get; set; } = StableChannel;

    /// <summary>
    /// Must be <c>true</c> before <see cref="Channel"/> may be set to <c>"insider"</c>. This is
    /// the explicit insider-channel acknowledgement required by issue #2757 — insider releases
    /// are pre-release builds from the development branch and are not production-hardened.
    /// </summary>
    [JsonPropertyName("insiderAcknowledged")]
    [SettingDisplay(Name = "Insider channel acknowledged", Description = "Required before selecting the insider channel. Insider releases are pre-release development builds.", InputType = SettingInputType.Boolean, Order = 2)]
    public bool InsiderAcknowledged { get; set; }

    /// <inheritdoc/>
    public void Validate()
    {
        if (Channel != StableChannel && Channel != InsiderChannel)
        {
            throw new ValidationException($"Invalid UpdateChannel value '{Channel}'. Must be '{StableChannel}' or '{InsiderChannel}'.");
        }

        if (Channel == InsiderChannel && !InsiderAcknowledged)
        {
            throw new ValidationException("Selecting the insider update channel requires InsiderAcknowledged to be true.");
        }
    }
}
