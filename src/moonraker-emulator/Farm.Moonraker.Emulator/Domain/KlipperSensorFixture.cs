using System.Globalization;
using System.Text.Json;

namespace Farm.Moonraker.Emulator.Domain;

/// <summary>
/// Standard Klipper telemetry fixtures, owned and synchronized by PrinterAggregate.
/// Readings are explicitly driven by tests, not a wall-clock thermal simulation.
/// </summary>
internal sealed class KlipperSensorFixture
{
    private Dictionary<string, Dictionary<string, object?>> objects = CreateObjects();

    public void Reset() => objects = CreateObjects();

    public void SetMultipleExtruders(bool enabled)
    {
        for (int index = 1; index < 4; index++)
        {
            string id = $"extruder{index}";
            if (enabled)
            {
                objects.TryAdd(id, new Dictionary<string, object?>
                {
                    ["temperature"] = 23.4 + index,
                    ["target"] = 0.0,
                    ["power"] = 0.0,
                });
            }
            else
            {
                objects.Remove(id);
            }
        }
    }

    public void TurnOffHeaters()
    {
        foreach ((string name, Dictionary<string, object?> fields) in objects)
        {
            if (name.StartsWith("extruder", StringComparison.Ordinal) || name.StartsWith("heater_generic ", StringComparison.Ordinal))
            {
                fields["target"] = 0.0;
                fields["power"] = 0.0;
            }
        }
    }

    public void AddTo(Dictionary<string, object> snapshot)
    {
        foreach ((string name, Dictionary<string, object?> fields) in objects)
        {
            snapshot[name] = new Dictionary<string, object?>(fields, StringComparer.Ordinal);
        }

        string[] extruders = ["extruder", .. objects.Keys.Where(name => name.StartsWith("extruder", StringComparison.Ordinal))];
        snapshot["heaters"] = new Dictionary<string, object?>
        {
            ["available_heaters"] = new[] { "heater_bed", "heater_generic chamber" }.Concat(extruders).ToArray(),
            ["available_sensors"] = new[] { "heater_bed", "heater_generic chamber", "temperature_sensor endstop", "temperature_fan chamber_heater" }.Concat(extruders).ToArray(),
            ["available_monitors"] = Array.Empty<string>(),
        };
        Dictionary<string, object?> settings = new()
        {
            ["extruder"] = Settings("Generic 3950", 0, 300),
            ["heater_bed"] = Settings("Generic 3950", 0, 120),
            ["temperature_sensor endstop"] = Settings("Generic 3950", -255, 256),
            ["temperature_fan chamber_heater"] = Settings("ATC Semitec 104GT-2", -255, 256),
            ["heater_generic chamber"] = Settings("Generic 3950", 0, 100),
        };
        foreach (string extruder in extruders.Skip(1))
        {
            settings[extruder] = Settings("Generic 3950", 10, ExtruderMaximum(extruder));
        }

        snapshot["configfile"] = new Dictionary<string, object?>
        {
            ["settings"] = settings,
        };
    }

    public void Update(string objectName, IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!objects.TryGetValue(objectName, out Dictionary<string, object?>? current) || fields.Count == 0)
        {
            throw new GcodeParameterException("Expected a configured Klipper sensor object and at least one field.");
        }

        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string field, JsonElement value) in fields)
        {
            if (!current.TryGetValue(field, out object? currentValue) || field is "measured_min_temp" or "measured_max_temp")
            {
                throw new GcodeParameterException($"Field '{field}' is not an injectable reading of '{objectName}'.");
            }

            if (field is "enabled" or "filament_detected" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                changes[field] = value.GetBoolean();
            }
            else if (field == "rpm" && currentValue is null && value.ValueKind == JsonValueKind.Null)
            {
                changes[field] = null;
            }
            else if (field is not ("enabled" or "filament_detected") &&
                (field != "rpm" || currentValue is not null) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
                double.IsFinite(number) && IsInRange(objectName, field, number))
            {
                changes[field] = number;
            }
            else
            {
                throw new GcodeParameterException($"Invalid value for '{objectName}.{field}'.");
            }
        }

        foreach ((string field, object? value) in changes)
        {
            current[field] = value;
        }

        // Klipper ignores exactly zero for extrema; near-zero readings still count.
        if (objectName == "temperature_sensor endstop" && changes.TryGetValue("temperature", out object? reading) &&
            reading is double temperature && temperature != 0)
        {
            current["measured_min_temp"] = Math.Min((double)current["measured_min_temp"]!, temperature);
            current["measured_max_temp"] = Math.Max((double)current["measured_max_temp"]!, temperature);
        }
    }

    public bool TrySendGcode(string line)
    {
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        (string NameKey, string Prefix, string Field, string ValueKey)? command = tokens[0].ToUpperInvariant() switch
        {
            "SET_FAN_SPEED" => ("FAN", "fan_generic ", "speed", "SPEED"),
            "SET_HEATER_TEMPERATURE" => ("HEATER", "heater_generic ", "target", "TARGET"),
            "SET_TEMPERATURE_FAN_TARGET" => ("TEMPERATURE_FAN", "temperature_fan ", "target", "TARGET"),
            "SET_FILAMENT_SENSOR" => ("SENSOR", string.Empty, "enabled", "ENABLE"),
            _ => null,
        };
        if (command is not { } spec)
        {
            return false;
        }

        Dictionary<string, string> parameters = ParseParameters(line);
        if (!parameters.TryGetValue(spec.NameKey, out string? name) ||
            !parameters.TryGetValue(spec.ValueKey, out string? rawValue) || parameters.Count != 2 ||
            !double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            throw new GcodeParameterException("Expected a configured sensor name and finite command value.");
        }

        string objectName = spec.Prefix + name;
        if (tokens[0].Equals("SET_HEATER_TEMPERATURE", StringComparison.OrdinalIgnoreCase) &&
            objects.ContainsKey(name) && name.StartsWith("extruder", StringComparison.Ordinal))
        {
            objectName = name;
        }

        if (tokens[0].Equals("SET_FILAMENT_SENSOR", StringComparison.OrdinalIgnoreCase))
        {
            objectName = new[] { "filament_switch_sensor ", "filament_motion_sensor " }
                .Select(prefix => prefix + name).FirstOrDefault(objects.ContainsKey) ?? string.Empty;
        }

        if (spec.Field == "speed" && value > 1)
        {
            value = 1;
        }

        bool validObject = objects.TryGetValue(objectName, out Dictionary<string, object?>? status) &&
            status.ContainsKey(spec.Field) &&
            (spec.Field != "target" || objectName.StartsWith("temperature_fan ", StringComparison.Ordinal) ||
                objectName.StartsWith("heater_generic ", StringComparison.Ordinal) ||
                objectName.StartsWith("extruder", StringComparison.Ordinal));
        if (!validObject || !IsInRange(objectName, spec.Field, value) ||
            (spec.Field == "enabled" && value is not (0 or 1)))
        {
            throw new GcodeParameterException("Unknown sensor or command value outside configured limits.");
        }

        status![spec.Field] = spec.Field == "enabled" ? value is 1 : value;
        return true;
    }

    public static Dictionary<string, string> ParseParameters(string line)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(token => token.Split('=', 2)))
        {
            if (pair.Length != 2 || !parameters.TryAdd(pair[0], pair[1]))
            {
                throw new GcodeParameterException("Expected unique NAME=VALUE parameters.");
            }
        }

        return parameters;
    }

    private static double ExtruderMaximum(string name) => name switch
    {
        "extruder1" => 290,
        "extruder2" => 280,
        "extruder3" => 270,
        _ => 300,
    };

    private static bool IsInRange(string name, string field, double value) => field switch
    {
        "speed" or "power" => value is >= 0 and <= 1,
        "rpm" => value >= 0,
        "target" when name.StartsWith("extruder", StringComparison.Ordinal) => value.Equals(0d) || (value >= 10 && value <= ExtruderMaximum(name)),
        "temperature" when name.StartsWith("extruder", StringComparison.Ordinal) => value >= 10 && value <= ExtruderMaximum(name),
        "target" => name == "heater_generic chamber" ? value is >= 0 and <= 100 : value is >= -255 and <= 256,
        "temperature" => name == "heater_generic chamber" ? value is >= 0 and <= 100 : value is >= -255 and <= 256,
        "enabled" => value is 0 or 1,
        _ => false,
    };

    private static Dictionary<string, object?> Settings(string sensorType, double min, double max) => new()
    {
        ["sensor_type"] = sensorType,
        ["min_temp"] = min,
        ["max_temp"] = max,
    };

    private static Dictionary<string, Dictionary<string, object?>> CreateObjects() => new(StringComparer.Ordinal)
    {
        ["temperature_sensor endstop"] = new()
        {
            ["temperature"] = 23.4,
            ["measured_min_temp"] = 23.4,
            ["measured_max_temp"] = 23.4,
        },
        ["temperature_fan chamber_heater"] = new()
        {
            ["temperature"] = 24.0,
            ["target"] = 0.0,
            ["speed"] = 0.0,
            ["rpm"] = 0.0,
        },
        ["heater_generic chamber"] = new()
        {
            ["temperature"] = 24.0,
            ["target"] = 0.0,
            ["power"] = 0.0,
        },
        ["heater_fan hotend"] = new() { ["speed"] = 0.0, ["rpm"] = null },
        ["controller_fan electronics"] = new() { ["speed"] = 0.0, ["rpm"] = null },
        ["fan_generic auxiliary"] = new() { ["speed"] = 0.0, ["rpm"] = null },
        ["filament_switch_sensor runout"] = new() { ["enabled"] = true, ["filament_detected"] = true },
        ["filament_motion_sensor encoder"] = new() { ["enabled"] = true, ["filament_detected"] = true },
    };
}
