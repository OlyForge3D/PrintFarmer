using System.Text.Json;
using Farm.Infrastructure.Services.Printers;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterSensorNormalizerTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static PrinterSensorReadingDto Reading(PrinterSensorQuantity quantity, double value, PrinterSensorUnit unit = PrinterSensorUnit.Celsius) =>
        new(quantity, PrinterSensorValue.FromNumber(value), unit);

    private static PrinterSensorDto Fan(string id, PrinterFanRole role, params PrinterSensorWritableDto[] writable) =>
        new(id, id, PrinterSensorKind.Fan)
        {
            FanRole = role,
            Readings = [Reading(PrinterSensorQuantity.FanSpeed, 50, PrinterSensorUnit.Percent)],
            Writable = writable,
        };

    private static PrinterSensorWritableDto FanSpeedWritable(double? step = null) =>
        new(PrinterSensorQuantity.FanSpeed, 0, 100, step, PrinterSensorUnit.Percent);

    [Fact]
    public void Normalize_DeduplicatesById_LastWins_AndOrdersById()
    {
        IReadOnlyList<PrinterSensorDto> result = PrinterSensorNormalizer.Normalize(
        [
            new PrinterSensorDto("z", "Z", PrinterSensorKind.Chamber),
            new PrinterSensorDto("a", "First", PrinterSensorKind.Chamber),
            new PrinterSensorDto("a", "Second", PrinterSensorKind.Chamber),
        ]);

        Assert.Equal(["a", "z"], result.Select(sensor => sensor.Id));
        Assert.Equal("Second", result[0].DisplayName);
    }

    [Fact]
    public void Normalize_DropsNullBlankAndOverlongIds_AndCapsCount()
    {
        List<PrinterSensorDto?> input =
        [
            null,
            new PrinterSensorDto(" ", "blank", PrinterSensorKind.Other),
            new PrinterSensorDto(new string('x', PrinterSensorNormalizer.MaxIdLength + 1), "long", PrinterSensorKind.Other),
        ];
        input.AddRange(Enumerable.Range(0, PrinterSensorNormalizer.MaxSensors + 10)
            .Select(index => new PrinterSensorDto($"s{index:D3}", "s", PrinterSensorKind.Other)));

        IReadOnlyList<PrinterSensorDto> result = PrinterSensorNormalizer.Normalize(input);

        Assert.Equal(PrinterSensorNormalizer.MaxSensors, result.Count);
        Assert.All(result, sensor => Assert.StartsWith("s", sensor.Id, StringComparison.Ordinal));
        Assert.Empty(PrinterSensorNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_DeduplicatesReadingsPerQuantity_AndDropsNonFiniteValues()
    {
        PrinterSensorDto sensor = new("chamber", "  ", PrinterSensorKind.Chamber)
        {
            Readings =
            [
                Reading(PrinterSensorQuantity.Temperature, 30),
                Reading(PrinterSensorQuantity.Temperature, 31),
                Reading(PrinterSensorQuantity.Humidity, double.NaN, PrinterSensorUnit.Percent),
                new PrinterSensorReadingDto(PrinterSensorQuantity.FilamentDetected, PrinterSensorValue.FromBoolean(true), PrinterSensorUnit.None),
            ],
            Writable =
            [
                new PrinterSensorWritableDto(PrinterSensorQuantity.TargetTemperature, 0, double.PositiveInfinity, null, PrinterSensorUnit.Celsius),
                new PrinterSensorWritableDto(PrinterSensorQuantity.Power, 10, 5, null, PrinterSensorUnit.Watt),
            ],
        };

        PrinterSensorDto result = Assert.Single(PrinterSensorNormalizer.Normalize([sensor]));

        Assert.Equal("chamber", result.DisplayName);
        Assert.Null(result.FanRole);
        Assert.Equal(2, result.Readings.Count);
        Assert.Equal(31, result.Readings.Single(r => r.Quantity == PrinterSensorQuantity.Temperature).Value.Number);
        Assert.True(result.Readings.Single(r => r.Quantity == PrinterSensorQuantity.FilamentDetected).Value.Boolean);
        Assert.Empty(result.Writable);
    }

    [Theory]
    [InlineData(PrinterFanRole.Hotend, true)]
    [InlineData(PrinterFanRole.Controller, true)]
    [InlineData(PrinterFanRole.PartCooling, false)]
    [InlineData(PrinterFanRole.Auxiliary, false)]
    public void Normalize_FirmwareAutomaticFans_AreForcedReadOnly(PrinterFanRole role, bool readOnly)
    {
        PrinterSensorDto result = Assert.Single(PrinterSensorNormalizer.Normalize([Fan("fan", role, FanSpeedWritable())]));

        Assert.Equal(readOnly, result.Writable.Count == 0);
    }

    [Fact]
    public void Normalize_FanWithoutRole_DefaultsToOther()
    {
        PrinterSensorDto input = new("fan", "Fan", PrinterSensorKind.Fan);

        Assert.Equal(PrinterFanRole.Other, Assert.Single(PrinterSensorNormalizer.Normalize([input])).FanRole);
    }

    [Fact]
    public void Merge_PreservesOmittedReadingsAndUnmentionedSensors()
    {
        IReadOnlyList<PrinterSensorDto> existing = PrinterSensorNormalizer.Normalize(
        [
            new PrinterSensorDto("chamber", "Chamber", PrinterSensorKind.Chamber)
            {
                Readings =
                [
                    Reading(PrinterSensorQuantity.Temperature, 30),
                    Reading(PrinterSensorQuantity.Humidity, 40, PrinterSensorUnit.Percent),
                ],
            },
            new PrinterSensorDto("mcu", "MCU", PrinterSensorKind.Mcu) { Readings = [Reading(PrinterSensorQuantity.Temperature, 45)] },
        ]);

        IReadOnlyList<PrinterSensorDto> merged = PrinterSensorNormalizer.Merge(
            existing,
            [new PrinterSensorDto("chamber", "Chamber", PrinterSensorKind.Chamber) { Readings = [Reading(PrinterSensorQuantity.Temperature, 33)] }]);

        PrinterSensorDto chamber = merged.Single(sensor => sensor.Id == "chamber");
        Assert.Equal(33, chamber.Readings.Single(r => r.Quantity == PrinterSensorQuantity.Temperature).Value.Number);
        Assert.Equal(40, chamber.Readings.Single(r => r.Quantity == PrinterSensorQuantity.Humidity).Value.Number);
        Assert.Equal(45, merged.Single(sensor => sensor.Id == "mcu").Readings[0].Value.Number);
    }

    [Fact]
    public void MarkUnavailable_KeepsValues()
    {
        IReadOnlyList<PrinterSensorDto> sensors = PrinterSensorNormalizer.Normalize([Fan("fan", PrinterFanRole.PartCooling)]);

        PrinterSensorDto result = Assert.Single(PrinterSensorNormalizer.MarkUnavailable(sensors));

        Assert.False(result.Available);
        Assert.Equal(50, result.Readings[0].Value.Number);
    }

    [Theory]
    [InlineData("missing", PrinterSensorQuantity.FanSpeed, 50, PrinterSensorControlValidationCode.UnknownSensor)]
    [InlineData("FAN", PrinterSensorQuantity.FanSpeed, 50, PrinterSensorControlValidationCode.UnknownSensor)]
    [InlineData("offline", PrinterSensorQuantity.FanSpeed, 50, PrinterSensorControlValidationCode.SensorUnavailable)]
    [InlineData("hotend", PrinterSensorQuantity.FanSpeed, 50, PrinterSensorControlValidationCode.ReadOnly)]
    [InlineData("fan", PrinterSensorQuantity.TargetTemperature, 50, PrinterSensorControlValidationCode.UnsupportedQuantity)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, double.NaN, PrinterSensorControlValidationCode.InvalidValue)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, -1, PrinterSensorControlValidationCode.OutOfRange)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 101, PrinterSensorControlValidationCode.OutOfRange)]
    [InlineData("stepped", PrinterSensorQuantity.FanSpeed, 33, PrinterSensorControlValidationCode.OutOfRange)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 0, PrinterSensorControlValidationCode.Valid)]
    [InlineData("fan", PrinterSensorQuantity.FanSpeed, 100, PrinterSensorControlValidationCode.Valid)]
    [InlineData("stepped", PrinterSensorQuantity.FanSpeed, 30, PrinterSensorControlValidationCode.Valid)]
    public void ValidateControl_EnforcesAdvertisedDescriptors(
        string sensorId,
        PrinterSensorQuantity quantity,
        double value,
        PrinterSensorControlValidationCode expected)
    {
        IReadOnlyList<PrinterSensorDto> advertised = AdvertisedSensors();

        PrinterSensorControlValidation result = PrinterSensorNormalizer.ValidateControl(
            advertised, sensorId, quantity, PrinterSensorValue.FromNumber(value));

        Assert.Equal(expected, result.Code);
        Assert.Equal(expected == PrinterSensorControlValidationCode.Valid, result.IsValid);
    }

    [Fact]
    public void ValidateControl_BooleanValue_IsInvalid()
    {
        PrinterSensorControlValidation result = PrinterSensorNormalizer.ValidateControl(
            AdvertisedSensors(), "fan", PrinterSensorQuantity.FanSpeed, PrinterSensorValue.FromBoolean(true));

        Assert.Equal(PrinterSensorControlValidationCode.InvalidValue, result.Code);
    }

    [Fact]
    public void Serialization_UsesCamelCaseStringEnumsAndBareValues()
    {
        PrinterSensorDto sensor = Assert.Single(PrinterSensorNormalizer.Normalize([Fan("fan", PrinterFanRole.PartCooling, FanSpeedWritable(5))]));
        PrinterSensorDto runout = new("filament_switch_sensor runout", "Runout", PrinterSensorKind.FilamentRunout)
        {
            Readings = [new PrinterSensorReadingDto(PrinterSensorQuantity.FilamentDetected, PrinterSensorValue.FromBoolean(false), PrinterSensorUnit.None)],
        };

        using JsonDocument fanJson = JsonDocument.Parse(JsonSerializer.Serialize(sensor, WebJson));
        using JsonDocument runoutJson = JsonDocument.Parse(JsonSerializer.Serialize(runout, WebJson));
        JsonElement root = fanJson.RootElement;

        Assert.Equal("Fan", root.GetProperty("kind").GetString());
        Assert.Equal("PartCooling", root.GetProperty("fanRole").GetString());
        Assert.True(root.GetProperty("available").GetBoolean());
        JsonElement reading = root.GetProperty("readings")[0];
        Assert.Equal("FanSpeed", reading.GetProperty("quantity").GetString());
        Assert.Equal(JsonValueKind.Number, reading.GetProperty("value").ValueKind);
        Assert.Equal("Percent", reading.GetProperty("unit").GetString());
        JsonElement writable = root.GetProperty("writable")[0];
        Assert.Equal(0, writable.GetProperty("min").GetDouble());
        Assert.Equal(100, writable.GetProperty("max").GetDouble());
        Assert.Equal(5, writable.GetProperty("step").GetDouble());
        Assert.Equal(JsonValueKind.False, runoutJson.RootElement.GetProperty("readings")[0].GetProperty("value").ValueKind);

        PrinterSensorDto roundTrip = JsonSerializer.Deserialize<PrinterSensorDto>(JsonSerializer.Serialize(sensor, WebJson), WebJson)!;
        Assert.Equal(sensor, roundTrip);
    }

    [Fact]
    public void Serialization_StatusDto_DefaultsToEmptySensors()
    {
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(new PrinterStatusDto(Guid.NewGuid(), true, "idle"), WebJson));

        Assert.Equal(0, json.RootElement.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public void SetRequest_DeserializesStringQuantityAndNumericValue()
    {
        PrinterSensorSetRequest request = JsonSerializer.Deserialize<PrinterSensorSetRequest>(
            """{"quantity":"FanSpeed","value":40}""", WebJson)!;

        Assert.Equal(PrinterSensorQuantity.FanSpeed, request.Quantity);
        Assert.Equal(40, request.Value?.Number);
    }

    private static IReadOnlyList<PrinterSensorDto> AdvertisedSensors() => PrinterSensorNormalizer.Normalize(
    [
        Fan("fan", PrinterFanRole.PartCooling, FanSpeedWritable()),
        Fan("stepped", PrinterFanRole.Auxiliary, FanSpeedWritable(10)),
        Fan("hotend", PrinterFanRole.Hotend, FanSpeedWritable()),
        Fan("offline", PrinterFanRole.Chamber, FanSpeedWritable()) with { Available = false },
    ]);
}
