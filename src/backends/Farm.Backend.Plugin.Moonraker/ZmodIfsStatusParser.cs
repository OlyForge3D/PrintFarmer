using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Parses ZMOD (Flashforge AD5X / 5M) Intelligent Filament Switch telemetry from Moonraker
/// status frames into the shared MMU fields of <see cref="PrinterState"/> (issue #3323).
/// </summary>
/// <remarks>
/// ZMOD 1.7.3+ exposes two Klipper objects:
/// <list type="bullet">
/// <item><c>zmod_ifs</c>: <c>available</c>, <c>Ports</c> (per-port filament sensor booleans),
/// <c>Chan</c> (1-based active channel, 0 = none) plus diagnostic fields.</item>
/// <item><c>zmod_color</c>: <c>channel</c> (1-based active channel) and <c>slots</c>, a list of
/// <c>{ID, Material, Color, HEX, hasFilament}</c> where <c>Material</c> "?" and <c>HEX</c> ""
/// mean unset.</item>
/// </list>
/// Moonraker sends diffs, so every field is applied independently and merged into state.
/// <para>
/// ZMOD reports channel <em>selection</em> and per-port sensors only; it has no head sensor or
/// load-completion signal. A selected channel is therefore published with filament state
/// "Unknown" and no action, so downstream consumers never treat it as a confirmed, settled load.
/// Malformed sensor or channel values are reported as unknown rather than empty/unloaded.
/// </para>
/// </remarks>
internal static class ZmodIfsStatusParser
{
    public const string IfsObjectName = "zmod_ifs";
    public const string ColorObjectName = "zmod_color";

    /// <summary>Gate count assumed until the printer reports ports or slots (AD5X IFS has 4).</summary>
    public const int DefaultGateCount = 4;

    /// <summary>Upper bound guarding against malformed payloads allocating huge arrays.</summary>
    public const int MaxGateCount = 16;

    // A present but unparseable channel: unknown selection, never "nothing selected".
    private const int InvalidChannel = -1;

    /// <summary>
    /// Applies any ZMOD IFS objects present in <paramref name="statusObj"/> to <paramref name="state"/>.
    /// Returns true when ZMOD telemetry was found and applied.
    /// </summary>
    public static bool Apply(PrinterState state, JsonElement statusObj)
    {
        if (statusObj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool hasIfs = statusObj.TryGetProperty(IfsObjectName, out JsonElement ifs) &&
                      ifs.ValueKind == JsonValueKind.Object;
        bool hasColor = statusObj.TryGetProperty(ColorObjectName, out JsonElement color) &&
                        color.ValueKind == JsonValueKind.Object;
        if (!hasIfs && !hasColor)
        {
            return false;
        }

        // Another MMU protocol (Happy Hare / AFC / Qidibox / U1) already owns this printer's MMU state.
        if (state.MmuType is not (MmuProtocol.Unknown or MmuProtocol.ZmodIfs))
        {
            return false;
        }

        state.ZmodIfsDetected = true;

        if (hasIfs)
        {
            ApplyIfs(state, ifs);
        }

        IReadOnlyList<ZmodSlot> slots = hasColor ? ApplyColor(state, color) : [];

        Recompute(state, slots);
        return true;
    }

    /// <summary>
    /// True when this frame itself carries a live gate count (a non-empty <c>zmod_ifs.Ports</c> or
    /// <c>zmod_color.slots</c> array), as opposed to unrelated deltas that leave cached counts untouched.
    /// </summary>
    public static bool ReportsGateCount(JsonElement statusObj)
    {
        if (statusObj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasNonEmptyArray(statusObj, IfsObjectName, "Ports") ||
               HasNonEmptyArray(statusObj, ColorObjectName, "slots");
    }

    private static bool HasNonEmptyArray(JsonElement statusObj, string objectName, string propertyName) =>
        statusObj.TryGetProperty(objectName, out JsonElement obj) &&
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out JsonElement arr) &&
        arr.ValueKind == JsonValueKind.Array &&
        arr.GetArrayLength() > 0;

    private static void ApplyIfs(PrinterState state, JsonElement ifs)
    {
        if (ifs.TryGetProperty("available", out JsonElement available) &&
            available.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            state.ZmodIfsAvailable = available.ValueKind == JsonValueKind.True;
        }

        if (ifs.TryGetProperty("Ports", out JsonElement ports) && ports.ValueKind == JsonValueKind.Array)
        {
            state.ZmodIfsPorts = ports.EnumerateArray()
                .Take(MaxGateCount)
                .Select(ParseNullableBool)
                .ToArray();
        }

        if (ifs.TryGetProperty("Chan", out JsonElement chan))
        {
            state.ZmodIfsChannel = TryParseInt(chan, out int channel) ? channel : InvalidChannel;
        }
    }

    private static List<ZmodSlot> ApplyColor(PrinterState state, JsonElement color)
    {
        if (color.TryGetProperty("channel", out JsonElement channelElem))
        {
            state.ZmodColorChannel = TryParseInt(channelElem, out int channel) ? channel : InvalidChannel;
        }

        List<ZmodSlot> slots = [];
        if (!color.TryGetProperty("slots", out JsonElement slotsElem) || slotsElem.ValueKind != JsonValueKind.Array)
        {
            return slots;
        }

        int position = 0;
        foreach (JsonElement slot in slotsElem.EnumerateArray())
        {
            int fallbackIndex = position++;
            if (slot.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int index = slot.TryGetProperty("ID", out JsonElement idElem) && TryParseInt(idElem, out int id) && id >= 1
                ? id - 1
                : fallbackIndex;
            if (index >= MaxGateCount)
            {
                continue;
            }

            bool hasMaterial = slot.TryGetProperty("Material", out JsonElement materialElem) &&
                               materialElem.ValueKind is JsonValueKind.String or JsonValueKind.Null;
            bool hasHex = slot.TryGetProperty("HEX", out JsonElement hexElem) &&
                          hexElem.ValueKind is JsonValueKind.String or JsonValueKind.Null;
            bool hasFilamentField = slot.TryGetProperty("hasFilament", out JsonElement filamentElem);
            bool? hasFilament = hasFilamentField ? ParseNullableBool(filamentElem) : null;

            slots.Add(new ZmodSlot(
                index,
                hasMaterial,
                hasMaterial ? NormalizeMaterial(materialElem) : string.Empty,
                hasHex,
                hasHex ? NormalizeHex(hexElem) : string.Empty,
                hasFilamentField,
                hasFilament));
        }

        state.ZmodSlotCount = Math.Max(state.ZmodSlotCount, slots.Count == 0 ? 0 : slots.Max(s => s.Index) + 1);
        return slots;
    }

    private static void Recompute(PrinterState state, IReadOnlyList<ZmodSlot> slots)
    {
        int portCount = state.ZmodIfsPorts?.Length ?? 0;
        int gateCount = Math.Max(portCount, state.ZmodSlotCount);
        if (gateCount == 0)
        {
            gateCount = DefaultGateCount;
        }

        state.EnsureMmuGateArrays(gateCount);
        state.ZmodSlotHasFilament = ResizeNullable(state.ZmodSlotHasFilament, gateCount);
        state.MmuNumGates = gateCount;

        foreach (ZmodSlot slot in slots)
        {
            if (slot.HasMaterial)
            {
                state.MmuGateMaterial![slot.Index] = slot.Material;
                state.MmuGateFilamentName![slot.Index] = slot.Material;
            }

            if (slot.HasHex)
            {
                state.MmuGateColor![slot.Index] = slot.Hex;
            }

            // A present but malformed value clears the cache to unknown rather than keeping stale presence.
            if (slot.HasFilamentField)
            {
                state.ZmodSlotHasFilament![slot.Index] = slot.HasFilament;
            }
        }

        // IFS port sensors are the authority on presence; slot hasFilament is the fallback.
        for (int i = 0; i < gateCount; i++)
        {
            bool? present = state.ZmodIfsPorts is { } ports && i < ports.Length
                ? ports[i]
                : state.ZmodSlotHasFilament![i];
            state.MmuGateStatus![i] = present switch
            {
                true => 1,
                false => 0,
                null => 2,
            };
        }

        // A selected channel is not proof that filament reached the hotend (no head sensor), so it is
        // never reported as "Loaded". Only channel 0 is an explicit "nothing selected".
        int? activeChannel = state.ZmodIfsChannel ?? state.ZmodColorChannel;
        if (activeChannel is int chan && chan >= 1 && chan <= gateCount)
        {
            state.MmuActiveGate = chan - 1;
            state.MmuActiveTool = chan - 1;
            state.MmuFilamentState = "Unknown";
        }
        else
        {
            state.MmuActiveGate = -1;
            state.MmuActiveTool = -1;
            state.MmuFilamentState = activeChannel == 0 ? "Unloaded" : "Unknown";
        }

        // ZMOD reports no MMU action; never retain a settled action from earlier state.
        state.MmuAction = null;

        bool available = state.ZmodIfsAvailable != false;
        state.MmuType = MmuProtocol.ZmodIfs;
        state.MmuDetected = available;
        state.MmuEnabled = available;
        state.MmuIsHomed = available;
        state.MmuDirty = true;
    }

    private static bool?[] ResizeNullable(bool?[]? source, int length)
    {
        if (source is not null && source.Length == length)
        {
            return source;
        }

        bool?[] resized = new bool?[length];
        if (source is not null)
        {
            Array.Copy(source, resized, Math.Min(source.Length, length));
        }

        return resized;
    }

    private static string NormalizeMaterial(JsonElement element)
    {
        string? material = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
        return string.IsNullOrEmpty(material) || material == "?" ? string.Empty : material;
    }

    private static string NormalizeHex(JsonElement element)
    {
        string? hex = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim().TrimStart('#') : null;
        if (string.IsNullOrEmpty(hex) || (hex.Length != 6 && hex.Length != 8) || !hex.All(char.IsAsciiHexDigit))
        {
            return string.Empty;
        }

        return "#" + hex[..6].ToUpperInvariant();
    }

    private static bool? ParseNullableBool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt32(out int value) && value is 0 or 1 => value == 1,
        _ => null,
    };

    private static bool TryParseInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private sealed record ZmodSlot(
        int Index,
        bool HasMaterial,
        string Material,
        bool HasHex,
        string Hex,
        bool HasFilamentField,
        bool? HasFilament);
}
