using Farm.Infrastructure.Dtos.Attention;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// Builds the fixed <c>printfarmer://</c> deep-link URLs the mobile app resolves on tap.
/// The scheme was adjudicated by Dallas; see <c>docs/OPERATOR_NATIVE_PUSH.md</c>.
/// </summary>
public static class AttentionDeepLinks
{
    /// <summary>The universal deep-link scheme registered by the iOS app.</summary>
    public const string Scheme = "printfarmer";

    /// <summary>
    /// Deep link resolved on tap for the given attention envelope. Filament-runout links
    /// route to the swap flow; offline links route to the printer detail; everything else
    /// routes to the attention item so per-user snooze / dedupe stays server-side.
    /// </summary>
    public static string For(AttentionKind kind, Guid printerId, string attentionItemId, int? toolheadIndex, Guid? jobId)
        => kind switch
        {
            AttentionKind.Offline => $"{Scheme}://printer/{printerId:D}",
            AttentionKind.Runout => BuildRunoutLink(printerId, toolheadIndex, jobId),
            _ => $"{Scheme}://attention/{attentionItemId}",
        };

    private static string BuildRunoutLink(Guid printerId, int? toolheadIndex, Guid? jobId)
    {
        int tool = toolheadIndex ?? 0;
        string path = $"{Scheme}://printer/{printerId:D}/swap/{tool}";
        return jobId.HasValue
            ? $"{path}?jobId={jobId.Value:D}"
            : path;
    }
}
