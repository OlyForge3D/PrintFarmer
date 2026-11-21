using System.Text.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>Versioned job contract for project-aware Orca bed selection.</summary>
public sealed record ProjectBedSelectionContract(string? ExplicitBedType)
{
    /// <summary>The worker capability suffix required to handle this contract.</summary>
    public const string Capability = "project-bed-selection-v1";

    /// <summary>Parses an optional contract, rejecting ambiguous contract-bearing payloads.</summary>
    public static bool TryParse(string? json, out ProjectBedSelectionContract? contract, out string? error)
    {
        contract = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Legacy profile parsing retains responsibility for malformed payloads.
            return true;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            List<JsonProperty> markers = root.EnumerateObject().Where(p => p.NameEquals("bedSelection")).ToList();
            if (markers.Count == 0)
            {
                return true;
            }

            if (markers.Count != 1 || markers[0].Value.ValueKind != JsonValueKind.Object)
            {
                error = "Bed selection must be a single object.";
                return false;
            }

            JsonElement marker = markers[0].Value;
            List<JsonProperty> versions = marker.EnumerateObject().Where(p => p.NameEquals("version")).ToList();
            List<JsonProperty> beds = marker.EnumerateObject().Where(p => p.NameEquals("explicitBedType")).ToList();
            if (versions.Count != 1 || versions[0].Value.ValueKind != JsonValueKind.Number
                || !versions[0].Value.TryGetInt32(out int version) || version != 1
                || beds.Count > 1 || marker.EnumerateObject().Any(p => p.Name is not ("version" or "explicitBedType")))
            {
                error = "The bed selection contract is invalid or unsupported.";
                return false;
            }

            string? explicitBedType = null;
            if (beds.Count == 1)
            {
                if (beds[0].Value.ValueKind != JsonValueKind.String
                    || !IsSupportedBedType(beds[0].Value.GetString()))
                {
                    error = "The explicit bed type is invalid.";
                    return false;
                }

                explicitBedType = beds[0].Value.GetString();
            }

            List<JsonProperty> overrides = root.EnumerateObject().Where(p => p.NameEquals("overrides")).ToList();
            if (overrides.Count > 1 || (overrides.Count == 1
                && (overrides[0].Value.ValueKind != JsonValueKind.Object
                    || overrides[0].Value.EnumerateObject().Any(p => p.NameEquals("curr_bed_type")))))
            {
                error = "The bed selection contract cannot be combined with a curr_bed_type override.";
                return false;
            }

            contract = new ProjectBedSelectionContract(explicitBedType);
            return true;
        }
    }

    /// <summary>Returns whether the value is a supported native Orca bed name.</summary>
    public static bool IsSupportedBedType(string? bedType) => bedType is
        "Cool Plate" or "Engineering Plate" or "High Temp Plate" or "Textured PEI Plate"
        or "Textured Cool Plate" or "Supertack Plate";
}
