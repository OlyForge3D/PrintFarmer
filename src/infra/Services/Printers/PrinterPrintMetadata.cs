using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Optional native print metadata. Units are explicit; cost has no assumed currency.
/// Per-tool arrays retain provider ordering and are never compacted when a value is invalid.
/// </summary>
public sealed record PrinterPrintMetadata
{
    public const int MaximumTextLength = 512;
    public const int MaximumTools = 16;

    /// <summary>Column bound shared by every database provider.</summary>
    public const int MaximumStoredJsonLength = 32768;

    /// <summary>Storage-only encoding avoids unnecessary escaping; FitsStorage still enforces the column bound.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions StorageJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>True when the stored JSON form fits the database column on every provider.</summary>
    public bool FitsStorage() =>
        System.Text.Json.JsonSerializer.Serialize(this, StorageJsonOptions).Length <= MaximumStoredJsonLength;

    /// <summary>Bed temperature in degrees Celsius.</summary>
    public double? BedTemperatureC { get; init; }

    /// <summary>Bed temperatures in degrees Celsius, in provider tool order.</summary>
    public double[]? BedTemperaturePerToolC { get; init; }

    /// <summary>Nozzle temperature in degrees Celsius.</summary>
    public double? NozzleTemperatureC { get; init; }

    /// <summary>Nozzle temperatures in degrees Celsius, in provider tool order.</summary>
    public double[]? NozzleTemperaturePerToolC { get; init; }

    /// <summary>Brim width in millimeters, including zero for no brim.</summary>
    public double? BrimWidthMm { get; init; }

    /// <summary>Provider's normal-mode duration text, without guessing its format.</summary>
    public string? EstimatedPrintTimeNormalMode { get; init; }

    /// <summary>Provider's estimated print duration in seconds.</summary>
    public double? EstimatedPrintTimeSeconds { get; init; }

    /// <summary>Number of faded layers for resin prints.</summary>
    public int? FadedLayers { get; init; }

    /// <summary>Filament cost, with currency unspecified by the provider.</summary>
    public double? FilamentCost { get; init; }

    /// <summary>Filament costs in provider tool order, with currency unspecified.</summary>
    public double[]? FilamentCostPerTool { get; init; }

    /// <summary>Filament volume in cubic centimeters.</summary>
    public double? FilamentUsedCm3 { get; init; }

    /// <summary>Filament volumes in cubic centimeters, in provider tool order.</summary>
    public double[]? FilamentUsedPerToolCm3 { get; init; }

    /// <summary>Filament mass in grams.</summary>
    public double? FilamentUsedG { get; init; }

    /// <summary>Filament masses in grams, in provider tool order.</summary>
    public double[]? FilamentUsedPerToolG { get; init; }

    /// <summary>Filament length in millimeters.</summary>
    public double? FilamentUsedMm { get; init; }

    /// <summary>Filament lengths in millimeters, in provider tool order.</summary>
    public double[]? FilamentUsedPerToolMm { get; init; }

    /// <summary>Filament type supplied by the provider.</summary>
    public string? FilamentType { get; init; }

    /// <summary>Filament types in provider tool order.</summary>
    public string[]? FilamentTypePerTool { get; init; }

    /// <summary>Provider's percentage text for infill density.</summary>
    public string? FillDensity { get; init; }

    /// <summary>Initial exposure time in seconds.</summary>
    public double? InitialExposureTimeSeconds { get; init; }

    /// <summary>Layer height in millimeters.</summary>
    public double? LayerHeightMm { get; init; }

    /// <summary>Provider's material name.</summary>
    public string? MaterialName { get; init; }

    /// <summary>Exposure time in seconds.</summary>
    public double? ExposureTimeSeconds { get; init; }

    /// <summary>Maximum exposure time in seconds.</summary>
    public double? MaxExposureTimeSeconds { get; init; }

    /// <summary>Maximum initial exposure time in seconds.</summary>
    public double? MaxInitialExposureTimeSeconds { get; init; }

    /// <summary>Minimum exposure time in seconds.</summary>
    public double? MinExposureTimeSeconds { get; init; }

    /// <summary>Minimum initial exposure time in seconds.</summary>
    public double? MinInitialExposureTimeSeconds { get; init; }

    /// <summary>Nozzle diameter in millimeters.</summary>
    public double? NozzleDiameterMm { get; init; }

    /// <summary>Nozzle diameters in millimeters, in provider tool order.</summary>
    public double[]? NozzleDiameterPerToolMm { get; init; }

    /// <summary>Whether normal-mode percentage reporting is present.</summary>
    public bool? NormalPercentPresent { get; init; }

    /// <summary>Whether normal-mode remaining-time reporting is present.</summary>
    public bool? NormalLeftPresent { get; init; }

    /// <summary>Whether quiet-mode percentage reporting is present.</summary>
    public bool? QuietPercentPresent { get; init; }

    /// <summary>Whether quiet-mode remaining-time reporting is present.</summary>
    public bool? QuietLeftPresent { get; init; }

    /// <summary>Whether layer information is present.</summary>
    public bool? LayerInfoPresent { get; init; }

    /// <summary>Maximum layer Z height in millimeters.</summary>
    public double? MaxLayerZMm { get; init; }

    /// <summary>Provider's print duration in seconds.</summary>
    public double? PrintTimeSeconds { get; init; }

    /// <summary>Provider's printer model, without a firmware-version-specific allowlist.</summary>
    public string? PrinterModel { get; init; }

    /// <summary>Provider's support material text.</summary>
    public string? SupportMaterial { get; init; }

    /// <summary>Provider's nonnegative ironing setting.</summary>
    public int? Ironing { get; init; }

    /// <summary>Required resin volume in milliliters.</summary>
    public double? RequiredResinMl { get; init; }

    /// <summary>Provider's profile name.</summary>
    public string? Profile { get; init; }

    /// <summary>False only when every optional field is absent; zero and false are data.</summary>
    [JsonIgnore]
    public bool HasValues => this != new PrinterPrintMetadata();

    /// <summary>Checks bounded, inert metadata before writing or serving private cached content.</summary>
    public bool IsValid()
    {
        double?[] numbers = [BedTemperatureC, NozzleTemperatureC, BrimWidthMm, EstimatedPrintTimeSeconds,
            FadedLayers, FilamentCost, FilamentUsedCm3, FilamentUsedG, FilamentUsedMm, InitialExposureTimeSeconds,
            LayerHeightMm, ExposureTimeSeconds, MaxExposureTimeSeconds, MaxInitialExposureTimeSeconds,
            MinExposureTimeSeconds, MinInitialExposureTimeSeconds, NozzleDiameterMm, MaxLayerZMm,
            PrintTimeSeconds, Ironing, RequiredResinMl];
        string?[] texts = [EstimatedPrintTimeNormalMode, FilamentType, FillDensity, MaterialName,
            PrinterModel, SupportMaterial, Profile];
        double[]?[] tools = [BedTemperaturePerToolC, NozzleTemperaturePerToolC, FilamentCostPerTool,
            FilamentUsedPerToolCm3, FilamentUsedPerToolG, FilamentUsedPerToolMm, NozzleDiameterPerToolMm];
        return numbers.All(value => value is null || IsValidNumber(value.Value)) &&
            texts.All(value => value is null || IsValidText(value)) &&
            tools.All(values => values is null || (values.Length is > 0 and <= MaximumTools &&
                values.All(IsValidNumber))) &&
            (FilamentTypePerTool is null || (FilamentTypePerTool.Length is > 0 and <= MaximumTools &&
                FilamentTypePerTool.All(IsValidText)));
    }

    /// <summary>Native numeric measurements may be zero but must be finite and nonnegative.</summary>
    public static bool IsValidNumber(double value) => double.IsFinite(value) && value >= 0;

    /// <summary>Native text is display-only, bounded, and must not contain control characters.</summary>
    public static bool IsValidText(string? value) =>
        value is { Length: > 0 and <= MaximumTextLength } && !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}
