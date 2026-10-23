using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>Configured limits of one Klipper config section read from <c>configfile.settings</c>.</summary>
internal sealed record MoonrakerSectionSettings(double? MinTemp, double? MaxTemp, string? SensorType, bool InvalidBounds = false);

/// <summary>
/// Maps Klipper sensor and fan objects to backend-neutral <see cref="PrinterSensorDto"/> entries
/// (issue #3407) and builds the G-code for the small set of supported writes. Sensor ids are the
/// Klipper object names, so they stay stable across reconnects.
/// </summary>
internal static class MoonrakerSensorMapper
{
    internal const string PartFan = "fan";
    internal const string TemperatureSensor = "temperature_sensor";
    internal const string TemperatureFan = "temperature_fan";
    internal const string HeaterGeneric = "heater_generic";
    internal const string HeaterBed = "heater_bed";
    internal const string HeaterFan = "heater_fan";
    internal const string ControllerFan = "controller_fan";
    internal const string FanGeneric = "fan_generic";
    internal const string FilamentSwitchSensor = "filament_switch_sensor";
    internal const string FilamentMotionSensor = "filament_motion_sensor";

    private const int MaxObjectNameLength = 64;

    private static readonly string[] GenericFields = ["temperature", "humidity", "pressure", "gas"];

    // Objects that never describe a sensor even if a future Klipper adds a numeric field.
    private static readonly HashSet<string> NonSensorTypes = new(StringComparer.Ordinal)
    {
        "configfile", "webhooks", "print_stats", "display_status", "virtual_sdcard", "toolhead",
        "gcode_move", "motion_report", "heaters", "idle_timeout", "system_stats", "pause_resume",
        "exclude_object", "bed_mesh", "query_endstops", "save_variables", "mcu", "menu",
    };

    /// <summary>Splits a Klipper object name into its type and (possibly empty) instance name.</summary>
    internal static (string Type, string Name) Split(string objectName)
    {
        int space = objectName.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (objectName, string.Empty) : (objectName[..space], objectName[(space + 1)..]);
    }

    /// <summary>Whether <paramref name="type"/> is an <c>extruder</c>, <c>extruder1</c>, … object.</summary>
    internal static bool IsExtruder(string type) =>
        type.StartsWith("extruder", StringComparison.Ordinal) &&
        type.Length >= "extruder".Length &&
        type["extruder".Length..].All(char.IsAsciiDigit);

    /// <summary>Whether an object of this type is mapped explicitly (not by the generic field rule).</summary>
    internal static bool IsKnownType(string type) =>
        IsExtruder(type) || IsVendorFan(type) || type is PartFan or TemperatureSensor or TemperatureFan or HeaterGeneric or HeaterBed or
            HeaterFan or ControllerFan or FanGeneric or FilamentSwitchSensor or FilamentMotionSensor;

    private static bool IsVendorFan(string type) => type.EndsWith("_fan", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="objectName"/> is a well-formed Klipper object name that may be
    /// interpolated into G-code: an exact <c>type</c> or <c>type name</c> made of ASCII letters,
    /// digits, <c>_</c> and <c>-</c>. Anything else (spaces, <c>=</c>, newlines, …) is rejected.
    /// </summary>
    internal static bool IsSafeObjectName(string? objectName)
    {
        if (string.IsNullOrEmpty(objectName) || objectName.Length > MaxObjectNameLength)
        {
            return false;
        }

        (string type, string name) = Split(objectName);
        return IsSafeToken(type) && (name.Length == 0 || IsSafeToken(name));
    }

    private static bool IsSafeToken(string token) =>
        token.Length > 0 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    /// <summary>Whether a generic (unknown-type) status object exposes an environmental reading.</summary>
    internal static bool IsGenericCandidate(string type, JsonElement status) =>
        !NonSensorTypes.Contains(type) &&
        status.ValueKind == JsonValueKind.Object &&
        GenericFields.Any(field => status.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number);

    /// <summary>Reads the sections of <c>configfile.settings</c> relevant to sensors and writable bounds.</summary>
    internal static Dictionary<string, MoonrakerSectionSettings> ReadSettings(JsonElement settings)
    {
        Dictionary<string, MoonrakerSectionSettings> result = new(StringComparer.OrdinalIgnoreCase);
        if (settings.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty section in settings.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string type = Split(section.Name).Type;
            if (!IsExtruder(type) && type is not (HeaterBed or TemperatureSensor or TemperatureFan or HeaterGeneric))
            {
                continue;
            }

            string? sensorTypeName =
                section.Value.TryGetProperty("sensor_type", out JsonElement sensorType) && sensorType.ValueKind == JsonValueKind.String
                    ? sensorType.GetString()
                    : null;
            result[section.Name] = new MoonrakerSectionSettings(
                ReadTargetBound(section.Value, "min_temp", "target_min_temp", Math.Max),
                ReadTargetBound(section.Value, "max_temp", "target_max_temp", Math.Min),
                sensorTypeName,
                new[] { "min_temp", "max_temp", "target_min_temp", "target_max_temp" }.Any(field =>
                    section.Value.TryGetProperty(field, out _) && ReadFiniteNumber(section.Value, field) is null));
        }

        return result;
    }

    // Some Klipper forks distinguish allowable targets from the sensor's shutdown limits.
    private static double? ReadTargetBound(JsonElement section, string limitName, string targetName, Func<double, double, double> intersect)
    {
        double? limit = ReadFiniteNumber(section, limitName);
        if (!section.TryGetProperty(targetName, out _))
        {
            return limit;
        }

        return limit is double bound && ReadFiniteNumber(section, targetName) is double target
            ? intersect(bound, target)
            : null;
    }

    private static double? ReadFiniteNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) &&
        double.IsFinite(number)
            ? number
            : null;

    /// <summary>
    /// Advertises manual fans, filament enablement and heaters with configured finite bounds.
    /// Automatic heater/controller fans remain read-only.
    /// </summary>
    internal static IReadOnlyList<PrinterSensorWritableDto> GetWritable(
        string objectName,
        IReadOnlyDictionary<string, MoonrakerSectionSettings> settings)
    {
        if (!IsSafeObjectName(objectName))
        {
            return [];
        }

        (string type, string name) = Split(objectName);
        bool primaryHeater = name.Length == 0 && (IsExtruder(type) || type == HeaterBed);
        bool namedHeater = name.Length > 0 && type is TemperatureFan or HeaterGeneric;
        if (primaryHeater || namedHeater)
        {
            return settings.TryGetValue(objectName, out MoonrakerSectionSettings? section) &&
                !section.InvalidBounds &&
                section.MinTemp is double min && section.MaxTemp is double max &&
                double.IsFinite(min) && double.IsFinite(max) && Math.Max(0, min) <= max
                    ? [new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, Math.Max(0, min), max, null, PrinterSensorUnit.Celsius)
                        { AllowZero = true }]
                    : [];
        }

        switch (type)
        {
            case PartFan when name.Length == 0:
                return [new PrinterSensorWritableDto(PrinterSensorQuantity.FanSpeed, 0, 100, 1, PrinterSensorUnit.Percent)];
            case FanGeneric when name.Length > 0:
                return [new PrinterSensorWritableDto(PrinterSensorQuantity.FanSpeed, 0, 100, 1, PrinterSensorUnit.Percent)];
            case FilamentSwitchSensor or FilamentMotionSensor when name.Length > 0:
                return [new PrinterSensorWritableDto(PrinterSensorQuantity.Enabled, 0, 1, 1, PrinterSensorUnit.None)];
            default:
                return [];
        }
    }

    /// <summary>
    /// Builds the Klipper G-code for a targeted write, or returns null when the object is not in
    /// the discovered set, its name is unsafe, the type/quantity is read-only, or the value is out
    /// of the advertised bounds. No G-code may be sent when this returns null.
    /// </summary>
    internal static string? TryBuildCommand(
        string? sensorId,
        PrinterSensorQuantity quantity,
        double value,
        IReadOnlySet<string> discoveredObjects,
        IReadOnlyDictionary<string, MoonrakerSectionSettings> settings)
    {
        if (sensorId is null || !discoveredObjects.Contains(sensorId) || !IsSafeObjectName(sensorId) || !double.IsFinite(value))
        {
            return null;
        }

        PrinterSensorWritableDto? descriptor = GetWritable(sensorId, settings).FirstOrDefault(writable => writable.Quantity == quantity);
        bool off = value.Equals(0d) && descriptor?.AllowZero == true && quantity == PrinterSensorQuantity.TargetTemperature;
        if (descriptor is null || (!off && (value < descriptor.Min || value > descriptor.Max)))
        {
            return null;
        }

        if (!off && descriptor.Step is double step && Math.Abs(((value - descriptor.Min) / step) - Math.Round((value - descriptor.Min) / step)) > 1e-6)
        {
            return null;
        }

        (string type, string name) = Split(sensorId);
        return type switch
        {
            PartFan => BuildPartFanCommand((int)Math.Round(value, MidpointRounding.AwayFromZero)),
            FanGeneric => string.Create(CultureInfo.InvariantCulture, $"SET_FAN_SPEED FAN={name} SPEED={value / 100d:0.###}"),
            TemperatureFan => string.Create(CultureInfo.InvariantCulture, $"SET_TEMPERATURE_FAN_TARGET temperature_fan={name} target={value:0.##}"),
            HeaterGeneric => string.Create(CultureInfo.InvariantCulture, $"SET_HEATER_TEMPERATURE HEATER={name} TARGET={value:0.##}"),
            HeaterBed => string.Create(CultureInfo.InvariantCulture, $"SET_HEATER_TEMPERATURE HEATER={type} TARGET={value:0.##}"),
            _ when IsExtruder(type) => string.Create(CultureInfo.InvariantCulture, $"SET_HEATER_TEMPERATURE HEATER={type} TARGET={value:0.##}"),
            FilamentSwitchSensor or FilamentMotionSensor => $"SET_FILAMENT_SENSOR SENSOR={name} ENABLE={(value is 1 ? 1 : 0)}",
            _ => null,
        };
    }

    /// <summary>Part-cooling fan command: percent is converted to Klipper's 0-255 PWM scale.</summary>
    internal static string BuildPartFanCommand(int speedPercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(speedPercent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(speedPercent, 100);
        int gcodeSpeed = (int)Math.Round(speedPercent * 255d / 100d, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"M106 S{gcodeSpeed}");
    }

    internal static (PrinterSensorKind Kind, PrinterFanRole? Role) Classify(string objectName, MoonrakerSectionSettings? settings)
    {
        (string type, string name) = Split(objectName);
        if (IsExtruder(type))
        {
            return (PrinterSensorKind.Nozzle, null);
        }

        return type switch
        {
            HeaterBed => (PrinterSensorKind.Bed, null),
            PartFan => (PrinterSensorKind.Fan, PrinterFanRole.PartCooling),
            HeaterFan => (PrinterSensorKind.Fan, PrinterFanRole.Hotend),
            ControllerFan => (PrinterSensorKind.Fan, PrinterFanRole.Controller),
            FanGeneric or TemperatureFan => (PrinterSensorKind.Fan, HintFanRole(name)),
            _ when IsVendorFan(type) => (PrinterSensorKind.Fan, HintFanRole(name)),
            FilamentSwitchSensor or FilamentMotionSensor => (PrinterSensorKind.FilamentRunout, null),
            TemperatureSensor => (SourceKind(settings?.SensorType) ?? HintKind(name), null),
            HeaterGeneric => (HintKind(name), null),
            _ => (HintKind(name), null),
        };
    }

    private static PrinterSensorKind? SourceKind(string? sensorType) => sensorType?.Trim().ToLowerInvariant() switch
    {
        "temperature_mcu" => PrinterSensorKind.Mcu,
        "temperature_host" => PrinterSensorKind.Host,
        _ => null,
    };

    private static PrinterSensorKind HintKind(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("chamber", StringComparison.Ordinal) ||
            lower.Contains("enclosure", StringComparison.Ordinal) ||
            lower.Contains("exhaust", StringComparison.Ordinal))
        {
            return PrinterSensorKind.Chamber;
        }

        return lower.Contains("ambient", StringComparison.Ordinal) ? PrinterSensorKind.Ambient : PrinterSensorKind.Other;
    }

    private static PrinterFanRole HintFanRole(string name) =>
        HintKind(name) == PrinterSensorKind.Chamber ? PrinterFanRole.Chamber : PrinterFanRole.Auxiliary;

    internal static string DisplayName(string objectName)
    {
        (string type, string name) = Split(objectName);
        if (name.Length > 0)
        {
            return name;
        }

        if (IsExtruder(type))
        {
            string index = type["extruder".Length..];
            return index.Length == 0 ? "Extruder" : $"Extruder {index}";
        }

        return type switch
        {
            HeaterBed => "Bed",
            PartFan => "Part cooling fan",
            _ => type,
        };
    }

    /// <summary>Fields read for an object type, each mapped to its quantity, unit and scale.</summary>
    internal static IReadOnlyList<FieldMapping> FieldsFor(string type)
    {
        if (IsExtruder(type))
        {
            return HeaterFields;
        }

        return type switch
        {
            HeaterBed or HeaterGeneric => HeaterFields,
            TemperatureSensor => EnvironmentalFields,
            TemperatureFan => TemperatureFanFields,
            PartFan or HeaterFan or ControllerFan or FanGeneric => FanFields,
            _ when IsVendorFan(type) => FanFields,
            FilamentSwitchSensor or FilamentMotionSensor => FilamentFields,
            _ => GenericFieldMappings,
        };
    }

    internal sealed record FieldMapping(string Field, PrinterSensorQuantity Quantity, PrinterSensorUnit Unit, double Scale = 1);

    private static readonly FieldMapping Temperature = new("temperature", PrinterSensorQuantity.Temperature, PrinterSensorUnit.Celsius);
    private static readonly FieldMapping Target = new("target", PrinterSensorQuantity.TargetTemperature, PrinterSensorUnit.Celsius);
    private static readonly FieldMapping Power = new("power", PrinterSensorQuantity.Power, PrinterSensorUnit.Percent, 100);
    private static readonly FieldMapping Speed = new("speed", PrinterSensorQuantity.FanSpeed, PrinterSensorUnit.Percent, 100);
    private static readonly FieldMapping Rpm = new("rpm", PrinterSensorQuantity.FanRpm, PrinterSensorUnit.Rpm);
    private static readonly FieldMapping Humidity = new("humidity", PrinterSensorQuantity.Humidity, PrinterSensorUnit.Percent);
    private static readonly FieldMapping Pressure = new("pressure", PrinterSensorQuantity.Pressure, PrinterSensorUnit.HectoPascal);
    private static readonly FieldMapping Gas = new("gas", PrinterSensorQuantity.Gas, PrinterSensorUnit.Ohm);
    private static readonly FieldMapping MeasuredMin = new("measured_min_temp", PrinterSensorQuantity.MeasuredMinTemperature, PrinterSensorUnit.Celsius);
    private static readonly FieldMapping MeasuredMax = new("measured_max_temp", PrinterSensorQuantity.MeasuredMaxTemperature, PrinterSensorUnit.Celsius);

    private static readonly FieldMapping[] HeaterFields = [Temperature, Target, Power];
    private static readonly FieldMapping[] EnvironmentalFields = [Temperature, Humidity, Pressure, Gas, MeasuredMin, MeasuredMax];
    private static readonly FieldMapping[] TemperatureFanFields = [Temperature, Target, Speed, Rpm];
    private static readonly FieldMapping[] FanFields = [Speed, Rpm];
    private static readonly FieldMapping[] GenericFieldMappings = [Temperature, Humidity, Pressure, Gas];

    private static readonly FieldMapping[] FilamentFields =
    [
        new("filament_detected", PrinterSensorQuantity.FilamentDetected, PrinterSensorUnit.None),
        new("enabled", PrinterSensorQuantity.Enabled, PrinterSensorUnit.None),
    ];
}

/// <summary>
/// Per-printer Klipper sensor state. Discovery (<c>printer.objects.list</c>) defines the current
/// object set, <c>notify_status_update</c> payloads are merged field by field so partial updates
/// keep unrelated readings, and <see cref="Snapshot"/> produces the complete sensor list for each
/// <c>printerupdated</c> broadcast. Thread-safe.
/// </summary>
internal sealed class MoonrakerSensorTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Dictionary<string, MoonrakerSectionSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _discovered;
    private string? _activeExtruderId;
    private bool _activeExtruderReported;

    private sealed class Entry(string objectName)
    {
        public string ObjectName { get; } = objectName;

        public Dictionary<string, PrinterSensorValue> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, DateTime> ObservedAt { get; } = new(StringComparer.Ordinal);

        public bool Available { get; set; } = true;

        public DateTime? LastUpdated { get; set; }
    }

    /// <summary>The object names from the latest discovery, or null before the first discovery.</summary>
    public IReadOnlySet<string>? DiscoveredObjects
    {
        get
        {
            lock (_gate)
            {
                return _discovered is null ? null : new HashSet<string>(_discovered, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Records a fresh object enumeration. Every known entry becomes unavailable until the
    /// subscription snapshot reports it again; entries for objects Klipper no longer lists stay
    /// unavailable with their last values.
    /// </summary>
    public void Discover(IEnumerable<string> objectNames)
    {
        lock (_gate)
        {
            _discovered = new HashSet<string>(objectNames, StringComparer.Ordinal);
            _activeExtruderId = null;
            _activeExtruderReported = false;
            foreach (Entry entry in _entries.Values)
            {
                entry.Available = false;
            }
        }
    }

    /// <summary>Merges one Moonraker status payload (full or partial) into the tracked sensors.</summary>
    /// <returns>True when any sensor-relevant object was present.</returns>
    public bool Apply(JsonElement status, DateTime nowUtc)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        lock (_gate)
        {
            bool changed = false;
            if (status.TryGetProperty("toolhead", out JsonElement toolhead) &&
                toolhead.ValueKind == JsonValueKind.Object &&
                toolhead.TryGetProperty("extruder", out JsonElement active))
            {
                _activeExtruderReported = true;
                string? id = active.ValueKind == JsonValueKind.String ? active.GetString() : null;
                _activeExtruderId = id is not null && MoonrakerSensorMapper.IsExtruder(id) &&
                    MoonrakerSensorMapper.IsSafeObjectName(id) && (_discovered is null || _discovered.Contains(id))
                        ? id
                        : null;
            }

            if (status.TryGetProperty("configfile", out JsonElement configfile) &&
                configfile.ValueKind == JsonValueKind.Object &&
                configfile.TryGetProperty("settings", out JsonElement settings))
            {
                _settings = MoonrakerSensorMapper.ReadSettings(settings);
                changed = true;
            }

            foreach (JsonProperty property in status.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    (_discovered is not null && !_discovered.Contains(property.Name)))
                {
                    continue;
                }

                string type = MoonrakerSensorMapper.Split(property.Name).Type;
                if (!_entries.TryGetValue(property.Name, out Entry? entry))
                {
                    if (!MoonrakerSensorMapper.IsKnownType(type) && !MoonrakerSensorMapper.IsGenericCandidate(type, property.Value))
                    {
                        continue;
                    }

                    entry = new Entry(property.Name);
                    _entries[property.Name] = entry;
                }

                foreach (MoonrakerSensorMapper.FieldMapping field in MoonrakerSensorMapper.FieldsFor(type))
                {
                    if (!property.Value.TryGetProperty(field.Field, out JsonElement value))
                    {
                        continue;
                    }

                    switch (value.ValueKind)
                    {
                        case JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number):
                            entry.Values[field.Field] = PrinterSensorValue.FromNumber(number * field.Scale);
                            entry.ObservedAt[field.Field] = nowUtc;
                            break;
                        case JsonValueKind.True or JsonValueKind.False:
                            entry.Values[field.Field] = PrinterSensorValue.FromBoolean(value.GetBoolean());
                            entry.ObservedAt[field.Field] = nowUtc;
                            break;
                        default:
                            _ = entry.Values.Remove(field.Field);
                            _ = entry.ObservedAt.Remove(field.Field);
                            break;
                    }
                }

                entry.Available = true;
                entry.LastUpdated = nowUtc;
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>Marks every tracked sensor unavailable (Klippy or transport disconnected).</summary>
    /// <returns>True when at least one sensor changed.</returns>
    public bool MarkAllUnavailable()
    {
        lock (_gate)
        {
            bool changed = false;
            foreach (Entry entry in _entries.Values.Where(entry => entry.Available))
            {
                entry.Available = false;
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>Whether any sensor is tracked.</summary>
    public bool HasSensors
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    /// <summary>Active physical heater, with a single-extruder fallback only when unambiguous.</summary>
    public string? ActiveExtruderId
    {
        get
        {
            lock (_gate)
            {
                if (_activeExtruderReported)
                {
                    return _activeExtruderId;
                }

                string[] extruders = (_discovered ?? _entries.Keys.ToHashSet(StringComparer.Ordinal))
                    .Where(MoonrakerSensorMapper.IsExtruder).ToArray();
                return extruders.Length == 1 ? extruders[0] : null;
            }
        }
    }

    /// <summary>Reads an available scalar and its own observation time, not the last unrelated object update.</summary>
    public (double? Value, DateTime? ObservedAtUtc) ReadScalar(string? objectName, string field)
    {
        lock (_gate)
        {
            return objectName is not null && _entries.TryGetValue(objectName, out Entry? entry) && entry.Available &&
                entry.Values.TryGetValue(field, out PrinterSensorValue value) && value.Number is double number &&
                entry.ObservedAt.TryGetValue(field, out DateTime observedAt)
                    ? (number, observedAt)
                    : (null, null);
        }
    }

    /// <summary>Builds a legacy primary-heater write without guessing a tool or ignoring known limits.</summary>
    public string? BuildPrimaryHeaterCommand(string? objectName, double target)
    {
        lock (_gate)
        {
            if (objectName is null || _discovered?.Contains(objectName) != true ||
                !MoonrakerSensorMapper.IsSafeObjectName(objectName) ||
                (!MoonrakerSensorMapper.IsExtruder(objectName) && objectName != MoonrakerSensorMapper.HeaterBed) ||
                !double.IsFinite(target) || target < 0)
            {
                return null;
            }

            string? bounded = MoonrakerSensorMapper.TryBuildCommand(
                objectName, PrinterSensorQuantity.TargetTemperature, target, _discovered, _settings);
            if (bounded is not null)
            {
                return bounded;
            }

            // Preserve legacy integer entry when limits are absent, never when supplied limits are invalid.
            if (_settings.TryGetValue(objectName, out MoonrakerSectionSettings? section))
            {
                bool invalidBounds = section.InvalidBounds ||
                    (section.MinTemp is double min && section.MaxTemp is double max && min > max);
                bool belowMinimum = !target.Equals(0d) && section.MinTemp is double lower && target < lower;
                bool aboveMaximum = section.MaxTemp is double upper && target > upper;
                bool completeBounds = section.MinTemp.HasValue && section.MaxTemp.HasValue;
                if (invalidBounds || belowMinimum || aboveMaximum || completeBounds)
                {
                    return null;
                }
            }

            return target.Equals(Math.Truncate(target))
                ? string.Create(CultureInfo.InvariantCulture, $"SET_HEATER_TEMPERATURE HEATER={objectName} TARGET={target:0}")
                : null;
        }
    }

    /// <summary>The complete current sensor list.</summary>
    public IReadOnlyList<PrinterSensorDto> Snapshot()
    {
        lock (_gate)
        {
            Dictionary<string, PrinterSensorDto> sensors = _entries.Values.ToDictionary(entry => entry.ObjectName, BuildDto, StringComparer.Ordinal);
            foreach (Entry driver in _entries.Values.Where(entry => !MoonrakerSensorMapper.IsKnownType(MoonrakerSensorMapper.Split(entry.ObjectName).Type)))
            {
                (string type, string name) = MoonrakerSensorMapper.Split(driver.ObjectName);
                string[] owners = _settings.Where(pair =>
                    string.Equals(pair.Value.SensorType, type, StringComparison.OrdinalIgnoreCase) &&
                    (MoonrakerSensorMapper.Split(pair.Key).Name == name || pair.Key == name) &&
                    sensors.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray();
                if (owners.Length != 1)
                {
                    continue;
                }

                PrinterSensorDto owner = sensors[owners[0]];
                HashSet<PrinterSensorQuantity> quantities = owner.Readings.Select(reading => reading.Quantity).ToHashSet();
                sensors[owners[0]] = owner with
                {
                    Readings = [.. owner.Readings, .. sensors[driver.ObjectName].Readings.Where(reading =>
                        driver.Available && owner.Available && !quantities.Contains(reading.Quantity))],
                };
                _ = sensors.Remove(driver.ObjectName);
            }

            return sensors.Values.ToArray();
        }
    }

    private PrinterSensorDto BuildDto(Entry entry)
    {
        string type = MoonrakerSensorMapper.Split(entry.ObjectName).Type;
        _ = _settings.TryGetValue(entry.ObjectName, out MoonrakerSectionSettings? section);
        (PrinterSensorKind kind, PrinterFanRole? role) = MoonrakerSensorMapper.Classify(entry.ObjectName, section);

        List<PrinterSensorReadingDto> readings = [];
        foreach (MoonrakerSensorMapper.FieldMapping field in MoonrakerSensorMapper.FieldsFor(type).Where(f => entry.Values.ContainsKey(f.Field)))
        {
            PrinterSensorValue value = entry.Values[field.Field];
            readings.Add(new PrinterSensorReadingDto(field.Quantity, value, value.IsBoolean ? PrinterSensorUnit.None : field.Unit));
        }

        return new PrinterSensorDto(entry.ObjectName, MoonrakerSensorMapper.DisplayName(entry.ObjectName), kind)
        {
            FanRole = role,
            Readings = readings,
            Available = entry.Available,
            LastUpdated = entry.LastUpdated,
            Writable = MoonrakerSensorMapper.GetWritable(entry.ObjectName, _settings),
        };
    }
}
