using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Parses imported slicer profile JSON, extracts core metadata and produces a sanitized
/// deterministic JSON string plus a stable SHA256 hash for deduplication.
/// </summary>
public interface IProfileParsingService
{
    /// <summary>
    /// Parses raw profile JSON, extracts metadata, removes volatile keys and returns:
    /// SanitizedRawJson (deterministic, serialized), MetadataJson (subset object), Hash (SHA256 hex).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when rawJson is null/empty.</exception>
    (string SanitizedRawJson, string MetadataJson, string Hash) ParseAndPrepare(string rawJson);
}

public sealed class ProfileParsingService : IProfileParsingService
{
    // Keys that are typically volatile or environment-specific and should not contribute to hash uniqueness
    private static readonly HashSet<string> VolatileKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "lastModified", "modified", "generated_at", "timestamp", "uuid", "id",
        "profile_id", "creation_date"
    };

    // Canonical metadata key mapping (source key -> canonical metadata name)
    private static readonly Dictionary<string, string> MetadataKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "layer_height", "layerHeight" },
        { "layerHeight", "layerHeight" },
        { "nozzle_diameter", "nozzleDiameter" },
        { "nozzleDiameter", "nozzleDiameter" },
        { "filament_type", "filamentMaterial" },
        { "filamentType", "filamentMaterial" },
        { "filament_material", "filamentMaterial" },
        { "material", "filamentMaterial" },
        { "infill_density", "infillPercentage" },
        { "infillPercent", "infillPercentage" },
        { "infill_percentage", "infillPercentage" },
        { "infill", "infillPercentage" },
        { "slicer_version", "slicerVersion" },
        { "version", "slicerVersion" },
        { "profile_type", "profileType" },
        { "type", "profileType" },
    };

    public (string SanitizedRawJson, string MetadataJson, string Hash) ParseAndPrepare(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new ArgumentException("Raw JSON is required", nameof(rawJson));
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (Exception)
        {
            // Treat invalid JSON as opaque: hash original string; metadata empty; sanitized = trimmed original
            string trimmed = rawJson.Trim();
            return (trimmed, "{}", ComputeSha256(trimmed));
        }

        if (root is null)
        {
            string trimmed = rawJson.Trim();
            return (trimmed, "{}", ComputeSha256(trimmed));
        }

        // Only handle object roots; arrays or primitives we treat as opaque
        if (root is not JsonObject obj)
        {
            string trimmed = rawJson.Trim();
            return (trimmed, "{}", ComputeSha256(trimmed));
        }

        Dictionary<string, JsonNode?> sanitized = new(StringComparer.Ordinal);
        Dictionary<string, JsonNode?> metadata = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            string key = kv.Key;
            JsonNode? value = kv.Value;

            // Skip volatile keys from sanitized set
            if (VolatileKeys.Contains(key))
            {
                continue;
            }

            // Collect metadata if key recognized and value is primitive/number/string
            if (value is JsonValue v && MetadataKeyMap.TryGetValue(key, out string? canonicalValue) && canonicalValue is not null)
            {
                metadata[canonicalValue!] = v;
            }

            sanitized[key] = value; // Preserve other keys verbatim (without volatile ones)
        }

        // Produce deterministic ordering by sorting keys alphabetically
        JsonObject sanitizedOrdered = new();
        foreach (string key in sanitized.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sanitizedOrdered[key] = sanitized[key];
        }

        JsonObject metadataOrdered = new();
        foreach (string key in metadata.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            metadataOrdered[key] = metadata[key];
        }

        string sanitizedJson = sanitizedOrdered.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        string metadataJson = metadataOrdered.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Compute hash from sanitized JSON (stable canonical form)
        string hash = ComputeSha256(sanitizedJson);
        return (sanitizedJson, metadataJson, hash);
    }

    private static string ComputeSha256(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = SHA256.HashData(bytes);
        StringBuilder sb = new(hashBytes.Length * 2);
        foreach (byte b in hashBytes)
        {
            _ = sb.Append(b.ToString("x2")); // lower-case hex for consistency
        }
        return sb.ToString();
    }
}
