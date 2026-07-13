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
    /// </summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, bool> Categories
    {
        get => _categories;
        set => _categories = value ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
    /// </summary>
    public static AttentionPushCategoryPreferences FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AttentionPushCategoryPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<AttentionPushCategoryPreferences>(json, SerializerOptions)
                ?? new AttentionPushCategoryPreferences();
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
