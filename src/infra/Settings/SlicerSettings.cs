using System.ComponentModel.DataAnnotations;
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
    [SettingDisplay(Name = "Slicer Mode", Description = "Simple shows only profile selection and basic overrides (supports, adhesion, infill). Advanced exposes the full OrcaSlicer parameter editor.", InputType = SettingInputType.Select, AllowedValues = ["Simple", "Advanced"], Order = 1)]
    public SlicerMode SlicerMode { get; set; } = SlicerMode.Simple;

    public void Validate()
    {
        if (!Enum.IsDefined(typeof(SlicerMode), SlicerMode))
            throw new ValidationException($"Invalid SlicerMode value '{SlicerMode}'. Must be Simple or Advanced.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicerMode
{
    Simple,
    Advanced
}
