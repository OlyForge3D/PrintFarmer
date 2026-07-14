using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Per-user opt-out map for native-push attention categories, persisted as JSON on
/// <see cref="Farm.Infrastructure.Domain.Notifications.NotificationPreferences.AttentionPushCategoryPreferencesJson"/>.
/// Absence (null / missing key) means opt-in — new categories light up automatically.
/// See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public sealed class AttentionPushCategoryPreferences
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Dictionary<string, bool> _categories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map keyed by camelCase attention-kind name (<c>failure</c>, <c>offline</c>,
    /// <c>maintenance</c>, <c>harvest</c>, <c>runout</c>) → enabled. Missing entry = true.
    /// Never <see langword="null"/>; setter substitutes an empty dictionary if the
    /// deserializer supplies null (e.g., persisted JSON contains <c>"categories":null</c>).
    ///
    /// Hicks #5: mixed-case keys (e.g. <c>"Failure"</c>) MUST behave identically to the
    /// canonical camelCase form after persistence. The setter therefore rebuilds the
    /// incoming dictionary under <see cref="StringComparer.OrdinalIgnoreCase"/> regardless
    /// of how System.Text.Json materialized it, and collapses duplicate case variants
    /// last-write-wins in insertion order. A user's opt-out of <c>Failure</c> continues
    /// to disable delivery after a full persistence round-trip.
    /// </summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, bool> Categories
    {
        get => _categories;
        set => _categories = RebuildCaseInsensitive(value);
    }

    /// <summary>
    /// Rebuild <paramref name="source"/> under a case-insensitive comparer.
    /// Null / empty inputs produce a fresh empty dictionary. Duplicate case
    /// variants collapse last-write-wins so a persisted payload carrying both
    /// <c>Failure</c> and <c>failure</c> resolves deterministically instead of
    /// throwing <see cref="ArgumentException"/> when the setter is invoked
    /// via deserialization.
    /// </summary>
    private static Dictionary<string, bool> RebuildCaseInsensitive(Dictionary<string, bool>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        // Skip the copy if the source is already a case-insensitive dictionary AND
        // has no duplicate case variants (Dictionary.Comparer already reflects this).
        if (ReferenceEquals(source.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            return source;
        }

        var rebuilt = new Dictionary<string, bool>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, bool> kv in source)
        {
            // Indexer semantics: last-write-wins on duplicate case variants.
            rebuilt[kv.Key] = kv.Value;
        }

        return rebuilt;
    }

    /// <summary>Returns whether the operator wants native pushes for the given kind.</summary>
    public bool IsEnabled(AttentionKind kind)
    {
        string key = ToWireKey(kind);
        return !_categories.TryGetValue(key, out bool enabled) || enabled;
    }

    /// <summary>Sets the opt-in state for a single kind.</summary>
    public void Set(AttentionKind kind, bool enabled) => _categories[ToWireKey(kind)] = enabled;

    /// <summary>Serializes the preferences to the persisted JSON representation.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Parses persisted JSON. Returns an empty (all-enabled) preferences instance for
    /// null / whitespace / malformed input so a corrupt row never breaks delivery.
    /// The returned instance's <see cref="Categories"/> is guaranteed to use
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> so mixed-case persisted keys
    /// still route correctly (Hicks #5).
    /// </summary>
    public static AttentionPushCategoryPreferences FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AttentionPushCategoryPreferences();
        }

        try
        {
            AttentionPushCategoryPreferences? parsed = JsonSerializer.Deserialize<AttentionPushCategoryPreferences>(json, SerializerOptions)
                ?? new AttentionPushCategoryPreferences();

            // Defensive re-canonicalization: even if a future refactor changed the
            // property setter, the round-tripped instance must present a
            // case-insensitive backing dictionary. Rebuild directly against the
            // backing field so the analyzer doesn't flag a self-assignment (S1656).
            parsed._categories = RebuildCaseInsensitive(parsed._categories);
            return parsed;
        }
        catch (JsonException)
        {
            return new AttentionPushCategoryPreferences();
        }
    }

    private static string ToWireKey(AttentionKind kind) => kind switch
    {
        AttentionKind.Failure => "failure",
        AttentionKind.Runout => "runout",
        AttentionKind.Harvest => "harvest",
        AttentionKind.Maintenance => "maintenance",
        AttentionKind.Offline => "offline",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
