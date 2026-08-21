namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Policy for which process-profile settings a slice submission's <c>overrides</c> object may
/// supply.
/// </summary>
/// <remarks>
/// <para>
/// Overrides exist so a user can tune print behaviour — layer height, infill, speeds — on top of a
/// resolved process profile. A handful of settings are not print behaviour at all: they decide
/// whether that process preset may be used with the selected printer in the first place.
/// OrcaSlicer gates the slice on <c>compatible_printers</c>, comparing each entry against the
/// machine document's system preset name, and the worker materializes
/// <c>compatible_printers_condition</c> into that array for profiles that express compatibility
/// only through the condition (issue #1795).
/// </para>
/// <para>
/// Accepting either key from a submission would let the submission authorize its own
/// machine/process pairing, defeating the compatibility check rather than passing it. They are
/// therefore rejected wherever overrides are applied.
/// </para>
/// <para>
/// This lives in the shared module because overrides are applied on two independent paths that
/// must not diverge: the API's native-profile snapshot
/// (<c>SliceJobController.ApplyProcessOverrides</c>, whose document the worker writes verbatim
/// after digest verification) and the worker's own resolution
/// (<c>HttpJobPollerService.ResolveProfileFromJsonAsync</c>). Filtering on only one of them leaves
/// the other as a bypass.
/// </para>
/// </remarks>
public static class ProcessOverridePolicy
{
    /// <summary>
    /// Process settings that decide machine/process compatibility rather than print behaviour.
    /// </summary>
    private static readonly HashSet<string> CompatibilityKeys =
        new(StringComparer.Ordinal)
        {
            "compatible_printers",
            "compatible_printers_condition",
        };

    /// <summary>
    /// Reports whether a submission-supplied override key must be rejected.
    /// </summary>
    /// <param name="key">The native snake_case settings key the override targets.</param>
    /// <returns>
    /// <see langword="true"/> when the key decides compatibility and so may not come from a
    /// submission.
    /// </returns>
    public static bool IsRejectedOverrideKey(string key) => CompatibilityKeys.Contains(key);
}
