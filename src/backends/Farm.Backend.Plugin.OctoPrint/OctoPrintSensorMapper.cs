using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

internal static class OctoPrintSensorMapper
{
    public static IReadOnlyList<PrinterSensorDto> Map(JsonElement temperatures)
    {
        List<PrinterSensorDto> sensors = [];
        if (temperatures.ValueKind != JsonValueKind.Object)
        {
            return sensors;
        }

        foreach (JsonProperty property in temperatures.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            List<PrinterSensorReadingDto> readings = [];
            AddReading(readings, property.Value, "actual", PrinterSensorQuantity.Temperature);
            AddReading(readings, property.Value, "target", PrinterSensorQuantity.TargetTemperature);
            if (readings.Count == 0)
            {
                continue;
            }

            PrinterSensorKind kind = property.Name switch
            {
                "bed" => PrinterSensorKind.Bed,
                "chamber" => PrinterSensorKind.Chamber,
                _ when property.Name.StartsWith("tool", StringComparison.Ordinal) &&
                    int.TryParse(property.Name.AsSpan(4), out int index) && index >= 0 => PrinterSensorKind.Nozzle,
                _ => PrinterSensorKind.Other,
            };
            sensors.Add(new PrinterSensorDto(property.Name, property.Name, kind) { Readings = readings });
        }

        return PrinterSensorNormalizer.Normalize(sensors);
    }

    public static IReadOnlyList<PrinterSensorDto> Map(Dictionary<string, object>? temperatures)
    {
        if (temperatures is null)
        {
            return [];
        }

        // The HTTP client retains each temperature block as raw JSON.
        List<PrinterSensorDto> sensors = [];
        foreach ((string key, object value) in temperatures)
        {
            JsonElement element = value is string json
                ? JsonSerializer.Deserialize<JsonElement>(json)
                : JsonSerializer.SerializeToElement(value);
            Dictionary<string, JsonElement> entry = new() { [key] = element };
            sensors.AddRange(Map(JsonSerializer.SerializeToElement(entry)));
        }

        return PrinterSensorNormalizer.Normalize(sensors);
    }

    public static double? Reading(IReadOnlyList<PrinterSensorDto>? sensors, string id, PrinterSensorQuantity quantity) =>
        sensors?.FirstOrDefault(sensor => sensor.Id == id)?.Readings
            .FirstOrDefault(reading => reading.Quantity == quantity)?.Value.Number;

    private static void AddReading(List<PrinterSensorReadingDto> readings, JsonElement element, string key, PrinterSensorQuantity quantity)
    {
        if (element.TryGetProperty(key, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
        {
            readings.Add(new(quantity, PrinterSensorValue.FromNumber(number), PrinterSensorUnit.Celsius));
        }
    }
}
