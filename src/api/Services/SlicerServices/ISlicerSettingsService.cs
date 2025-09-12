using Farm.Web.Shared;

namespace Farm.Web.Api.Services.SlicerServices;

public record PerEngineSlicerSetting(string? Path, string? ArgsTemplate);

public record SlicerSettingsDto(bool Enabled, Dictionary<SlicerEngineType, PerEngineSlicerSetting> PerEngine);

public interface ISlicerSettingsService
{
    SlicerSettingsDto GetSettings();
    void SaveSettings(SlicerSettingsDto settings);
}
