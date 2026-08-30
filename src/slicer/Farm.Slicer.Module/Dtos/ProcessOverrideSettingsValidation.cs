using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Backend-side defense-in-depth validation for the numeric print-quality fields a slice
/// submission's <c>overrides</c> object may supply.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2223 fixed the frontend gap: the slicer settings UI accepted negative
/// perimeter/infill values and a zero top/bottom shell layer count with no inline validation,
/// only failing later with a generic "Slicing failed" once the unsliceable settings reached the
/// worker. Issue #2229 is the backend follow-up — a caller can bypass the frontend entirely by
/// POSTing negative values straight to <c>POST /api/slice</c>, reproducing the exact same late,
/// generic failure via direct API access.
/// </para>
/// <para>
/// This mirrors the frontend's <c>validateOrcaPrintSettings</c>
/// (<c>slicerSettingsValidation.ts</c>) semantics exactly: only a negative or non-finite value is
/// rejected. OrcaSlicer's own vendored settings metadata (<c>orcaSettingsMetadata.json</c>)
/// declares <c>min: 0</c> for <c>wall_loops</c>, <c>top_shell_layers</c> and
/// <c>bottom_shell_layers</c> — zero is legitimate (Spiral vase mode requires
/// <c>top_shell_layers: 0</c>) — so this must not flag zero.
/// </para>
/// <para>
/// Applied once, at the API boundary in <c>SliceJobController.SubmitAsync</c>, against the raw
/// <c>overrides</c> object embedded in the submission's <c>SlicerProfileJson</c> — before the job
/// is persisted or ever reaches the queue/worker. Both downstream override-application paths
/// (<c>SliceJobController.ApplyProcessOverrides</c> and the worker's own
/// <c>HttpJobPollerService.ResolveProfileFromJsonAsync</c>) read overrides from that same
/// persisted document, so validating once here covers both.
/// </para>
/// </remarks>
public static class ProcessOverrideSettingsValidation
{
    /// <summary>
    /// Native OrcaSlicer settings keys that must never be negative, mapped to a human-readable
    /// label used in the rejection message.
    /// </summary>
    private static readonly Dictionary<string, string> NonNegativeFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wall_loops"] = "Perimeters (wall_loops)",
            ["sparse_infill_density"] = "Infill density (sparse_infill_density)",
            ["fill_density"] = "Infill density (fill_density)",
            ["top_shell_layers"] = "Top shell layers (top_shell_layers)",
            ["bottom_shell_layers"] = "Bottom shell layers (bottom_shell_layers)",
        };

    private static readonly Regex LeadingNumberPattern = new(
        @"^\s*[-+]?(\d+(\.\d+)?|\.\d+)([eE][-+]?\d+)?",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Validates the <c>overrides</c> object embedded in a submission's raw
    /// <c>slicerProfileJson</c>, if present.
    /// </summary>
    /// <param name="slicerProfileJson">
    /// The submission's raw, opaque <c>SlicerProfileJson</c> payload. May be null/empty/malformed
    /// — those are handled/rejected elsewhere and are treated as valid here.
    /// </param>
    /// <param name="errorMessage">The rejection reason when validation fails; otherwise null.</param>
    /// <returns><see langword="true"/> when no rejected negative value was found.</returns>
    public static bool TryValidate(string? slicerProfileJson, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(slicerProfileJson))
        {
            return true;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(slicerProfileJson);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("overrides", out JsonElement overridesElem))
            {
                return true;
            }

            if (overridesElem.ValueKind != JsonValueKind.Object)
            {
                // Every downstream consumer (ApplyProcessOverrides, the worker's
                // ResolveProfileFromJsonAsync) expects "overrides" to be a JSON object and calls
                // EnumerateObject() on it. Reject a malformed shape here with a clear 400 instead
                // of letting it reach the worker as a late, generic failure.
                errorMessage = "\"overrides\" must be a JSON object.";
                return false;
            }

            foreach (JsonProperty prop in overridesElem.EnumerateObject())
            {
                if (!NonNegativeFields.TryGetValue(prop.Name, out string? label))
                {
                    continue;
                }

                double? numeric = CoerceToNumber(prop.Value);
                if (numeric is double value && (double.IsNaN(value) || value < 0))
                {
                    errorMessage = double.IsNaN(value)
                        ? $"{label} must be a non-negative number."
                        : $"{label} cannot be negative.";
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            // Malformed SlicerProfileJson is not this validator's concern.
            return true;
        }
    }

    /// <summary>
    /// Validates the same non-negative print-quality fields on the legacy, typed
    /// <see cref="ProcessProfileDto"/> submission shape used by the deprecated
    /// <c>POST /api/slicer/jobs</c> and <c>POST /api/slicer/slice(-model)</c> routes. Those routes
    /// predate the <c>overrides</c>-object convention validated by
    /// <see cref="TryValidate(string?, out string?)"/> and carry the same fields as strongly-typed
    /// properties instead, so they need their own check to close the same bypass.
    /// </summary>
    /// <param name="processProfile">The submission's process/quality profile, if any.</param>
    /// <param name="errorMessage">The rejection reason when validation fails; otherwise null.</param>
    /// <returns><see langword="true"/> when no negative value was found.</returns>
    public static bool TryValidate(ProcessProfileDto? processProfile, out string? errorMessage)
    {
        errorMessage = null;

        if (processProfile is null)
        {
            return true;
        }

        if (processProfile.WallCount < 0)
        {
            errorMessage = "Perimeters (wallCount) cannot be negative.";
            return false;
        }

        if (processProfile.InfillPercentage < 0)
        {
            errorMessage = "Infill density (infillPercentage) cannot be negative.";
            return false;
        }

        if (processProfile.TopLayers < 0)
        {
            errorMessage = "Top shell layers (topLayers) cannot be negative.";
            return false;
        }

        if (processProfile.BottomLayers < 0)
        {
            errorMessage = "Bottom shell layers (bottomLayers) cannot be negative.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Coerces an override value to a finite number, tolerating the string/array encodings
    /// OrcaSlicer configs and profile imports sometimes use (e.g. <c>sparse_infill_density</c> as
    /// <c>"15%"</c>), mirroring the frontend's <c>coerceToNumber</c>. Returns <see langword="null"/>
    /// when the value is absent/empty (nothing to validate), or <see cref="double.NaN"/> when it is
    /// present but not a usable number (itself a rejection, since <c>NaN</c> fails the caller's
    /// range check).
    /// </summary>
    private static double? CoerceToNumber(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out double d) ? d : double.NaN;
            case JsonValueKind.String:
                string? s = value.GetString();
                return string.IsNullOrEmpty(s) ? null : ParseLeadingNumber(s);
            case JsonValueKind.Array:
                using (JsonElement.ArrayEnumerator enumerator = value.EnumerateArray())
                {
                    return enumerator.MoveNext() ? CoerceToNumber(enumerator.Current) : null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// Parses the leading numeric portion of a string the way JavaScript's <c>parseFloat</c> does
    /// (e.g. <c>"-15%"</c> → <c>-15</c>), rather than requiring the whole string to be numeric.
    /// </summary>
    private static double ParseLeadingNumber(string s)
    {
        Match match;
        try
        {
            match = LeadingNumberPattern.Match(s);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathologically long/adversarial input: fail closed as "not a usable number" (NaN),
            // which the caller treats as a rejection, rather than letting the timeout surface as
            // an unhandled 500.
            return double.NaN;
        }

        if (!match.Success)
        {
            return double.NaN;
        }

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.NaN;
    }
}
