namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Shared normalization, merge and control-validation rules for backend-neutral sensor lists.
/// Every sensor list that enters the status cache passes through <see cref="Normalize"/> so the
/// <c>printerupdated</c> payload stays bounded and deduplicated regardless of backend.
/// </summary>
public static class PrinterSensorNormalizer
{
    /// <summary>Maximum sensor entries per printer.</summary>
    public const int MaxSensors = 64;

    /// <summary>Maximum readings or writable descriptors per sensor entry.</summary>
    public const int MaxEntriesPerSensor = 16;

    /// <summary>Maximum length of a sensor id.</summary>
    public const int MaxIdLength = 128;

    /// <summary>Maximum length of a sensor display name.</summary>
    public const int MaxDisplayNameLength = 128;

    /// <summary>
    /// Returns a bounded, deduplicated copy of <paramref name="sensors"/>: blank or overlong ids
    /// are dropped, the last entry wins per id (ordinal), the list is ordered by id, readings
    /// and writable descriptors are deduplicated per quantity (last wins) with non-finite values
    /// removed, and firmware-automatic fans (hotend/controller) are forced read-only.
    /// </summary>
    public static IReadOnlyList<PrinterSensorDto> Normalize(IEnumerable<PrinterSensorDto?>? sensors)
    {
        if (sensors is null)
        {
            return [];
        }

        Dictionary<string, PrinterSensorDto> byId = new(StringComparer.Ordinal);
        foreach (PrinterSensorDto? sensor in sensors)
        {
            if (sensor is null || string.IsNullOrWhiteSpace(sensor.Id) || sensor.Id.Length > MaxIdLength)
            {
                continue;
            }

            byId[sensor.Id] = NormalizeEntry(sensor);
        }

        return byId.Values
            .OrderBy(sensor => sensor.Id, StringComparer.Ordinal)
            .Take(MaxSensors)
            .ToArray();
    }

    /// <summary>
    /// Applies a partial update: entries in <paramref name="updates"/> replace their matching
    /// ids, but readings the update omits are preserved from <paramref name="existing"/>.
    /// Sensors not mentioned in the update are preserved unchanged.
    /// </summary>
    public static IReadOnlyList<PrinterSensorDto> Merge(
        IReadOnlyList<PrinterSensorDto>? existing,
        IEnumerable<PrinterSensorDto?> updates)
    {
        Dictionary<string, PrinterSensorDto> byId = new(StringComparer.Ordinal);
        foreach (PrinterSensorDto sensor in existing ?? [])
        {
            byId[sensor.Id] = sensor;
        }

        foreach (PrinterSensorDto update in Normalize(updates))
        {
            if (byId.TryGetValue(update.Id, out PrinterSensorDto? previous))
            {
                HashSet<PrinterSensorQuantity> updatedQuantities =
                    update.Readings.Select(reading => reading.Quantity).ToHashSet();
                PrinterSensorReadingDto[] preserved = previous.Readings
                    .Where(reading => !updatedQuantities.Contains(reading.Quantity))
                    .ToArray();
                byId[update.Id] = update with { Readings = [.. preserved, .. update.Readings] };
            }
            else
            {
                byId[update.Id] = update;
            }
        }

        return Normalize(byId.Values);
    }

    /// <summary>Returns the list with every entry marked unavailable; values are kept for display.</summary>
    public static IReadOnlyList<PrinterSensorDto> MarkUnavailable(IReadOnlyList<PrinterSensorDto> sensors) =>
        sensors.All(sensor => !sensor.Available)
            ? sensors
            : sensors.Select(sensor => sensor.Available ? sensor with { Available = false } : sensor).ToArray();

    /// <summary>Whether two sensor lists carry identical content.</summary>
    public static bool ContentEquals(IReadOnlyList<PrinterSensorDto>? left, IReadOnlyList<PrinterSensorDto>? right) =>
        (left ?? []).SequenceEqual(right ?? []);

    /// <summary>
    /// Validates a targeted sensor write against the advertised sensor list. Requests that fail
    /// validation must never be dispatched to a backend client.
    /// </summary>
    public static PrinterSensorControlValidation ValidateControl(
        IReadOnlyList<PrinterSensorDto> advertised,
        string? sensorId,
        PrinterSensorQuantity quantity,
        PrinterSensorValue value)
    {
        PrinterSensorDto? sensor = advertised.FirstOrDefault(
            candidate => string.Equals(candidate.Id, sensorId, StringComparison.Ordinal));
        if (sensor is null)
        {
            return new(PrinterSensorControlValidationCode.UnknownSensor, $"Sensor '{sensorId}' is not reported by this printer.");
        }

        if (!sensor.Available)
        {
            return new(PrinterSensorControlValidationCode.SensorUnavailable, $"Sensor '{sensor.Id}' is not currently available.");
        }

        if (sensor.Writable.Count == 0)
        {
            return new(PrinterSensorControlValidationCode.ReadOnly, $"Sensor '{sensor.Id}' is read-only.");
        }

        PrinterSensorWritableDto? descriptor = sensor.Writable.FirstOrDefault(writable => writable.Quantity == quantity);
        if (descriptor is null)
        {
            return new(PrinterSensorControlValidationCode.UnsupportedQuantity, $"Sensor '{sensor.Id}' does not accept {quantity}.");
        }

        if (value.Number is not double number || !double.IsFinite(number))
        {
            return new(PrinterSensorControlValidationCode.InvalidValue, $"{quantity} requires a finite numeric value.");
        }

        if (number < descriptor.Min || number > descriptor.Max)
        {
            return new(
                PrinterSensorControlValidationCode.OutOfRange,
                $"{quantity} must be between {descriptor.Min} and {descriptor.Max}.");
        }

        if (descriptor.Step is double step && step > 0)
        {
            double steps = (number - descriptor.Min) / step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
            {
                return new(
                    PrinterSensorControlValidationCode.OutOfRange,
                    $"{quantity} must be a multiple of {step} from {descriptor.Min}.");
            }
        }

        return new(PrinterSensorControlValidationCode.Valid, null, sensor.Id, descriptor);
    }

    private static PrinterSensorDto NormalizeEntry(PrinterSensorDto sensor)
    {
        bool firmwareAutomaticFan =
            sensor.Kind == PrinterSensorKind.Fan &&
            sensor.FanRole is PrinterFanRole.Hotend or PrinterFanRole.Controller;
        string displayName = string.IsNullOrWhiteSpace(sensor.DisplayName) ? sensor.Id : sensor.DisplayName.Trim();

        return sensor with
        {
            DisplayName = displayName.Length > MaxDisplayNameLength ? displayName[..MaxDisplayNameLength] : displayName,
            FanRole = sensor.Kind == PrinterSensorKind.Fan ? sensor.FanRole ?? PrinterFanRole.Other : null,
            LastUpdated = sensor.LastUpdated is DateTime lastUpdated
                ? DateTime.SpecifyKind(lastUpdated.ToUniversalTime(), DateTimeKind.Utc)
                : null,
            Readings = LastPerQuantity(
                (sensor.Readings ?? []).Where(reading =>
                    reading is not null &&
                    (reading.Value.IsBoolean || (reading.Value.Number is double number && double.IsFinite(number)))),
                reading => reading.Quantity),
            Writable = firmwareAutomaticFan
                ? []
                : LastPerQuantity(
                    (sensor.Writable ?? []).Where(writable =>
                        writable is not null &&
                        double.IsFinite(writable.Min) &&
                        double.IsFinite(writable.Max) &&
                        writable.Min <= writable.Max &&
                        (writable.Step is null || (writable.Step is double step && double.IsFinite(step) && step > 0))),
                    writable => writable.Quantity),
        };
    }

    private static T[] LastPerQuantity<T>(IEnumerable<T> items, Func<T, PrinterSensorQuantity> quantity)
    {
        Dictionary<PrinterSensorQuantity, T> byQuantity = new();
        List<PrinterSensorQuantity> order = new();
        foreach (T item in items)
        {
            PrinterSensorQuantity key = quantity(item);
            if (!byQuantity.ContainsKey(key))
            {
                order.Add(key);
            }

            byQuantity[key] = item;
        }

        return order.Take(MaxEntriesPerSensor).Select(key => byQuantity[key]).ToArray();
    }
}

/// <summary>Outcome code of <see cref="PrinterSensorNormalizer.ValidateControl"/>.</summary>
public enum PrinterSensorControlValidationCode
{
    Valid,
    UnknownSensor,
    SensorUnavailable,
    ReadOnly,
    UnsupportedQuantity,
    InvalidValue,
    OutOfRange,
}

/// <summary>Result of validating a targeted sensor write.</summary>
public sealed record PrinterSensorControlValidation(
    PrinterSensorControlValidationCode Code,
    string? Detail,
    string? SensorId = null,
    PrinterSensorWritableDto? Descriptor = null)
{
    /// <summary>Whether the request may be dispatched.</summary>
    public bool IsValid => Code == PrinterSensorControlValidationCode.Valid;
}
