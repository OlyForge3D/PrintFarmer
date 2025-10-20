using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Parses OrcaSlicer config bundle JSON into structured preview DTOs.
/// OrcaSlicer bundles typically contain:
/// - "printer": array of printer presets
/// - "filament": array of filament presets  
/// - "process": array of process/print settings presets
/// Each preset can inherit from a base preset via "inherits" or "from" keys.
/// </summary>
public sealed class OrcaBundleParsingService : IOrcaBundleParsingService
{
    /// <summary>
    /// Known OrcaSlicer bundle root keys that indicate valid bundle format.
    /// </summary>
    private static readonly HashSet<string> OrcaBundleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "printer", "filament", "process", "machine", "print", "material"
    };

    public bool IsValidOrcaBundle(string bundleJson)
    {
        if (string.IsNullOrWhiteSpace(bundleJson))
        {
            return false;
        }

        try
        {
            JsonNode? root = JsonNode.Parse(bundleJson);
            if (root is not JsonObject obj)
            {
                return false;
            }

            // Check if at least one expected section exists
            foreach (var key in OrcaBundleKeys)
            {
                if (obj.ContainsKey(key))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public OrcaBundlePreviewDto ParseBundle(string bundleJson)
    {
        if (string.IsNullOrWhiteSpace(bundleJson))
        {
            throw new ArgumentException("Bundle JSON is required", nameof(bundleJson));
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bundleJson);
        }
        catch (Exception ex)
        {
            throw new FormatException("Invalid JSON format", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new FormatException("Bundle root must be a JSON object");
        }

        if (!IsValidOrcaBundle(bundleJson))
        {
            throw new FormatException("JSON does not match OrcaSlicer bundle format (missing expected preset sections)");
        }

        var preview = new OrcaBundlePreviewDto();

        // Parse printer presets (can be "printer" or "machine")
        if (obj.TryGetPropertyValue("printer", out var printerNode) && printerNode is JsonArray printerArray)
        {
            preview.Printers = ParsePrinterPresets(printerArray);
        }
        else if (obj.TryGetPropertyValue("machine", out var machineNode) && machineNode is JsonArray machineArray)
        {
            preview.Printers = ParsePrinterPresets(machineArray);
        }

        // Parse filament presets (can be "filament" or "material")
        if (obj.TryGetPropertyValue("filament", out var filamentNode) && filamentNode is JsonArray filamentArray)
        {
            preview.Filaments = ParseFilamentPresets(filamentArray);
        }
        else if (obj.TryGetPropertyValue("material", out var materialNode) && materialNode is JsonArray materialArray)
        {
            preview.Filaments = ParseFilamentPresets(materialArray);
        }

        // Parse process presets (can be "process" or "print")
        if (obj.TryGetPropertyValue("process", out var processNode) && processNode is JsonArray processArray)
        {
            preview.Processes = ParseProcessPresets(processArray);
        }
        else if (obj.TryGetPropertyValue("print", out var printNode) && printNode is JsonArray printArray)
        {
            preview.Processes = ParseProcessPresets(printArray);
        }

        // Extract bundle-level metadata
        preview.Metadata = ExtractBundleMetadata(obj);

        return preview;
    }

    private List<OrcaPrinterPresetDto> ParsePrinterPresets(JsonArray printerArray)
    {
        var printers = new List<OrcaPrinterPresetDto>();

        foreach (var node in printerArray)
        {
            if (node is not JsonObject printerObj)
            {
                continue;
            }

            var printer = new OrcaPrinterPresetDto
            {
                Name = GetStringValue(printerObj, "name") ?? "Unknown Printer",
                InherentFrom = GetStringValue(printerObj, "inherits", "from"),
                PrinterModel = GetStringValue(printerObj, "printer_model", "model", "printer_variant"),
                Manufacturer = GetStringValue(printerObj, "printer_vendor", "vendor", "manufacturer"),
                BedWidth = GetDoubleValue(printerObj, "bed_width", "bed_shape_x", "printable_area_x") ?? 200,
                BedDepth = GetDoubleValue(printerObj, "bed_depth", "bed_shape_y", "printable_area_y") ?? 200,
                MaxZHeight = GetDoubleValue(printerObj, "max_print_height", "printable_height", "max_z") ?? 200,
                NozzleDiameter = GetDoubleValue(printerObj, "nozzle_diameter", "nozzle_size") ?? 0.4,
                MaxBedTemperature = GetIntValue(printerObj, "max_bed_temperature", "bed_temperature") ?? 100,
                MaxHotendTemperature = GetIntValue(printerObj, "max_hotend_temperature", "nozzle_temperature", "temperature") ?? 300,
                PrinterTechnology = GetStringValue(printerObj, "printer_technology", "technology") ?? "FFF"
            };

            // Check for heated bed capability
            // Evaluate heated bed: explicit boolean takes precedence; else infer from max bed temp
            bool? heatedFlag = GetBoolValue(printerObj, "has_heated_bed", "heated_bed");
            printer.HasHeatedBed = heatedFlag ?? (printer.MaxBedTemperature > 0);

            // Store raw parameters for advanced mapping
            printer.RawParameters = ExtractRawParameters(printerObj);

            printers.Add(printer);
        }

        return printers;
    }

    private List<OrcaFilamentPresetDto> ParseFilamentPresets(JsonArray filamentArray)
    {
        var filaments = new List<OrcaFilamentPresetDto>();

        foreach (var node in filamentArray)
        {
            if (node is not JsonObject filamentObj)
            {
                continue;
            }

            var filament = new OrcaFilamentPresetDto
            {
                Name = GetStringValue(filamentObj, "name") ?? "Unknown Filament",
                InherentFrom = GetStringValue(filamentObj, "inherits", "from"),
                FilamentType = GetStringValue(filamentObj, "filament_type", "material_type", "type"),
                NozzleTemperature = GetIntValue(filamentObj, "nozzle_temperature", "temperature", "first_layer_temperature"),
                BedTemperature = GetIntValue(filamentObj, "bed_temperature", "first_layer_bed_temperature"),
                Manufacturer = GetStringValue(filamentObj, "filament_vendor", "vendor", "manufacturer"),
                Density = GetDoubleValue(filamentObj, "filament_density", "density"),
                Cost = GetDoubleValue(filamentObj, "filament_cost", "cost"),
                Color = GetStringValue(filamentObj, "filament_colour", "color", "colour")
            };

            filament.RawParameters = ExtractRawParameters(filamentObj);
            filaments.Add(filament);
        }

        return filaments;
    }

    private List<OrcaProcessPresetDto> ParseProcessPresets(JsonArray processArray)
    {
        var processes = new List<OrcaProcessPresetDto>();

        foreach (var node in processArray)
        {
            if (node is not JsonObject processObj)
            {
                continue;
            }

            var process = new OrcaProcessPresetDto
            {
                Name = GetStringValue(processObj, "name") ?? "Unknown Process",
                InherentFrom = GetStringValue(processObj, "inherits", "from"),
                LayerHeight = GetDoubleValue(processObj, "layer_height") ?? 0.2,
                FirstLayerHeight = GetDoubleValue(processObj, "first_layer_height", "initial_layer_height") ?? 0.2,
                InfillPercentage = GetIntValue(processObj, "fill_density", "infill_density", "infill_percent") ?? 20,
                InfillPattern = GetStringValue(processObj, "fill_pattern", "infill_pattern"),
                PrintSpeed = GetIntValue(processObj, "print_speed", "default_speed"),
                InfillSpeed = GetIntValue(processObj, "infill_speed", "sparse_infill_speed"),
                OuterWallSpeed = GetIntValue(processObj, "outer_wall_speed", "external_perimeter_speed"),
                InnerWallSpeed = GetIntValue(processObj, "inner_wall_speed", "perimeter_speed"),
                EnableSupports = GetBoolValue(processObj, "support_enable", "support_material") ?? false,
                SupportType = GetStringValue(processObj, "support_type", "support_material_pattern"),
                SupportAngle = GetIntValue(processObj, "support_angle", "support_material_threshold"),
                Perimeters = GetIntValue(processObj, "wall_loops", "perimeters") ?? 3,
                TopLayers = GetIntValue(processObj, "top_shell_layers", "top_solid_layers") ?? 4,
                BottomLayers = GetIntValue(processObj, "bottom_shell_layers", "bottom_solid_layers") ?? 4
            };

            // Derive quality from layer height or explicit quality field
            string? explicitQuality = GetStringValue(processObj, "quality", "print_quality");
            if (!string.IsNullOrWhiteSpace(explicitQuality))
            {
                process.Quality = explicitQuality;
            }
            else
            {
                // Heuristic quality classification based on layer height
                process.Quality = process.LayerHeight switch
                {
                    <= 0.12 => "Fine",
                    <= 0.2 => "Standard",
                    _ => "Draft"
                };
            }

            process.RawParameters = ExtractRawParameters(processObj);
            processes.Add(process);
        }

        return processes;
    }

    private Dictionary<string, string> ExtractBundleMetadata(JsonObject obj)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract version, generated date, or other top-level bundle metadata
        if (obj.TryGetPropertyValue("version", out var version) && version is JsonValue versionValue)
        {
            metadata["version"] = versionValue.ToString();
        }

        if (obj.TryGetPropertyValue("generated", out var generated) && generated is JsonValue generatedValue)
        {
            metadata["generated"] = generatedValue.ToString();
        }

        if (obj.TryGetPropertyValue("app_version", out var appVersion) && appVersion is JsonValue appVersionValue)
        {
            metadata["app_version"] = appVersionValue.ToString();
        }

        return metadata;
    }

    private Dictionary<string, object?> ExtractRawParameters(JsonObject obj)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in obj)
        {
            parameters[prop.Key] = prop.Value switch
            {
                JsonValue v when v.TryGetValue<string>(out var str) => str,
                JsonValue v when v.TryGetValue<long>(out var lng) => lng,
                JsonValue v when v.TryGetValue<double>(out var dbl) => dbl,
                JsonValue v when v.TryGetValue<bool>(out var bln) => bln,
                _ => prop.Value?.ToJsonString()
            };
        }

        return parameters;
    }

    // Helper methods to extract values with multiple key aliases
    private string? GetStringValue(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var str))
                {
                    return str;
                }
            }
        }
        return null;
    }

    private double? GetDoubleValue(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<double>(out var dbl))
                {
                    return dbl;
                }
                // Also handle string representations of numbers
                if (value.TryGetValue<string>(out var str) && double.TryParse(str, out dbl))
                {
                    return dbl;
                }
            }
        }
        return null;
    }

    private int? GetIntValue(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intVal))
                {
                    return intVal;
                }
                if (value.TryGetValue<long>(out var lng))
                {
                    return (int)lng;
                }
                // Handle string representations
                if (value.TryGetValue<string>(out var str) && int.TryParse(str, out intVal))
                {
                    return intVal;
                }
            }
        }
        return null;
    }

    private bool? GetBoolValue(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var bln))
                {
                    return bln;
                }
                // Handle string representations ("true", "false", "1", "0")
                if (value.TryGetValue<string>(out var str))
                {
                    if (bool.TryParse(str, out bln))
                    {
                        return bln;
                    }
                    if (str == "1")
                        return true;
                    if (str == "0")
                        return false;
                }
                // Handle numeric representations (1 = true, 0 = false)
                if (value.TryGetValue<int>(out var intVal))
                {
                    return intVal != 0;
                }
            }
        }
        return null;
    }
}
