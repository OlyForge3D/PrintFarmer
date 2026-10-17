using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>Backend-neutral classification of a printer sensor or fan entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterSensorKind>))]
public enum PrinterSensorKind
{
    Other = 0,
    Nozzle,
    Bed,
    Chamber,
    Mcu,
    Host,
    Ambient,
    FilamentRunout,
    Fan,
}

/// <summary>Role of a fan entry (<see cref="PrinterSensorKind.Fan"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterFanRole>))]
public enum PrinterFanRole
{
    Other = 0,
    PartCooling,
    Hotend,
    Controller,
    Chamber,
    Auxiliary,
}

/// <summary>Physical quantity carried by a sensor reading or accepted by a writable descriptor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterSensorQuantity>))]
public enum PrinterSensorQuantity
{
    Other = 0,
    Temperature,
    TargetTemperature,
    Humidity,
    Pressure,
    Gas,
    FanSpeed,
    FanRpm,
    Power,
    FilamentDetected,
}

/// <summary>Unit of a sensor reading or writable descriptor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterSensorUnit>))]
public enum PrinterSensorUnit
{
    None = 0,
    Celsius,
    Percent,
    HectoPascal,
    Ohm,
    Rpm,
    Watt,
}

/// <summary>
/// A sensor reading value: either a finite number or a boolean. Serializes as a bare JSON
/// number or boolean.
/// </summary>
[JsonConverter(typeof(PrinterSensorValueJsonConverter))]
public readonly record struct PrinterSensorValue
{
    private PrinterSensorValue(double? number, bool? boolean)
    {
        Number = number;
        Boolean = boolean;
    }

    /// <summary>The numeric value, when this is a numeric reading.</summary>
    public double? Number { get; }

    /// <summary>The boolean value, when this is a boolean reading.</summary>
    public bool? Boolean { get; }

    /// <summary>Whether this value is numeric.</summary>
    public bool IsNumber => Number.HasValue;

    /// <summary>Whether this value is boolean.</summary>
    public bool IsBoolean => Boolean.HasValue;

    /// <summary>Creates a numeric value.</summary>
    public static PrinterSensorValue FromNumber(double value) => new(value, null);

    /// <summary>Creates a boolean value.</summary>
    public static PrinterSensorValue FromBoolean(bool value) => new(null, value);
}

/// <summary>Reads and writes <see cref="PrinterSensorValue"/> as a JSON number or boolean.</summary>
public sealed class PrinterSensorValueJsonConverter : JsonConverter<PrinterSensorValue>
{
    public override PrinterSensorValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => PrinterSensorValue.FromNumber(reader.GetDouble()),
            JsonTokenType.True => PrinterSensorValue.FromBoolean(true),
            JsonTokenType.False => PrinterSensorValue.FromBoolean(false),
            _ => throw new JsonException("A sensor value must be a JSON number or boolean."),
        };

    public override void Write(Utf8JsonWriter writer, PrinterSensorValue value, JsonSerializerOptions options)
    {
        if (value.Boolean is bool boolean)
        {
            writer.WriteBooleanValue(boolean);
        }
        else if (value.Number is double number && double.IsFinite(number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>One measured quantity of a sensor.</summary>
/// <param name="Quantity">What is measured.</param>
/// <param name="Value">A number or boolean.</param>
/// <param name="Unit">Unit of <paramref name="Value"/>; <see cref="PrinterSensorUnit.None"/> for booleans.</param>
public sealed record PrinterSensorReadingDto(
    PrinterSensorQuantity Quantity,
    PrinterSensorValue Value,
    PrinterSensorUnit Unit);

/// <summary>
/// A settable quantity of a sensor entry and its inclusive numeric bounds. A sensor without
/// a descriptor for a quantity is read-only for that quantity.
/// </summary>
/// <param name="Quantity">The settable quantity, e.g. <see cref="PrinterSensorQuantity.FanSpeed"/>.</param>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Step">Optional increment; values must be <c>Min + n * Step</c>.</param>
/// <param name="Unit">Unit of accepted values.</param>
public sealed record PrinterSensorWritableDto(
    PrinterSensorQuantity Quantity,
    double Min,
    double Max,
    double? Step,
    PrinterSensorUnit Unit);

/// <summary>
/// Backend-neutral sensor or fan entry. Fans are entries with <see cref="Kind"/> =
/// <see cref="PrinterSensorKind.Fan"/> and a <see cref="FanRole"/>.
/// </summary>
/// <param name="Id">Backend key, unique per printer and stable across reconnects (e.g. <c>temperature_sensor chamber</c>).</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Kind">Sensor classification.</param>
public sealed record PrinterSensorDto(
    string Id,
    string DisplayName,
    PrinterSensorKind Kind)
{
    /// <summary>Fan role when <see cref="Kind"/> is <see cref="PrinterSensorKind.Fan"/>; otherwise null.</summary>
    public PrinterFanRole? FanRole { get; init; }

    /// <summary>Current readings, at most one per quantity.</summary>
    public IReadOnlyList<PrinterSensorReadingDto> Readings { get; init; } = [];

    /// <summary>Whether the backend currently reports this sensor. False means stale or disconnected.</summary>
    public bool Available { get; init; } = true;

    /// <summary>UTC time the backend last reported this sensor, when known.</summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>Settable quantities. Empty means read-only.</summary>
    public IReadOnlyList<PrinterSensorWritableDto> Writable { get; init; } = [];

    /// <inheritdoc />
    public bool Equals(PrinterSensorDto? other) =>
        other is not null &&
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
        Kind == other.Kind &&
        FanRole == other.FanRole &&
        Available == other.Available &&
        LastUpdated == other.LastUpdated &&
        Readings.SequenceEqual(other.Readings) &&
        Writable.SequenceEqual(other.Writable);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(Id), Kind, FanRole, Available, Readings.Count);
}

/// <summary>
/// An immutable sensor list already normalized by
/// <see cref="Services.Printers.PrinterSensorNormalizer"/>, compared by content so record
/// equality of status payloads reflects sensor values rather than list references.
/// </summary>
internal sealed class PrinterSensorList : IReadOnlyList<PrinterSensorDto>, IEquatable<PrinterSensorList>
{
    public static readonly PrinterSensorList Empty = new([]);

    private readonly PrinterSensorDto[] _items;

    internal PrinterSensorList(PrinterSensorDto[] items) => _items = items;

    public PrinterSensorDto this[int index] => _items[index];

    public int Count => _items.Length;

    public IEnumerator<PrinterSensorDto> GetEnumerator() => ((IEnumerable<PrinterSensorDto>)_items).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(PrinterSensorList? other) =>
        other is not null && (ReferenceEquals(this, other) || _items.SequenceEqual(other._items));

    public override bool Equals(object? obj) => Equals(obj as PrinterSensorList);

    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (PrinterSensorDto item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A sensor value requested through the targeted sensor-control endpoint.</summary>
/// <param name="Quantity">Quantity to set; must match an advertised writable descriptor.</param>
/// <param name="Value">Requested numeric value.</param>
public sealed record PrinterSensorSetRequest(
    PrinterSensorQuantity? Quantity,
    PrinterSensorValue? Value);
