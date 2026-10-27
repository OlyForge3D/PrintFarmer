using System.Text.Json;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.PrusaLink;

public partial class PrusaLinkApiClient
{
    private static PrinterPrintMetadata? ReadPrintFileMetadata(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        PrinterPrintMetadata metadata = new()
        {
            BedTemperatureC = Number("bed_temperature"),
            BedTemperaturePerToolC = Numbers("bed_temperature per tool"),
            NozzleTemperatureC = Number("temperature"),
            NozzleTemperaturePerToolC = Numbers("temperature per tool"),
            BrimWidthMm = Number("brim_width"),
            EstimatedPrintTimeNormalMode = Text("estimated printing time (normal mode)"),
            EstimatedPrintTimeSeconds = Number("estimated_print_time"),
            FadedLayers = Integer("faded_layers"),
            FilamentCost = Number("filament cost"),
            FilamentCostPerTool = Numbers("filament cost per tool"),
            FilamentUsedCm3 = Number("filament used [cm3]"),
            FilamentUsedPerToolCm3 = Numbers("filament used [cm3] per tool"),
            FilamentUsedG = Number("filament used [g]"),
            FilamentUsedPerToolG = Numbers("filament used [g] per tool"),
            FilamentUsedMm = Number("filament used [mm]"),
            FilamentUsedPerToolMm = Numbers("filament used [mm] per tool"),
            FilamentType = Text("filament_type"),
            FilamentTypePerTool = Texts("filament_type per tool"),
            FillDensity = Text("fill_density"),
            InitialExposureTimeSeconds = Number("initial_exposure_time"),
            LayerHeightMm = Number("layer_height"),
            LayerCount = Integer("total layers count") ?? Integer("layer_count") ?? Integer("total_layer_count"),
            InfillPercentage = Number("fill_density") is >= 0 and <= 100 ? Number("fill_density") : null,
            InfillPattern = Text("fill_pattern") ?? Text("sparse_infill_pattern"),
            SupportEnabled = Boolean("support_material") ?? Boolean("enable_support"),
            SupportType = Text("support_type"),
            SupportStyle = Text("support_style") ?? Text("support_material_style"),
            SlicerName = Text("slicer"),
            SlicerVersion = Text("slicer_version"),
            MaterialName = Text("material_name"),
            ExposureTimeSeconds = Number("exposure_time"),
            MaxExposureTimeSeconds = Number("max_exposure_time"),
            MaxInitialExposureTimeSeconds = Number("max_initial_exposure_time"),
            MinExposureTimeSeconds = Number("min_exposure_time"),
            MinInitialExposureTimeSeconds = Number("min_initial_exposure_time"),
            NozzleDiameterMm = Number("nozzle_diameter"),
            NozzleDiameterPerToolMm = Numbers("nozzle_diameter per tool"),
            NormalPercentPresent = Boolean("normal_percent_present"),
            NormalLeftPresent = Boolean("normal_left_present"),
            QuietPercentPresent = Boolean("quiet_percent_present"),
            QuietLeftPresent = Boolean("quiet_left_present"),
            LayerInfoPresent = Boolean("layer_info_present"),
            MaxLayerZMm = Number("max_layer_z"),
            PrintTimeSeconds = Number("print_time"),
            PrinterModel = Text("printer_model"),
            SupportMaterial = Text("support_material"),
            Ironing = Integer("ironing"),
            RequiredResinMl = Number("required_resin_ml"),
            Profile = Text("profile"),
            Tools = Tools(),
        };
        if (metadata.FilamentUsedG.HasValue)
        {
            metadata = metadata with { FilamentUsedGIsDerived = false };
        }
        else if (metadata.Tools is { Length: > 0 } tools && tools.All(tool => tool.FilamentUsedG.HasValue))
        {
            double total = tools.Sum(tool => tool.FilamentUsedG!.Value);
            if (PrinterPrintMetadata.IsValidNumber(total))
            {
                metadata = metadata with { FilamentUsedG = total, FilamentUsedGIsDerived = true };
            }
        }

        return metadata.HasValues ? metadata : null;

        JsonElement Value(string property) => meta.TryGetProperty(property, out JsonElement value) ? value : default;
        double? Number(string property) => ReadMetadataNumber(Value(property));
        int? Integer(string property) => Value(property) is { ValueKind: JsonValueKind.Number } value &&
            value.TryGetInt32(out int number) && number >= 0 ? number : null;
        string? Text(string property) => ReadMetadataText(Value(property));
        bool? Boolean(string property) => Value(property).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when Number(property) is 0 => false,
            JsonValueKind.Number when Number(property) is 1 => true,
            _ => null,
        };
        PrinterFilamentTool[]? Tools()
        {
            JsonElement weights = Value("filament used [g] per tool");
            JsonElement types = Value("filament_type per tool");
            JsonElement colors = Value("filament_colour per tool");
            if (!IsToolArray(colors))
            {
                colors = Value("filament_color per tool");
            }

            int count = Math.Max(Count(weights), Math.Max(Count(types), Count(colors)));
            return count == 0 ? null : Enumerable.Range(0, count).Select(index =>
            {
                string? color = ReadMetadataText(Item(colors, index));
                return new PrinterFilamentTool
                {
                    Index = index,
                    Material = ReadMetadataText(Item(types, index)),
                    Color = PrinterFilamentTool.IsValidColor(color) ? color : null,
                    FilamentUsedG = ReadMetadataNumber(Item(weights, index)),
                };
            }).ToArray();

            static int Count(JsonElement value) => IsToolArray(value) ? value.GetArrayLength() : 0;
            static JsonElement Item(JsonElement value, int index) => index < Count(value) ? value[index] : default;
        }

        double[]? Numbers(string property)
        {
            JsonElement value = Value(property);
            if (!IsToolArray(value))
            {
                return null;
            }

            List<double> result = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (ReadMetadataNumber(item) is not { } number)
                {
                    return null;
                }

                result.Add(number);
            }

            return result.ToArray();
        }

        string[]? Texts(string property)
        {
            JsonElement value = Value(property);
            if (!IsToolArray(value))
            {
                return null;
            }

            List<string> result = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (ReadMetadataText(item) is not { } text)
                {
                    return null;
                }

                result.Add(text);
            }

            return result.ToArray();
        }
    }

    private static bool IsToolArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= PrinterPrintMetadata.MaximumTools;

    private static double? ReadMetadataNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
        PrinterPrintMetadata.IsValidNumber(number) ? number : null;

    private static string? ReadMetadataText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
        PrinterPrintMetadata.IsValidText(text) ? text : null;
}
