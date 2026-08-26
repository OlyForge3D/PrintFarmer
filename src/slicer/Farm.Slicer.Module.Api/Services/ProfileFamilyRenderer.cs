using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Renders authoritative profile-family state into a family-scoped fragment of OrcaSlicer's
/// native Custom bundle.
/// </summary>
public sealed class ProfileFamilyRenderer : IProfileFamilyRenderer
{
    private static readonly JsonSerializerOptions NativeJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly HashSet<string> IdentityKeys = new(StringComparer.Ordinal)
    {
        "name",
        "from",
        "inherits",
        "printer_model",
        "printer_notes",
        "nozzle_diameter",
        "nozzle_type",
        "printer_variant",
        "min_layer_height",
        "max_layer_height",
        "default_print_profile",
        "setting_id",
        "type",
        "instantiation"
    };

    /// <inheritdoc />
    public ProfileFamilyRenderResult Render(
        Guid familyId,
        CloneProfileFamilyRequestDto request,
        AllProfilesResponseDto sourceCatalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceCatalog);

        string familyName = RequireTrimmed(request.FamilyName, nameof(request.FamilyName));
        string sourceManufacturer = RequireTrimmed(request.SourceManufacturer, nameof(request.SourceManufacturer));
        string sourceModelName = RequireTrimmed(request.SourceMachineModelName, nameof(request.SourceMachineModelName));
        List<double> selectedNozzles = NormalizeNozzles(request.NozzleDiameters);
        ValidateFamilyOverrides(request.FamilyOverrides);

        ManufacturerProfilesDto manufacturer = FindManufacturer(sourceCatalog, sourceManufacturer);
        PrinterModelProfilesDto sourceModel = FindSourceModel(manufacturer, sourceModelName);
        MachineModelProfileDto sourceModelMetadata = FindSourceModelMetadata(
            sourceCatalog,
            sourceManufacturer,
            sourceModelName);

        List<SourceMachineVariant> allSourceVariants = sourceModel.MachineProfiles
            .Select(profile => new SourceMachineVariant(profile, ResolveNozzleDiameter(profile)))
            .OrderBy(variant => variant.NozzleDiameter)
            .ThenBy(variant => variant.Profile.Name, StringComparer.Ordinal)
            .ToList();

        List<SourceMachineVariant> selectedVariants = SelectMachineVariants(
            allSourceVariants,
            selectedNozzles,
            sourceModelName);
        SourceMachineVariant anchor = allSourceVariants
            .FirstOrDefault(variant => NearlyEqual(variant.NozzleDiameter, 0.4))
            ?? selectedVariants[0];

        Dictionary<string, string> customMachineNameBySource = selectedVariants.ToDictionary(
            variant => variant.Profile.Name,
            variant => BuildMachineName(familyName, variant.NozzleDiameter),
            StringComparer.OrdinalIgnoreCase);

        List<ProfileClone<ProcessProfileDto>> processClones = BuildProcessClones(
            sourceModel.ProcessProfiles,
            customMachineNameBySource,
            familyName);
        List<ProfileClone<FilamentProfileDto>> filamentClones = BuildFilamentClones(
            sourceModel.FilamentProfiles,
            customMachineNameBySource,
            familyName);

        Dictionary<string, string> processNameMap = processClones
            .GroupBy(clone => clone.Source.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single().CloneName
                    : throw new ProfileFamilySourceException(
                        $"Source process profile name '{group.Key}' is ambiguous."),
                StringComparer.OrdinalIgnoreCase);

        string familyPath = familyId.ToString("N", CultureInfo.InvariantCulture);
        List<RenderedProfileFileDto> files = [];
        List<ManifestEntry> machineModelEntries = [];
        List<ManifestEntry> machineEntries = [];
        List<ManifestEntry> processEntries = [];
        List<ManifestEntry> filamentEntries = [];

        AddDocument(
            "machine",
            familyPath,
            familyName,
            BuildMachineModelDocument(sourceModelMetadata, familyId, familyName, selectedNozzles),
            files,
            machineModelEntries);

        string baseName = $"{familyName} base";
        AddDocument(
            "machine",
            familyPath,
            baseName,
            BuildMachineBaseDocument(baseName, familyName, anchor.Profile.Name, request.FamilyOverrides),
            files,
            machineEntries);

        List<RenderedMachineVariant> renderedVariants = [];
        SortedDictionary<string, JsonElement> anchorSettings = CopySettings(anchor.Profile.Settings);
        foreach (SourceMachineVariant variant in selectedVariants)
        {
            string customName = customMachineNameBySource[variant.Profile.Name];
            SortedDictionary<string, JsonElement> delta = HarvestDelta(
                anchorSettings,
                CopySettings(variant.Profile.Settings),
                request.FamilyOverrides.Keys);
            string? sourceDefaultProcess = TryGetString(delta, "default_print_profile")
                ?? TryGetString(CopySettings(variant.Profile.Settings), "default_print_profile");

            if (!string.IsNullOrWhiteSpace(sourceDefaultProcess))
            {
                if (!processNameMap.TryGetValue(sourceDefaultProcess, out string? clonedDefaultProcess))
                {
                    throw new ProfileFamilySourceException(
                        $"Default process profile '{sourceDefaultProcess}' for source preset " +
                        $"'{variant.Profile.Name}' could not be cloned.");
                }

                Set(delta, "default_print_profile", clonedDefaultProcess);
            }

            Set(delta, "type", "machine");
            Set(delta, "name", customName);
            Set(delta, "inherits", baseName);
            Set(delta, "from", "system");
            Set(delta, "instantiation", "true");
            Set(delta, "printer_model", familyName);
            Set(delta, "nozzle_diameter", new[] { FormatNozzle(variant.NozzleDiameter) });
            Set(delta, "printer_variant", FormatNozzle(variant.NozzleDiameter));
            _ = delta.Remove("setting_id");

            AddDocument(
                "machine",
                familyPath,
                customName,
                delta,
                files,
                machineEntries);

            renderedVariants.Add(new RenderedMachineVariant(
                customName,
                variant.NozzleDiameter,
                variant.Profile.Name,
                SerializeDocument(HarvestDelta(
                    anchorSettings,
                    CopySettings(variant.Profile.Settings),
                    request.FamilyOverrides.Keys))));
        }

        foreach (ProfileClone<ProcessProfileDto> clone in processClones)
        {
            SortedDictionary<string, JsonElement> document = BuildCompatibilityStub(
                "process",
                clone.CloneName,
                clone.Source.Name,
                clone.Source.Instantiation,
                clone.TargetMachineNames);
            AddDocument(
                "process",
                familyPath,
                clone.CloneName,
                document,
                files,
                processEntries);
        }

        foreach (ProfileClone<FilamentProfileDto> clone in filamentClones)
        {
            SortedDictionary<string, JsonElement> document = BuildCompatibilityStub(
                "filament",
                clone.CloneName,
                clone.Source.Name,
                clone.Source.Instantiation,
                clone.TargetMachineNames);
            AddDocument(
                "filament",
                familyPath,
                clone.CloneName,
                document,
                files,
                filamentEntries);
        }

        string manifestJson = SerializeManifest(
            machineModelEntries,
            machineEntries,
            processEntries,
            filamentEntries);
        string canonicalOverrides = SerializeCanonicalDictionary(request.FamilyOverrides);

        return new ProfileFamilyRenderResult(
            new ProfileFamilyBundleDto(familyId, familyName, manifestJson, files),
            canonicalOverrides,
            renderedVariants,
            processClones.Count,
            filamentClones.Count);
    }

    private static ManufacturerProfilesDto FindManufacturer(
        AllProfilesResponseDto catalog,
        string sourceManufacturer)
    {
        ManufacturerProfilesDto? manufacturer = catalog.ByHierarchy
            .FirstOrDefault(pair => string.Equals(
                pair.Key,
                sourceManufacturer,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        return manufacturer ?? throw new ProfileFamilySourceException(
            $"Source manufacturer '{sourceManufacturer}' is unavailable on the selected OrcaSlicer worker.");
    }

    private static PrinterModelProfilesDto FindSourceModel(
        ManufacturerProfilesDto manufacturer,
        string sourceModelName)
    {
        PrinterModelProfilesDto? model = manufacturer.Models.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sourceModelName, StringComparison.OrdinalIgnoreCase));
        return model ?? throw new ProfileFamilySourceException(
            $"Source machine model '{sourceModelName}' is unavailable on the selected OrcaSlicer worker.");
    }

    private static MachineModelProfileDto FindSourceModelMetadata(
        AllProfilesResponseDto catalog,
        string sourceManufacturer,
        string sourceModelName)
    {
        IList<MachineModelProfileDto>? models = catalog.MachineModelProfiles
            .FirstOrDefault(pair => string.Equals(
                pair.Key,
                sourceManufacturer,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        MachineModelProfileDto? model = models?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sourceModelName, StringComparison.OrdinalIgnoreCase));
        return model ?? throw new ProfileFamilySourceException(
            $"Source machine model record '{sourceModelName}' is unavailable on the selected OrcaSlicer worker.");
    }

    private static List<SourceMachineVariant> SelectMachineVariants(
        IReadOnlyList<SourceMachineVariant> available,
        IReadOnlyList<double> selectedNozzles,
        string sourceModelName)
    {
        List<SourceMachineVariant> selected = [];
        foreach (double nozzle in selectedNozzles)
        {
            List<SourceMachineVariant> matches = available
                .Where(variant => NearlyEqual(variant.NozzleDiameter, nozzle))
                .ToList();
            if (matches.Count != 1)
            {
                throw new ProfileFamilySourceException(
                    matches.Count == 0
                        ? $"Source preset for {FormatNozzle(nozzle)} mm nozzle is unavailable in '{sourceModelName}'."
                        : $"Source preset for {FormatNozzle(nozzle)} mm nozzle is ambiguous in '{sourceModelName}'.");
            }

            selected.Add(matches[0]);
        }

        return selected;
    }

    private static List<ProfileClone<ProcessProfileDto>> BuildProcessClones(
        IEnumerable<ProcessProfileDto> profiles,
        IReadOnlyDictionary<string, string> customMachineNameBySource,
        string familyName)
    {
        List<ProcessProfileDto> candidates = profiles
            .Where(profile => profile.CompatiblePrinters.Count > 0)
            .Where(profile => profile.CompatiblePrinters.Any(customMachineNameBySource.ContainsKey))
            .OrderBy(profile => profile.Name, StringComparer.Ordinal)
            .ToList();

        return candidates.Select((profile, index) => new ProfileClone<ProcessProfileDto>(
            profile,
            MakeUniqueCloneName(
                RebaseProfileName(profile.Name, familyName),
                candidates,
                profile.Name,
                index),
            ResolveTargetMachineNames(profile.CompatiblePrinters, customMachineNameBySource)))
            .ToList();
    }

    private static List<ProfileClone<FilamentProfileDto>> BuildFilamentClones(
        IEnumerable<FilamentProfileDto> profiles,
        IReadOnlyDictionary<string, string> customMachineNameBySource,
        string familyName)
    {
        List<FilamentProfileDto> candidates = profiles
            .Where(profile => !string.Equals(
                profile.Manufacturer,
                "OrcaFilamentLibrary",
                StringComparison.OrdinalIgnoreCase))
            .Where(profile => profile.CompatiblePrinters.Count > 0)
            .Where(profile => profile.CompatiblePrinters.Any(customMachineNameBySource.ContainsKey))
            .OrderBy(profile => profile.Name, StringComparer.Ordinal)
            .ThenBy(profile => profile.Material, StringComparer.Ordinal)
            .ToList();

        return candidates.Select((profile, index) =>
        {
            string rebasedName = RebaseProfileName(profile.Name, familyName);
            int duplicateCount = candidates.Count(candidate =>
                string.Equals(candidate.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            string cloneName = duplicateCount > 1
                ? $"{rebasedName} ({profile.Material})"
                : rebasedName;
            return new ProfileClone<FilamentProfileDto>(
                profile,
                cloneName,
                ResolveTargetMachineNames(profile.CompatiblePrinters, customMachineNameBySource));
        }).ToList();
    }

    private static string MakeUniqueCloneName<T>(
        string rebasedName,
        IReadOnlyList<T> candidates,
        string sourceName,
        int index)
        where T : ProcessProfileDto
    {
        int duplicateCount = candidates.Count(candidate =>
            string.Equals(candidate.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        return duplicateCount > 1 ? $"{rebasedName} ({index + 1})" : rebasedName;
    }

    private static List<string> ResolveTargetMachineNames(
        IEnumerable<string> compatiblePrinters,
        IReadOnlyDictionary<string, string> customMachineNameBySource)
    {
        return compatiblePrinters
            .Where(customMachineNameBySource.ContainsKey)
            .Select(sourceName => customMachineNameBySource[sourceName])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static SortedDictionary<string, JsonElement> BuildMachineModelDocument(
        MachineModelProfileDto source,
        Guid familyId,
        string familyName,
        IReadOnlyList<double> selectedNozzles)
    {
        SortedDictionary<string, JsonElement> document = CopySettings(source.Settings);
        Set(document, "type", "machine_model");
        Set(document, "name", familyName);
        Set(document, "model_id", $"PrintFarmer_{familyId:N}");
        Set(document, "family", "Custom");
        Set(document, "nozzle_diameter", string.Join(';', selectedNozzles.Select(FormatNozzle)));
        return document;
    }

    private static SortedDictionary<string, JsonElement> BuildMachineBaseDocument(
        string baseName,
        string familyName,
        string anchorPresetName,
        IReadOnlyDictionary<string, JsonElement> familyOverrides)
    {
        SortedDictionary<string, JsonElement> document = new(StringComparer.Ordinal);
        Set(document, "type", "machine");
        Set(document, "name", baseName);
        Set(document, "inherits", anchorPresetName);
        Set(document, "from", "system");
        Set(document, "instantiation", "false");
        Set(document, "printer_model", familyName);
        foreach ((string key, JsonElement value) in familyOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            document[key] = value.Clone();
        }

        return document;
    }

    private static SortedDictionary<string, JsonElement> BuildCompatibilityStub(
        string type,
        string cloneName,
        string sourceName,
        bool instantiation,
        IReadOnlyList<string> targetMachineNames)
    {
        if (targetMachineNames.Count == 0)
        {
            throw new ProfileFamilySourceException(
                $"Profile '{sourceName}' resolved no selected source variants.");
        }

        SortedDictionary<string, JsonElement> document = new(StringComparer.Ordinal);
        Set(document, "type", type);
        Set(document, "name", cloneName);
        Set(document, "inherits", sourceName);
        Set(document, "from", "system");
        Set(document, "instantiation", instantiation ? "true" : "false");
        Set(document, "compatible_printers", targetMachineNames);
        Set(document, "compatible_printers_condition", string.Empty);
        return document;
    }

    private static SortedDictionary<string, JsonElement> HarvestDelta(
        SortedDictionary<string, JsonElement> anchor,
        SortedDictionary<string, JsonElement> variant,
        IEnumerable<string> familyOverrideKeys)
    {
        HashSet<string> excluded = new(IdentityKeys, StringComparer.Ordinal);
        excluded.UnionWith(familyOverrideKeys);

        SortedDictionary<string, JsonElement> delta = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in variant)
        {
            if (excluded.Contains(key))
            {
                continue;
            }

            if (!anchor.TryGetValue(key, out JsonElement anchorValue) ||
                !JsonElement.DeepEquals(anchorValue, value))
            {
                delta[key] = value.Clone();
            }
        }

        foreach (string perNozzleKey in IdentityKeys.Where(key =>
                     key is "printer_notes" or "nozzle_type" or "printer_variant" or
                         "min_layer_height" or "max_layer_height" or "default_print_profile")
                 .Where(variant.ContainsKey))
        {
            delta[perNozzleKey] = variant[perNozzleKey].Clone();
        }

        return delta;
    }

    private static void ValidateFamilyOverrides(IReadOnlyDictionary<string, JsonElement> overrides)
    {
        string? forbidden = overrides.Keys.FirstOrDefault(IdentityKeys.Contains);
        if (forbidden is not null)
        {
            throw new ArgumentException(
                $"Family override '{forbidden}' is nozzle-specific or identity-bearing and cannot be shared.",
                nameof(overrides));
        }
    }

    private static List<double> NormalizeNozzles(IEnumerable<double> nozzles)
    {
        List<double> normalized = nozzles
            .Where(double.IsFinite)
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
        if (normalized.Count == 0)
        {
            throw new ArgumentException("At least one positive nozzle diameter is required.", nameof(nozzles));
        }

        return normalized;
    }

    private static double ResolveNozzleDiameter(MachineProfileDto profile)
    {
        if (profile.NozzleDiameter is > 0)
        {
            return profile.NozzleDiameter.Value;
        }

        SortedDictionary<string, JsonElement> settings = CopySettings(profile.Settings);
        if (settings.TryGetValue("nozzle_diameter", out JsonElement value))
        {
            JsonElement scalar = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().FirstOrDefault()
                : value;
            string? text = scalar.ValueKind == JsonValueKind.String
                ? scalar.GetString()
                : scalar.GetRawText();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
                parsed > 0)
            {
                return parsed;
            }
        }

        throw new ProfileFamilySourceException(
            $"Source preset '{profile.Name}' does not declare a valid nozzle_diameter.");
    }

    private static SortedDictionary<string, JsonElement> CopySettings(
        IReadOnlyDictionary<string, object> settings)
    {
        SortedDictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach ((string key, object value) in settings)
        {
            result[key] = value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(value);
        }

        return result;
    }

    private static void AddDocument(
        string typeDirectory,
        string familyPath,
        string profileName,
        SortedDictionary<string, JsonElement> document,
        List<RenderedProfileFileDto> files,
        List<ManifestEntry> manifestEntries)
    {
        string fileName = BuildFileName(profileName);
        string relativePath = $"{typeDirectory}/{familyPath}/{fileName}.json";
        files.Add(new RenderedProfileFileDto(relativePath, SerializeDocument(document)));
        manifestEntries.Add(new ManifestEntry(profileName, relativePath));
    }

    private static string SerializeManifest(
        IReadOnlyCollection<ManifestEntry> machineModelEntries,
        IReadOnlyCollection<ManifestEntry> machineEntries,
        IReadOnlyCollection<ManifestEntry> processEntries,
        IReadOnlyCollection<ManifestEntry> filamentEntries)
    {
        var manifest = new
        {
            name = "Custom",
            version = "01.00.00.00",
            force_update = "1",
            description = "Generated by PrintFarmer. Do not edit.",
            machine_model_list = machineModelEntries,
            machine_list = machineEntries,
            process_list = processEntries,
            filament_list = filamentEntries
        };
        return JsonSerializer.Serialize(manifest, NativeJsonOptions);
    }

    private static string SerializeDocument(
        IReadOnlyDictionary<string, JsonElement> document)
        => JsonSerializer.Serialize(document, NativeJsonOptions);

    private static string SerializeCanonicalDictionary(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach ((string key, JsonElement value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                WriteCanonicalJson(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string BuildFileName(string name)
    {
        string sanitized = new(
            name.Select(character =>
                    char.IsLetterOrDigit(character) || character is ' ' or '.' or '_' or '-'
                        ? character
                        : '_')
                .ToArray());
        sanitized = sanitized.Trim();
        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].TrimEnd();
        }

        string suffix = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..10].ToLowerInvariant();
        return $"{(string.IsNullOrEmpty(sanitized) ? "profile" : sanitized)}-{suffix}";
    }

    private static string RebaseProfileName(string sourceName, string familyName)
    {
        int atIndex = sourceName.LastIndexOf('@');
        string prefix = atIndex >= 0 ? sourceName[..atIndex].TrimEnd() : sourceName.TrimEnd();
        return $"{prefix} @{familyName}";
    }

    private static string BuildMachineName(string familyName, double nozzleDiameter)
        => $"{familyName} {FormatNozzle(nozzleDiameter)} nozzle";

    private static string FormatNozzle(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) < 0.0001;

    private static string RequireTrimmed(string value, string parameterName)
    {
        string trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? throw new ArgumentException("Value is required.", parameterName)
            : trimmed;
    }

    private static string? TryGetString(
        SortedDictionary<string, JsonElement> values,
        string key)
    {
        if (!values.TryGetValue(key, out JsonElement value))
        {
            return null;
        }

        JsonElement scalar = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().FirstOrDefault()
            : value;
        return scalar.ValueKind == JsonValueKind.String ? scalar.GetString() : null;
    }

    private static void Set<T>(
        SortedDictionary<string, JsonElement> document,
        string key,
        T value)
        => document[key] = JsonSerializer.SerializeToElement(value);

    private sealed record SourceMachineVariant(MachineProfileDto Profile, double NozzleDiameter);

    private sealed record ProfileClone<T>(
        T Source,
        string CloneName,
        IReadOnlyList<string> TargetMachineNames);

    private sealed record ManifestEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("sub_path")] string SubPath);
}
