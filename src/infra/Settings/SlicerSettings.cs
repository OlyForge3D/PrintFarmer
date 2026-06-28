using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Operations", DisplayName = "Operations", Description = "Operational settings and cost tracking", Icon = "pf-icon-operations", Order = 3)]
[SettingDisplay(Name = "Browser Slicer", Description = "Controls browser-based slicer behaviour.", Icon = "pf-icon-slicer", Group = "Operations", Order = 6)]
public class SlicerSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "SlicerSettings";

    public static string SectionKey => SectionName;

    [JsonPropertyName("slicerMode")]
    [SettingDisplay(Name = "Default Slicer Mode", Description = "The mode users start in. When both modes are enabled, users can switch; otherwise this mode is forced. Must be one of the enabled modes.", InputType = SettingInputType.Select, AllowedValues = ["Simple", "Advanced"], Order = 1)]
    public SlicerMode SlicerMode { get; set; } = SlicerMode.Simple;

    /// <summary>
    /// The set of slicer modes an admin has enabled for the farm. When more than one mode is
    /// enabled, end-users get a per-user toggle on the slice page; when exactly one is enabled,
    /// that mode is forced and no toggle is shown.
    /// <para>
    /// Persisted as a JSON string array (e.g. <c>["Simple","Advanced"]</c>). When absent
    /// (legacy settings written before this field existed), it resolves to <c>[SlicerMode]</c>
    /// via <see cref="EffectiveEnabledModes"/> so existing single-mode farms keep their behaviour.
    /// </para>
    /// </summary>
    [JsonPropertyName("enabledModes")]
    [SettingDisplay(Name = "Enabled Slicer Modes", Description = "Which slicer modes are available to users. Enable both to let users toggle between Simple and Advanced.", InputType = SettingInputType.MultiSelect, AllowedValues = ["Simple", "Advanced"], Order = 2)]
    public IReadOnlyList<SlicerMode>? EnabledModes { get; set; }

    /// <summary>
    /// Resolves the effective set of enabled modes, falling back to <c>[SlicerMode]</c> when
    /// <see cref="EnabledModes"/> is unset or empty (back-compat with legacy settings).
    /// </summary>
    /// <remarks>
    /// Derived state — must NOT be persisted or surfaced as an editable setting. It is
    /// <see cref="JsonIgnoreAttribute">[JsonIgnore]</see>d so it is excluded from settings JSON
    /// serialization and skipped by the metadata reflector (which requires a
    /// <see cref="JsonPropertyNameAttribute"/> on every serialized public property).
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<SlicerMode> EffectiveEnabledModes
        => EnabledModes is { Count: > 0 } modes ? modes : [SlicerMode];

    public void Validate()
    {
        if (!Enum.IsDefined(SlicerMode))
        {
            throw new ValidationException($"Invalid SlicerMode value '{SlicerMode}'. Must be Simple or Advanced.");
        }

        if (EnabledModes is not null)
        {
            if (EnabledModes.Count == 0)
            {
                throw new ValidationException("At least one slicer mode must be enabled.");
            }

            foreach (SlicerMode mode in EnabledModes)
            {
                if (!Enum.IsDefined(mode))
                {
                    throw new ValidationException($"Invalid SlicerMode value '{mode}' in EnabledModes. Must be Simple or Advanced.");
                }
            }

            if (!EnabledModes.Contains(SlicerMode))
            {
                throw new ValidationException("The default SlicerMode must be one of the enabled modes.");
            }
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicerMode
{
    Simple,
    Advanced
}
