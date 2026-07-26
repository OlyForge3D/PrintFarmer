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
/// unmodified upstream documents so the smoke exercises that path. Selection therefore has two layers:
/// a machine that declares a nozzle diameter closest to 0.4mm, and then a process and a filament that
/// are explicitly compatible with that exact machine's published name — never an arbitrary first match
/// and never a "universal" (empty <c>compatible_printers</c>) profile that merely happens to declare
/// the field the caller is looking for. Real OrcaSlicer rejects a slice whose process/filament wasn't
/// authored for the selected machine with "process not compatible with printer", so compatibility is a
/// hard requirement here, not a preference.
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
    /// Thrown when the container publishes no usable machine, process or filament document, or no
    /// process/filament is explicitly compatible with the selected machine.
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

        return Select(document.RootElement);
    }

    /// <summary>
    /// Pure selection over an already-parsed <c>/api/profiles</c> document. Extracted from
    /// <see cref="SelectAsync"/> so the exact-compatibility selection rules can be exercised directly
    /// against JSON fixtures without a running worker.
    /// </summary>
    /// <param name="root">Root element of the worker's <c>/api/profiles</c> response.</param>
    /// <returns>The selected exact documents.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the document publishes no usable machine, process or filament candidate, or no
    /// process/filament is explicitly compatible with the selected machine.
    /// </exception>
    internal static PinnedOrcaProfileSelection Select(JsonElement root)
    {
        IReadOnlyList<ProfileCandidate> machines = ReadCandidates(root, "machineProfiles");
        IReadOnlyList<ProfileCandidate> processes = ReadCandidates(root, "processProfiles");
        IReadOnlyList<ProfileCandidate> filaments = ReadCandidates(root, "filamentProfiles");

        ProfileCandidate machine = machines
            .Where(candidate => TryReadNozzleDiameter(candidate.Settings, out _))
            .OrderBy(NozzleDistanceFromPreferred)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The pinned worker publishes no machine profile that declares a nozzle diameter.");
        _ = TryReadNozzleDiameter(machine.Settings, out double nozzleDiameter);

        ProfileCandidate process = SelectCompatible(processes, machine, "layer_height")
            ?? throw new InvalidOperationException(
                $"The pinned worker publishes no process profile explicitly compatible with machine '{machine.Name}' " +
                "(its compatible_printers metadata does not include that machine).");

        ProfileCandidate filament = SelectCompatible(filaments, machine, "filament_type")
            ?? throw new InvalidOperationException(
                $"The pinned worker publishes no filament profile explicitly compatible with machine '{machine.Name}' " +
                "(its compatible_printers metadata does not include that machine).");

        return new PinnedOrcaProfileSelection(
            Canonicalize(machine.Settings),
            Canonicalize(process.Settings),
            Canonicalize(filament.Settings),
            nozzleDiameter);
    }

    /// <summary>
    /// Selects the best process/filament candidate that both declares the given functional settings
    /// key and is explicitly compatible with <paramref name="machine"/>'s exact published name. Among
    /// equally compatible candidates, one published under the same manufacturer hierarchy as the
    /// machine is preferred; ties are then broken deterministically by name.
    /// </summary>
    private static ProfileCandidate? SelectCompatible(
        IReadOnlyList<ProfileCandidate> candidates,
        ProfileCandidate machine,
        string requiredSettingsKey) =>
        candidates
            .Where(candidate => candidate.Settings.ContainsKey(requiredSettingsKey))
            .Where(candidate => IsExplicitlyCompatible(candidate, machine.Name))
            .OrderByDescending(candidate => SharesManufacturerHierarchy(candidate, machine))
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// True only when the candidate's own metadata explicitly names the machine — never true for a
    /// "universal" candidate with an empty/missing <c>compatible_printers</c> list, which is precisely
    /// the ambiguity that let an incompatible tuple through before.
    /// </summary>
    private static bool IsExplicitlyCompatible(ProfileCandidate candidate, string machineName) =>
        !string.IsNullOrEmpty(machineName) &&
        candidate.CompatiblePrinters.Any(
            compatibleName => string.Equals(compatibleName, machineName, StringComparison.Ordinal));

    private static bool SharesManufacturerHierarchy(ProfileCandidate candidate, ProfileCandidate machine) =>
        !string.IsNullOrEmpty(candidate.Manufacturer) &&
        !string.IsNullOrEmpty(machine.Manufacturer) &&
        string.Equals(candidate.Manufacturer, machine.Manufacturer, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProfileCandidate> ReadCandidates(JsonElement root, string groupName)
    {
        if (!root.TryGetProperty(groupName, out JsonElement group) ||
            group.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<ProfileCandidate> candidates = [];
        foreach (JsonProperty manufacturerGroup in group.EnumerateObject())
        {
            if (manufacturerGroup.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement profile in manufacturerGroup.Value.EnumerateArray())
            {
                if (profile.ValueKind != JsonValueKind.Object ||
                    !profile.TryGetProperty("settings", out JsonElement settingsElement) ||
                    settingsElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (JsonNode.Parse(settingsElement.GetRawText()) is not JsonObject settings || settings.Count == 0)
                {
                    continue;
                }

                string name = profile.TryGetProperty("name", out JsonElement nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                string? manufacturer = profile.TryGetProperty("manufacturer", out JsonElement manufacturerElement) &&
                    manufacturerElement.ValueKind == JsonValueKind.String
                    ? manufacturerElement.GetString()
                    : null;

                candidates.Add(new ProfileCandidate(name, manufacturer, ReadCompatiblePrinters(profile), settings));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Reads the candidate's declared compatibility list. Production DTOs serialize this as the
    /// snake_case <c>compatible_printers</c>, but the camelCase <c>compatiblePrinters</c> spelling is
    /// accepted too so an upstream naming-policy change can't silently make every profile look
    /// "universal" and reintroduce the incompatible-tuple bug this catalogue exists to prevent.
    /// </summary>
    private static IReadOnlyList<string> ReadCompatiblePrinters(JsonElement profile)
    {
        if (!TryGetCompatiblePrintersElement(profile, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> names = [];
        foreach (JsonElement entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is string value)
            {
                names.Add(value);
            }
        }

        return names;
    }

    private static bool TryGetCompatiblePrintersElement(JsonElement profile, out JsonElement element) =>
        profile.TryGetProperty("compatible_printers", out element) ||
        profile.TryGetProperty("compatiblePrinters", out element);

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

    private static double NozzleDistanceFromPreferred(ProfileCandidate machine) =>
        TryReadNozzleDiameter(machine.Settings, out double diameter) ? Math.Abs(diameter - 0.4) : double.MaxValue;

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

    /// <summary>
    /// One candidate profile parsed out of a manufacturer-grouped array in the worker's response: its
    /// exact published name, optional manufacturer, declared compatibility list and native settings
    /// bag (the part that is hashed, stored and eventually sliced).
    /// </summary>
    private sealed record ProfileCandidate(
        string Name,
        string? Manufacturer,
        IReadOnlyList<string> CompatiblePrinters,
        JsonObject Settings);
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
