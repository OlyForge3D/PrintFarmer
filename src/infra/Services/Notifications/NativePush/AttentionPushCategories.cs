using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Fixed catalog of APNs category identifiers, threading rules and action-id strings
/// bound to each <see cref="AttentionKind"/>. Frozen in code so the mobile client can
/// register the identical categories at launch and so #708 acceptance ("actionable
/// categories") stays testable. See <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public static class AttentionPushCategories
{
    /// <summary>APNs category for print failure incidents.</summary>
    public const string PrinterFailure = "PRINTER_FAILURE";

    /// <summary>APNs category for printer-offline attention items.</summary>
    public const string PrinterOffline = "PRINTER_OFFLINE";

    /// <summary>APNs category for maintenance-due attention items.</summary>
    public const string MaintenanceDue = "MAINTENANCE_DUE";

    /// <summary>APNs category for harvest-ready plates.</summary>
    public const string HarvestReady = "HARVEST_READY";

    /// <summary>APNs category for filament-runout attention items (reserved for F4/#709).</summary>
    public const string FilamentRunout = "FILAMENT_RUNOUT";

    // Action ids — kept as constants so tests, mobile registration, and server payloads
    // agree exactly.
    public const string ActionPause = "PAUSE";
    public const string ActionCancel = "CANCEL";
    public const string ActionSnooze15 = "SNOOZE_15";
    public const string ActionAcknowledge = "ACKNOWLEDGE";
    public const string ActionOpenSwap = "OPEN_SWAP";

    /// <summary>
    /// Selects the APNs category for the given <see cref="AttentionKind"/>. Returns
    /// <c>null</c> when the kind is not a categorized attention event and the delivery
    /// service should skip the fan-out.
    /// </summary>
    public static string? CategoryFor(AttentionKind kind) => kind switch
    {
        AttentionKind.Failure => PrinterFailure,
        AttentionKind.Offline => PrinterOffline,
        AttentionKind.Maintenance => MaintenanceDue,
        AttentionKind.Harvest => HarvestReady,
        AttentionKind.Runout => FilamentRunout,
        _ => null,
    };

    /// <summary>
    /// The stable thread id used to collapse multiple pushes for the same underlying
    /// entity into a single conversation on iOS. Failure/offline items thread per printer;
    /// runout threads per printer+toolhead; harvest/maintenance thread per attention item
    /// so distinct events remain distinguishable in the notification tray.
    /// </summary>
    public static string ThreadIdFor(AttentionKind kind, Guid printerId, int? toolheadIndex, string attentionItemId)
        => kind switch
        {
            AttentionKind.Failure => $"printer:{printerId:D}:failure",
            AttentionKind.Offline => $"printer:{printerId:D}:offline",
            AttentionKind.Runout => $"printer:{printerId:D}:runout:{toolheadIndex ?? -1}",
            AttentionKind.Harvest => $"attention:{attentionItemId}",
            AttentionKind.Maintenance => $"attention:{attentionItemId}",
            _ => $"attention:{attentionItemId}",
        };

    /// <summary>
    /// Ordered action-id list for the category. Order matches the button order the mobile
    /// client renders when it registers the category at launch. When the operator has
    /// opted out of a category, an empty list is used and the tap-only path still applies.
    /// </summary>
    public static IReadOnlyList<string> ActionsFor(AttentionKind kind) => kind switch
    {
        AttentionKind.Failure => new[] { ActionPause, ActionCancel, ActionSnooze15 },
        AttentionKind.Offline => new[] { ActionSnooze15 },
        AttentionKind.Maintenance => new[] { ActionAcknowledge, ActionSnooze15 },
        AttentionKind.Harvest => Array.Empty<string>(),
        AttentionKind.Runout => new[] { ActionOpenSwap, ActionSnooze15 },
        _ => Array.Empty<string>(),
    };
}
