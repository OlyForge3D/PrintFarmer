using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// Reads the exact native OrcaSlicer profile documents out of the published pinned worker over its own
/// HTTP surface, so the calibration snapshot is seeded with profiles that container can actually slice.
/// </summary>
/// <remarks>
/// <para>
/// The worker resolves each profile's inheritance chain before publishing it, so the documents returned
/// here already carry every inherited setting. The only key removed is the now-satisfied
/// <c>inherits</c> marker, which the production plan compiler refuses precisely because an unresolved
/// parent reference would make the document non-self-contained. The resulting document is the single
/// exact artefact that is hashed and stored on the immutable snapshot.
/// </para>
/// <para>
/// Official upstream profiles legitimately carry command and notes fields such as
/// <c>machine_start_gcode</c> or <c>printer_notes</c>. Nothing here filters those out or strips them:
/// neutralizing them is the production plan compiler's job, and this catalogue deliberately hands it
/// unmodified upstream documents so the smoke exercises that path. Selection is therefore purely
/// functional — a machine that declares a nozzle diameter, a process that declares a layer height and
/// a filament that declares a filament type.
/// </para>
/// </remarks>
internal static class PinnedOrcaProfileCatalog
{
    /// <summary>
    /// Selects a machine, process and filament document the pinned worker publishes.
    /// </summary>
    /// <param name="workerBaseAddress">Loopback base address of the running worker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The selected exact documents.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the container publishes no usable machine, process or filament document.
    /// </exception>
    public static async Task<PinnedOrcaProfileSelection> SelectAsync(
        string workerBaseAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerBaseAddress);

        using HttpClient client = new()
        {
            BaseAddress = new Uri(workerBaseAddress),
            Timeout = TimeSpan.FromMinutes(3),
        };
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/profiles", UriKind.Relative),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "The pinned worker did not publish its profile catalogue: HTTP " +
                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        IReadOnlyList<JsonObject> machines = ReadSettings(document.RootElement, "machineProfiles");
        IReadOnlyList<JsonObject> processes = ReadSettings(document.RootElement, "processProfiles");
        IReadOnlyList<JsonObject> filaments = ReadSettings(document.RootElement, "filamentProfiles");

        JsonObject machine = machines
            .Where(candidate => TryReadNozzleDiameter(candidate, out _))
            .OrderBy(candidate => NozzleDistanceFromPreferred(candidate))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The pinned worker publishes no machine profile that declares a nozzle diameter.");
        _ = TryReadNozzleDiameter(machine, out double nozzleDiameter);

        JsonObject process = processes.FirstOrDefault(
            candidate => candidate.ContainsKey("layer_height"))
            ?? throw new InvalidOperationException(
                "The pinned worker publishes no process profile that declares a layer height.");
        JsonObject filament = filaments.FirstOrDefault(
            candidate => candidate.ContainsKey("filament_type"))
            ?? throw new InvalidOperationException(
                "The pinned worker publishes no filament profile that declares a filament type.");

        return new PinnedOrcaProfileSelection(
            Canonicalize(machine),
            Canonicalize(process),
            Canonicalize(filament),
            nozzleDiameter);
    }

    private static IReadOnlyList<JsonObject> ReadSettings(JsonElement root, string groupName)
    {
        if (!root.TryGetProperty(groupName, out JsonElement group) ||
            group.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<JsonObject> documents = [];
        foreach (JsonProperty manufacturer in group.EnumerateObject())
        {
            if (manufacturer.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement profile in manufacturer.Value.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object ||
                    !profile.TryGetProperty("settings", out JsonElement settings) ||
                    settings.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (JsonNode.Parse(settings.GetRawText()) is JsonObject parsed && parsed.Count > 0)
                {
                    documents.Add(parsed);
                }
            }
        }

        return documents;
    }

    /// <summary>
    /// Removes the resolved inheritance marker and renders the document with a stable key order.
    /// </summary>
    /// <param name="document">The flattened document the worker published.</param>
    /// <returns>The exact document that will be hashed, stored and delivered.</returns>
    private static string Canonicalize(JsonObject document)
    {
        JsonObject ordered = [];
        foreach (KeyValuePair<string, JsonNode?> property in document
            .Where(property => !string.Equals(property.Key, "inherits", StringComparison.Ordinal))
            .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            ordered[property.Key] = property.Value?.DeepClone();
        }

        return ordered.ToJsonString();
    }

    private static double NozzleDistanceFromPreferred(JsonObject machine) =>
        TryReadNozzleDiameter(machine, out double diameter) ? Math.Abs(diameter - 0.4) : double.MaxValue;

    private static bool TryReadNozzleDiameter(JsonObject machine, out double diameter)
    {
        diameter = 0;
        if (!machine.TryGetPropertyValue("nozzle_diameter", out JsonNode? node) || node is null)
        {
            return false;
        }

        string? raw = node switch
        {
            JsonArray array when array.Count > 0 => array[0]?.ToString(),
            JsonValue value => value.ToString(),
            _ => null,
        };
        return raw is not null &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out diameter) &&
            diameter > 0;
    }
}

/// <summary>The exact native documents the pinned worker publishes for one sliceable combination.</summary>
/// <param name="MachineJson">Exact machine document.</param>
/// <param name="ProcessJson">Exact process document.</param>
/// <param name="FilamentJson">Exact filament document.</param>
/// <param name="NozzleDiameterMillimeters">Nozzle diameter the machine document declares.</param>
internal sealed record PinnedOrcaProfileSelection(
    string MachineJson,
    string ProcessJson,
    string FilamentJson,
    double NozzleDiameterMillimeters);
