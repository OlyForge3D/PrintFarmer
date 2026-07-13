using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Central source of truth for the lowercase wire strings the shipped iOS client uses to
/// decode <see cref="AttentionKind"/> and <see cref="AttentionChangeKind"/> from APNs
/// payloads (see <c>mobile/PrintFarmer/Models/AttentionModels.swift</c>). Kept as an
/// explicit switch rather than <c>enum.ToString().ToLowerInvariant()</c> so a rename or
/// new enum member cannot silently break the pinned contract.
/// </summary>
internal static class AttentionAliasNames
{
    /// <summary>Wire string for an <see cref="AttentionKind"/>.</summary>
    public static string ForKind(AttentionKind kind) => kind switch
    {
        AttentionKind.Failure => "failure",
        AttentionKind.Runout => "runout",
        AttentionKind.Harvest => "harvest",
        AttentionKind.Maintenance => "maintenance",
        AttentionKind.Offline => "offline",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attention kind"),
    };

    /// <summary>Wire string for an <see cref="AttentionChangeKind"/>.</summary>
    public static string ForChangeKind(AttentionChangeKind changeKind) => changeKind switch
    {
        AttentionChangeKind.Created => "created",
        AttentionChangeKind.Updated => "updated",
        AttentionChangeKind.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(changeKind), changeKind, "Unknown attention change kind"),
    };
}
