namespace Farm.Infrastructure.Services.Mutations;

/// <summary>
/// Shared causal guard for consumers that reconcile absence-based observations.
/// </summary>
public static class MutationWatermarkCausality
{
    /// <summary>
    /// Returns true only when the row was stamped after rollout and no later than
    /// the watermark captured before the authoritative observation.
    /// </summary>
    public static bool CanAuthorizeAbsence(long lastMutationSequence, long? originWatermark)
        => originWatermark is long watermark
            && lastMutationSequence > 0
            && lastMutationSequence <= watermark;
}
