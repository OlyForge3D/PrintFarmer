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
    /// Parses raw profile JSON, extracts all settings as flat key-value pairs, removes volatile keys and returns:
    /// SanitizedRawJson (deterministic, serialized), SettingsJson (all properties as flat object), Hash (SHA256 hex).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when rawJson is null/empty.</exception>
    (string SanitizedRawJson, string SettingsJson, string Hash) ParseAndPrepare(string rawJson);
}

public sealed class ProfileParsingService : IProfileParsingService
{
    // Keys that are typically volatile or environment-specific and should not contribute to hash uniqueness
    private static readonly HashSet<string> VolatileKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "lastModified", "modified", "generated_at", "timestamp", "uuid", "id",
        "profile_id", "creation_date"
    };

    public (string SanitizedRawJson, string SettingsJson, string Hash) ParseAndPrepare(string rawJson)
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
            // Treat invalid JSON as opaque: hash original string; settings empty; sanitized = trimmed original
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
        Dictionary<string, object?> settings = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            string key = kv.Key;
            JsonNode? value = kv.Value;

            // Skip volatile keys from sanitized set
            if (VolatileKeys.Contains(key))
            {
                continue;
            }

            // Collect all non-null values into settings, preserving original key names from raw JSON
            if (value is not null)
            {
                // For nested objects/arrays, store as JSON string; for primitives, store the value
                if (value is JsonValue v)
                {
                    settings[key] = ExtractPrimitiveValue(v);
                }
                else if (value is JsonObject or JsonArray)
                {
                    settings[key] = value.ToJsonString();
                }
                else
                {
                    settings[key] = value.ToString();
                }
            }

            sanitized[key] = value; // Preserve other keys verbatim (without volatile ones)
        }

        // Produce deterministic ordering by sorting keys alphabetically
        // Clone each node to avoid parent conflicts (each JsonNode can only have one parent)
        JsonObject sanitizedOrdered = new();
        foreach (string key in sanitized.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sanitizedOrdered[key] = CloneJsonNode(sanitized[key]);
        }

        JsonObject settingsOrdered = new();
        foreach (var kvp in settings.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            // Create new JsonValue for each settings entry to avoid parent conflicts
            settingsOrdered[kvp.Key] = kvp.Value switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                float f => JsonValue.Create((double)f),
                bool b => JsonValue.Create(b),
                _ => JsonValue.Create(kvp.Value?.ToString() ?? "")
            };
        }

        string sanitizedJson = sanitizedOrdered.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        string settingsJson = settingsOrdered.ToJsonString(new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        // Compute hash from sanitized JSON (stable canonical form)
        string hash = ComputeSha256(sanitizedJson);
        return (sanitizedJson, settingsJson, hash);
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

    /// <summary>
    /// Extracts the primitive value from a JsonValue node.
    /// Returns the actual .NET type (string, int, double, bool, etc.) instead of the JsonValue wrapper.
    /// </summary>
    private static object? ExtractPrimitiveValue(JsonValue value)
    {
        try
        {
            if (value.TryGetValue(out string? s))
            {
                return s;
            }

            if (value.TryGetValue(out int i))
            {
                return i;
            }

            if (value.TryGetValue(out double d))
            {
                return d;
            }

            if (value.TryGetValue(out float f))
            {
                return f;
            }

            if (value.TryGetValue(out bool b))
            {
                return b;
            }

            if (value.TryGetValue(out long l))
            {
                return l;
            }

            if (value.TryGetValue(out decimal dec))
            {
                return dec;
            }

            // Fallback: try to parse as string
            return value.ToString();
        }
        catch
        {
            // If extraction fails, return string representation
            return value.ToString();
        }
    }

    /// <summary>
    /// Deep clones a JsonNode to avoid parent conflicts.
    /// System.Text.Json enforces that each JsonNode can only have one parent.
    /// </summary>
    private static JsonNode? CloneJsonNode(JsonNode? node)
    {
        return node is null
            ? null
            : node switch
            {
                JsonObject obj => new JsonObject(obj.Select(kvp =>
                    new KeyValuePair<string, JsonNode?>(kvp.Key, CloneJsonNode(kvp.Value)))),

                JsonArray arr => new JsonArray(arr.Select(CloneJsonNode).ToArray()),

                JsonValue val => JsonValue.Create(val.GetValue<object>()),

                _ => node
            };
    }
}
