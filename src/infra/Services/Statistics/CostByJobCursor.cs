using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Services.Statistics;

/// <summary>
/// Opaque, versioned seek-pagination cursor for <c>GetCostsByJobAsync</c> (issue #1734).
/// </summary>
/// <remarks>
/// <para>
/// The cursor captures the full canonical ordering key of the last item on the previous
/// page so the next page can resume without duplicates or omissions:
/// completedAt (ActualEndTime, nullable) DESC → job id DESC. The job-id tiebreak is
/// mandatory because multiple jobs can share the exact same (or null) completion
/// timestamp. Null completion times sort as <see cref="DateTime.MinValue"/>.
/// </para>
/// <para>
/// The wire form is a Base64Url-encoded JSON envelope carrying an explicit schema version,
/// mirroring <see cref="Attention.AttentionFeedCursor"/>. Malformed or unsupported cursors
/// are rejected by <see cref="TryDecode"/> so the caller can surface an explicit validation
/// error rather than silently restarting from page 1.
/// </para>
/// </remarks>
public sealed record CostByJobCursor(
    [property: JsonPropertyName("ca")] long CompletedAtTicks,
    [property: JsonPropertyName("id")] Guid JobId)
{
    /// <summary>Current cursor schema version. Bumped when the ordering key changes shape.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Builds a cursor from the last row on a returned page.
    /// </summary>
    public static CostByJobCursor FromRow(DateTime? completedAt, Guid jobId)
    {
        return new CostByJobCursor((completedAt ?? DateTime.MinValue).Ticks, jobId);
    }

    /// <summary>Encodes the cursor to its opaque wire token.</summary>
    public string Encode()
    {
        Envelope envelope = new(SchemaVersion, CompletedAtTicks, JobId);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        return Base64UrlEncode(json);
    }

    /// <summary>
    /// Attempts to decode an opaque cursor token. Returns <c>false</c> for null/blank input,
    /// non-Base64Url input, malformed JSON, or an unsupported schema version.
    /// </summary>
    public static bool TryDecode(string? token, out CostByJobCursor? cursor)
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
            if (envelope is null || envelope.Version != SchemaVersion || envelope.JobId == Guid.Empty)
            {
                return false;
            }

            cursor = new CostByJobCursor(envelope.CompletedAtTicks, envelope.JobId);
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
        [property: JsonPropertyName("ca")] long CompletedAtTicks,
        [property: JsonPropertyName("id")] Guid JobId);
}
