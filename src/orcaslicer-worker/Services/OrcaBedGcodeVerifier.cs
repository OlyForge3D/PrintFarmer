using System.Globalization;
using System.Text.RegularExpressions;

namespace Farm.OrcaSlicer.Worker.Services;

internal static partial class OrcaBedGcodeVerifier
{
    internal static async Task VerifyAsync(
        string path, string expectedBed, CancellationToken cancellationToken)
    {
        string prefix = expectedBed switch
        {
            "Cool Plate" => "cool_plate",
            "Engineering Plate" => "eng_plate",
            "High Temp Plate" => "hot_plate",
            "Textured PEI Plate" => "textured_plate",
            "Textured Cool Plate" => "textured_cool_plate",
            "Supertack Plate" => "supertack_plate",
            _ => throw new InvalidDataException("Unsupported expected bed type."),
        };
        string initialKey = $"{prefix}_temp_initial_layer";
        string normalKey = $"{prefix}_temp";
        HashSet<string> requiredKeys = ["curr_bed_type", "first_layer_bed_temperature", "bed_temperature_formula", initialKey, normalKey];
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        List<(decimal Temperature, bool IsInitial, bool ExtrusionAfter)> commands = [];
        string? usedFilaments = null;
        bool inHeader = false;
        bool headerSeen = false;
        bool inConfig = false;
        bool configSeen = false;
        bool relativeExtrusion = false;
        int layers = 0;
        using StreamReader reader = File.OpenText(path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line == "; HEADER_BLOCK_START")
            {
                if (headerSeen)
                {
                    throw new InvalidDataException("G-code has ambiguous header metadata.");
                }

                headerSeen = true;
                inHeader = true;
                continue;
            }

            if (line == "; HEADER_BLOCK_END")
            {
                inHeader = false;
                continue;
            }

            if (inHeader && line.StartsWith("; filament: ", StringComparison.Ordinal))
            {
                if (usedFilaments is not null)
                {
                    throw new InvalidDataException("G-code has ambiguous filament metadata.");
                }

                usedFilaments = line["; filament: ".Length..].Trim();
                continue;
            }

            if (line == "; CONFIG_BLOCK_START")
            {
                if (configSeen)
                {
                    throw new InvalidDataException("G-code has ambiguous configuration metadata.");
                }

                configSeen = true;
                inConfig = true;
                continue;
            }

            if (line == "; CONFIG_BLOCK_END")
            {
                inConfig = false;
                continue;
            }

            if (inConfig)
            {
                int equals = line.IndexOf(" = ", StringComparison.Ordinal);
                if (line.StartsWith("; ", StringComparison.Ordinal) && equals > 2)
                {
                    string key = line[2..equals];
                    if (requiredKeys.Contains(key) && !metadata.TryAdd(key, line[(equals + 3)..].Trim()))
                    {
                        throw new InvalidDataException("G-code has duplicate bed metadata.");
                    }
                }

                continue;
            }

            if (line.StartsWith(";LAYER_CHANGE", StringComparison.Ordinal))
            {
                layers++;
                continue;
            }

            string executable = line.Split(';', 2)[0].Trim();
            if (executable.Length == 0)
            {
                continue;
            }

            if (string.Equals(executable, "M83", StringComparison.OrdinalIgnoreCase))
            {
                relativeExtrusion = true;
            }
            else if (string.Equals(executable, "M82", StringComparison.OrdinalIgnoreCase))
            {
                relativeExtrusion = false;
            }

            Match extrusion = ExtrudingMove().Match(executable);
            if (extrusion.Success
                && (!relativeExtrusion || !decimal.TryParse(
                    extrusion.Groups[1].Value,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out decimal amount) || amount > 0))
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    if (commands[i].Temperature == 0)
                    {
                        commands[i] = (commands[i].Temperature, commands[i].IsInitial, true);
                    }
                }
            }

            Match command = BedCommand().Match(executable);
            if (command.Success)
            {
                MatchCollection parameters = BedCommandParameter().Matches(executable[command.Length..]);
                if (parameters.Count != 1)
                {
                    throw new InvalidDataException("G-code contains an unreadable bed temperature command.");
                }

                commands.Add((ParseTemperature(parameters[0].Groups[1].Value), layers < 2, false));
            }

            foreach (Match parameter in BedMacroParameter().Matches(executable))
            {
                commands.Add((ParseTemperature(parameter.Groups[1].Value), layers < 2, false));
            }
        }

        if (!headerSeen || inHeader || usedFilaments is null || !configSeen || inConfig || requiredKeys.Any(key => !metadata.ContainsKey(key))
            || metadata["curr_bed_type"] != expectedBed)
        {
            throw new InvalidDataException("G-code bed selection metadata is missing or does not match the requested plate.");
        }

        int[] filamentIndices = usedFilaments.Split(',').Select(part =>
            int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index > 0
                ? index - 1
                : throw new InvalidDataException("G-code contains invalid filament metadata.")).ToArray();
        if (filamentIndices.Distinct().Count() != filamentIndices.Length)
        {
            throw new InvalidDataException("G-code has ambiguous filament metadata.");
        }

        decimal[] firstLayer = ParseTemperatures(metadata[initialKey]);
        decimal[] laterLayers = ParseTemperatures(metadata[normalKey]);
        if (ParseTemperature(metadata["first_layer_bed_temperature"]) != firstLayer[0])
        {
            throw new InvalidDataException("G-code first-layer temperature metadata does not match the selected plate.");
        }

        HashSet<decimal> initialTemperatures = filamentIndices.Select(index => GetTemperature(firstLayer, index)).ToHashSet();
        if (initialTemperatures.Count != 1)
        {
            throw new InvalidDataException("G-code first-layer bed temperature is ambiguous.");
        }

        decimal initial = initialTemperatures.Single();
        HashSet<decimal> normalTemperatures = filamentIndices.Select(index => GetTemperature(laterLayers, index)).ToHashSet();
        decimal normal = metadata["bed_temperature_formula"] switch
        {
            "by_highest_temp" => normalTemperatures.Max(),
            "by_first_filament" when normalTemperatures.Count == 1 => normalTemperatures.Single(),
            "by_first_filament" => throw new InvalidDataException("G-code later-layer bed temperature is ambiguous."),
            _ => throw new InvalidDataException("G-code bed temperature formula is invalid."),
        };

        bool initialSeen = false;
        foreach ((decimal temperature, bool isInitial, bool extrusionAfter) in commands)
        {
            if (temperature == 0 && !extrusionAfter && initialSeen)
            {
                continue;
            }

            if (isInitial && temperature == initial)
            {
                initialSeen = true;
            }
            else if (isInitial || temperature != normal)
            {
                throw new InvalidDataException("G-code bed temperature command does not match the selected plate.");
            }
        }

        if (!initialSeen)
        {
            throw new InvalidDataException("G-code does not contain the expected initial bed temperature.");
        }
    }

    private static decimal[] ParseTemperatures(string value) =>
        value.Split(',').Select(part => ParseTemperature(part.Trim())).ToArray();

    private static decimal GetTemperature(decimal[] temperatures, int index) =>
        temperatures[index < temperatures.Length ? index : 0];

    private static decimal ParseTemperature(string value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out decimal temperature) && temperature >= 0
            ? temperature
            : throw new InvalidDataException("G-code contains an invalid bed temperature.");

    [GeneratedRegex(@"^(?:M140|M190)(?=\s|[SR]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BedCommand();

    [GeneratedRegex(@"(?:^|\s)[SR]([^\s]*)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BedCommandParameter();

    [GeneratedRegex(@"(?:^|\s)(?:BED|BED_TEMP|BED_TEMPERATURE)(?:=([^\s]*))?(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BedMacroParameter();

    [GeneratedRegex(@"^G0?[0123](?=\s|[XYZEF]|$).*?E([+-]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtrudingMove();
}
