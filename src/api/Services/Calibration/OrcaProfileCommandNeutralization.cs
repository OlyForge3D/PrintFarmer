using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// The rule that decides which native upstream-Orca profile keys can carry arbitrary commands,
/// post-processing scripts or preset-matching notes.
/// </summary>
/// <remarks>
/// <para>
/// Official upstream vendor profiles populate these keys, so a calibration plan neutralizes them
/// rather than refusing the profile outright: the immutable baseline document keeps its original
/// bytes and digest as provenance, while the document a worker receives carries none of their
/// values.
/// </para>
/// <para>
/// The rule is stated by shape rather than by enumeration, because upstream adds custom G-code
/// hooks release by release: every key whose native name ends in <see cref="GcodeSuffix"/> carries
/// commands by construction, so a hook this build has never heard of — a future
/// <c>vendor_magic_gcode</c> — is neutralized on sight instead of surviving as an unknown command
/// field. <see cref="AlwaysForbidden"/> adds the two command-bearing keys that do not carry the
/// suffix. The rule is fixed in this build: it is never extended, narrowed or supplied by a caller,
/// a request or a profile.
/// </para>
/// <para>
/// This type and <see cref="OrcaEffectiveProfileFactory"/> were originally authored inside the
/// calibration generation saga (removed in #1979) but are reused, unmodified, by
/// <c>PinnedOrcaCliRotationTests</c> in <c>Farm.Web.IntegrationTests</c> to neutralize vendor
/// profile command hooks before slicing with the real, pinned OrcaSlicer CLI — so they were
/// relocated here rather than deleted with the rest of the saga.
/// </para>
/// </remarks>
public static class OrcaProfileCommandKeys
{
    /// <summary>The native suffix every upstream custom G-code hook key ends with.</summary>
    public const string GcodeSuffix = "_gcode";

    /// <summary>
    /// Gets the command-bearing keys that do not end in <see cref="GcodeSuffix"/>, in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> AlwaysForbidden { get; } =
    [
        "post_process",
        "printer_notes",
    ];

    private static readonly HashSet<string> AlwaysForbiddenKeys =
        new(AlwaysForbidden, StringComparer.OrdinalIgnoreCase);

    /// <summary>Decides whether a native profile key is neutralized before a worker sees it.</summary>
    /// <param name="key">The native profile key name.</param>
    /// <returns><see langword="true"/> when the key carries server-owned content.</returns>
    /// <remarks>
    /// Native Orca keys are lowercase snake_case. The comparison ignores case anyway, so a cased
    /// variant of a hook name cannot smuggle a command field past the rule.
    /// </remarks>
    public static bool IsForbidden(string? key) =>
        !string.IsNullOrEmpty(key) &&
        (key.EndsWith(GcodeSuffix, StringComparison.OrdinalIgnoreCase) ||
            AlwaysForbiddenKeys.Contains(key));
}

/// <summary>The effective native profile document derived from an exact upstream baseline.</summary>
/// <param name="Json">The canonical effective JSON a worker is allowed to receive.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 of <paramref name="Json"/>.</param>
/// <param name="NeutralizedKeys">
/// The names of the keys that were neutralized, in ordinal order.
/// </param>
public sealed record OrcaEffectiveProfileDocument(
    string Json,
    string Sha256,
    IReadOnlyList<string> NeutralizedKeys);

/// <summary>
/// Derives the effective native profile document a pinned slicing worker may receive from an exact
/// upstream baseline document.
/// </summary>
/// <remarks>
/// <para>
/// The derivation is total, deterministic and driven only by <see cref="OrcaProfileCommandKeys"/>:
/// every top-level key the rule forbids is emptied in place when its declared shape allows it
/// (text becomes <c>""</c>, a list becomes <c>[]</c>) and dropped otherwise. No other key is added,
/// removed, reordered in meaning or rewritten, and no forbidden value is ever copied into the
/// result, so no caller-authored command or note can reach the slicer, a log, a manifest or emitted
/// G-code.
/// </para>
/// <para>
/// The result is canonical: object members are ordered ordinally at every depth, so the same
/// baseline always yields the same bytes and therefore the same digest. Numbers are copied as their
/// original JSON tokens rather than re-formatted through a CLR numeric type, so a profile keeps the
/// precision, magnitude and spelling the vendor shipped.
/// </para>
/// </remarks>
public static class OrcaEffectiveProfileFactory
{
    /// <summary>Derives the effective document from exact baseline JSON text.</summary>
    /// <param name="exactJson">The verbatim upstream baseline document.</param>
    /// <returns>The effective document, its digest and the ordered neutralized keys.</returns>
    /// <exception cref="ArgumentException">The text is absent or is not a JSON object.</exception>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static OrcaEffectiveProfileDocument Derive(string exactJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactJson);
        using JsonDocument document = JsonDocument.Parse(exactJson);
        return Derive(document.RootElement);
    }

    /// <summary>Derives the effective document from an already-parsed baseline document.</summary>
    /// <param name="exact">The verbatim upstream baseline document.</param>
    /// <returns>The effective document, its digest and the ordered neutralized keys.</returns>
    /// <exception cref="ArgumentException">The element is not a JSON object.</exception>
    /// <exception cref="JsonException">A value in the document cannot be written back out.</exception>
    public static OrcaEffectiveProfileDocument Derive(JsonElement exact)
    {
        if (exact.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "An exact native profile document must be a JSON object.",
                nameof(exact));
        }

        List<string> neutralized = [];
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            // Members are visited in ordinal order, so the audit list is identical for two documents
            // that differ only in how their members are laid out.
            foreach (JsonProperty property in exact
                .EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!OrcaProfileCommandKeys.IsForbidden(property.Name))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                    continue;
                }

                neutralized.Add(property.Name);
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        writer.WriteString(property.Name, string.Empty);
                        break;
                    case JsonValueKind.Array:
                        writer.WritePropertyName(property.Name);
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                        break;
                    default:

                        // A shape that is neither text nor a list cannot be emptied in place, so the
                        // key is dropped entirely instead of being carried in any form.
                        break;
                }
            }

            writer.WriteEndObject();
        }

        string json = Encoding.UTF8.GetString(buffer.ToArray());
        return new OrcaEffectiveProfileDocument(
            json,
            ComputeTextSha256(json),
            neutralized);
    }

    /// <summary>Computes the lowercase hexadecimal SHA-256 of a UTF-8 encoded text payload.</summary>
    /// <param name="value">The text payload.</param>
    /// <returns>The lowercase hexadecimal digest.</returns>
    private static string ComputeTextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Writes one baseline value in the canonical form an effective profile document uses.
    /// </summary>
    /// <param name="writer">A writer positioned where the value belongs.</param>
    /// <param name="element">The value to write.</param>
    /// <remarks>
    /// This is deliberately separate from the canonicalization every calibration digest uses. That
    /// one serializes trusted server-owned models, so it may normalize numbers through CLR types;
    /// this one copies an untrusted third-party document a worker must be able to slice, so a
    /// number is emitted as the exact token the vendor wrote. Reading <c>1e999</c> as a
    /// <see cref="double"/> would yield infinity, which is not writable as JSON at all, and
    /// <c>99999999999999999999</c> or a twenty-digit decimal would silently lose digits.
    /// </remarks>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.",
                    nameof(element));
        }
    }
}
