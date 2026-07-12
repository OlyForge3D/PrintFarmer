using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Centralises the mapping between a PrintFarmer <see cref="Toolhead.Index"/> and the
/// 0-based tool index used by G-code T-commands and slicer per-extruder metadata.
/// </summary>
/// <remarks>
/// <para>
/// Virtual MMU gates are stored at <see cref="Toolhead.Index"/> = 1..N (the physical
/// hotend takes Index=0); G-code tools are 0..N-1. Every consumer that needs to
/// translate between the two MUST go through this helper so the two indexing schemes
/// stay consistent (validators, dispatch, mobile UI parity). Introduced as part of
/// GitHub issue OlyForge3D/PrintFarmer#710.
/// </para>
/// <para>
/// Rule:
/// <list type="bullet">
///   <item><description><see cref="ToolheadType.MmuGate"/> → G-code tool = Index − 1.</description></item>
///   <item><description><see cref="ToolheadType.Physical"/> (traditional single/toolchanger printers and
///     Snapmaker U1 lanes) → G-code tool = Index (identity).</description></item>
/// </list>
/// </para>
/// </remarks>
public static class ToolheadIndexMapper
{
    /// <summary>
    /// Translates a stored <see cref="Toolhead"/> to its 0-based G-code tool index.
    /// Returns <c>null</c> when the toolhead is a virtual MMU gate at
    /// <see cref="Toolhead.Index"/> = 0 (which would produce a negative G-code tool
    /// index — this represents the shared physical hotend of an MMU printer and never
    /// corresponds to a filament source).
    /// </summary>
    public static int? ToGcodeToolIndex(Toolhead toolhead)
    {
        ArgumentNullException.ThrowIfNull(toolhead);

        return toolhead.ToolheadType switch
        {
            ToolheadType.MmuGate => toolhead.Index > 0 ? toolhead.Index - 1 : null,
            _ => toolhead.Index,
        };
    }

    /// <summary>
    /// Convenience overload for callers that only have loose <c>index</c> and
    /// <c>type</c> values (e.g., before the <see cref="Toolhead"/> entity has been
    /// materialised).
    /// </summary>
    public static int? ToGcodeToolIndex(int toolheadIndex, ToolheadType toolheadType)
    {
        return toolheadType switch
        {
            ToolheadType.MmuGate => toolheadIndex > 0 ? toolheadIndex - 1 : null,
            _ => toolheadIndex,
        };
    }
}
