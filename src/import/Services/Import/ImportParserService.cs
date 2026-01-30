using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;

namespace Farm.Importing.Services.Import;

public class ImportParserService : IImportParserService
{
    public async Task<(CreatePrinterFromDiscoveryDto[] Dtos, List<string> Errors)> ParseCsvAsync(Stream stream, CancellationToken ct)
    {
        List<string> errors = new();
        using StreamReader reader = new(stream);
        string content = await reader.ReadToEndAsync(ct);
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            errors.Add("CSV must contain header and at least one row");
            return (Array.Empty<CreatePrinterFromDiscoveryDto>(), errors);
        }

        string[] header = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            headerMap[header[i]] = i;
        }

        var dtos = new List<CreatePrinterFromDiscoveryDto>();
        for (int i = 1; i < lines.Length; i++)
        {
            try
            {
                string[] values = ParseCsvLine(lines[i]);
                CreatePrinterFromDiscoveryDto dto = new();
                string GetCol(string name)
                {
                    if (!headerMap.TryGetValue(name, out var idx))
                    {
                        return string.Empty;
                    }

                    if (idx < 0 || idx >= values.Length)
                    {
                        return string.Empty;
                    }

                    return values[idx].Trim();
                }

                dto.Name = GetCol("Name");

                // IpAddress is the required column for CSV import (matches discovery DTOs)
                string ipAddress = GetCol("IpAddress");
                dto.IpAddress = ipAddress;
                dto.ServerUrl = $"http://{ipAddress}";

                dto.OriginalServerUrl = string.IsNullOrWhiteSpace(GetCol("OriginalServerUrl")) ? null : GetCol("OriginalServerUrl");
                dto.Notes = string.IsNullOrWhiteSpace(GetCol("Notes")) ? null : GetCol("Notes");
                dto.NewManufacturerName = string.IsNullOrWhiteSpace(GetCol("ManufacturerName")) ? null : GetCol("ManufacturerName");
                dto.NewModelName = string.IsNullOrWhiteSpace(GetCol("ModelName")) ? null : GetCol("ModelName");
                var backendVal = GetCol("Backend");
                dto.Backend = Enum.TryParse<PrinterBackend>(backendVal, true, out var b) ? b : PrinterBackend.Moonraker;
                var dateStr = GetCol("DateAcquired");
                dto.DateAcquired = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

                dtos.Add(dto);
            }
            catch (Exception ex)
            {
                errors.Add($"Row {i + 1}: {ex.Message}");
            }
        }

        return (dtos.ToArray(), errors);
    }

    public async Task<(CreatePrinterFromDiscoveryDto[] Dtos, List<string> Errors)> ParseJsonAsync(Stream stream, CancellationToken ct)
    {
        using StreamReader reader = new(stream);
        string json = await reader.ReadToEndAsync(ct);
        try
        {
            var dtos = JsonSerializer.Deserialize<CreatePrinterFromDiscoveryDto[]>(json, ImportJsonOptions.Default) ?? Array.Empty<CreatePrinterFromDiscoveryDto>();
            return (dtos, new List<string>());
        }
        catch (JsonException ex)
        {
            return (Array.Empty<CreatePrinterFromDiscoveryDto>(), new List<string> { ex.Message });
        }
    }

    // Simple CSV parsing helper
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        int index = 0;
        while (index < line.Length)
        {
            char c = line[index];
            if (c == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index += 2;
                    continue;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }

            index++;
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
}
