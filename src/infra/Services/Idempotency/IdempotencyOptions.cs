namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Tunable knobs for the persistent Idempotency-Key store (issue #715).
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// Age past which a row still in
    /// <see cref="Farm.Infrastructure.Domain.IdempotencyRecordStatus.Processing"/>
    /// is considered abandoned — its owning request died before completing — and may
    /// be reclaimed by a new first-request (delete-then-insert). This guards against a
    /// crashed or hung request permanently blocking a key as "in progress" until the
    /// full retention window elapses. Must be comfortably larger than the slowest
    /// legitimate gated mutation so a genuinely in-flight request is never reclaimed.
    /// </summary>
    public TimeSpan ProcessingStaleness { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Shared default instance used when no explicit options are supplied.</summary>
    public static IdempotencyOptions Default { get; } = new();
}
