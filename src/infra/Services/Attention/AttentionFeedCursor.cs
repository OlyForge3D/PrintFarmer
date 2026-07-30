using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Opaque, versioned pagination cursor for the unified attention feed (issue #707, R1).
/// </summary>
/// <remarks>
/// <para>
/// The cursor captures the full canonical ordering key of the last item on the previous
/// page so the next page can resume without duplicates or omissions:
/// severity DESC → (deadlineAt ?? MaxValue) ASC → occurredAt ASC → stable item id ASC.
/// The item-id tiebreak is mandatory because multiple offline items share the same
/// <see cref="Sources.OfflineAttentionSource.StableOfflineOccurredAt"/> anchor.
/// </para>
/// <para>
/// The wire form is a Base64Url-encoded JSON envelope carrying an explicit schema version.
/// Malformed or unsupported cursors are rejected by <see cref="TryDecode"/> so the caller
/// can surface an explicit validation error rather than silently restarting from page 1.
/// </para>
/// </remarks>
public sealed record AttentionFeedCursor(
    [property: JsonPropertyName("sev")] int Severity,
    [property: JsonPropertyName("dl")] long DeadlineTicks,
    [property: JsonPropertyName("oc")] long OccurredTicks,
    [property: JsonPropertyName("id")] string Id)
{
    /// <summary>Current cursor schema version. Bumped when the ordering key changes shape.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Builds a cursor from the last item on a returned page.
    /// </summary>
    public static AttentionFeedCursor FromItem(AttentionItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AttentionFeedCursor(
            (int)item.Severity,
            (item.DeadlineAt ?? DateTime.MaxValue).Ticks,
            item.OccurredAt.Ticks,
            item.Id);
    }

    /// <summary>Encodes the cursor to its opaque wire token.</summary>
    public string Encode()
    {
        Envelope envelope = new(SchemaVersion, Severity, DeadlineTicks, OccurredTicks, Id);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        return Base64UrlEncode(json);
    }

    /// <summary>
    /// Attempts to decode an opaque cursor token. Returns <c>false</c> for null/blank input,
    /// non-Base64Url input, malformed JSON, or an unsupported schema version.
    /// </summary>
    public static bool TryDecode(string? token, out AttentionFeedCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(bytes, SerializerOptions);
            if (envelope is null || envelope.Version != SchemaVersion || envelope.Id is null)
            {
                return false;
            }

            cursor = new AttentionFeedCursor(envelope.Severity, envelope.DeadlineTicks, envelope.OccurredTicks, envelope.Id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        string base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string token)
    {
        string base64 = token.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
            case 1:
                throw new FormatException("Invalid Base64Url length.");
        }

        return Convert.FromBase64String(base64);
    }

    private sealed record Envelope(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("sev")] int Severity,
        [property: JsonPropertyName("dl")] long DeadlineTicks,
        [property: JsonPropertyName("oc")] long OccurredTicks,
        [property: JsonPropertyName("id")] string? Id);
}
