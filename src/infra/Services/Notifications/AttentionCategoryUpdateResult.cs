using System.Collections.Generic;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Typed outcome envelope returned by
/// <see cref="INotificationService.UpdateAttentionCategoryPreferencesAsync"/>.
/// Hicks #6: the service applies bounds inside its serializable transaction
/// (so a concurrent update cannot slip a bulk request past the cumulative
/// cap) and returns a first-class rejection reason instead of throwing a
/// generic exception the controller would broad-catch.
/// </summary>
public sealed record AttentionCategoryUpdateResult
{
    private AttentionCategoryUpdateResult(
        AttentionCategoryUpdateStatus status,
        IReadOnlyDictionary<string, bool>? categories,
        AttentionCategoryUpdateRejection? rejection)
    {
        Status = status;
        Categories = categories;
        Rejection = rejection;
    }

    /// <summary>Overall outcome discriminator.</summary>
    public AttentionCategoryUpdateStatus Status { get; }

    /// <summary>
    /// On success, the final persisted category map. Never <see langword="null"/>
    /// when <see cref="Status"/> is <see cref="AttentionCategoryUpdateStatus.Success"/>;
    /// always <see langword="null"/> for rejections.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? Categories { get; }

    /// <summary>Typed rejection reason. Non-null iff <see cref="Status"/> is Rejected.</summary>
    public AttentionCategoryUpdateRejection? Rejection { get; }

    /// <summary>Constructs a success result carrying the persisted final map.</summary>
    public static AttentionCategoryUpdateResult FromSuccess(IReadOnlyDictionary<string, bool> categories)
    {
        System.ArgumentNullException.ThrowIfNull(categories);
        return new AttentionCategoryUpdateResult(AttentionCategoryUpdateStatus.Success, categories, null);
    }

    /// <summary>Constructs a rejection result carrying a typed reason.</summary>
    public static AttentionCategoryUpdateResult FromRejection(AttentionCategoryUpdateRejection rejection)
    {
        return new AttentionCategoryUpdateResult(AttentionCategoryUpdateStatus.Rejected, null, rejection);
    }
}

/// <summary>Result discriminator for <see cref="AttentionCategoryUpdateResult"/>.</summary>
public enum AttentionCategoryUpdateStatus
{
    /// <summary>The merged map was persisted; <see cref="AttentionCategoryUpdateResult.Categories"/> carries it.</summary>
    Success,

    /// <summary>The request was rejected before persistence; the row is byte-for-byte unchanged.</summary>
    Rejected,
}

/// <summary>Typed rejection reason surfaced by the service to the controller.</summary>
public enum AttentionCategoryUpdateRejection
{
    /// <summary>The prospective merged map exceeds the cumulative key cap.</summary>
    CumulativeKeyLimitExceeded,

    /// <summary>The prospective serialized JSON exceeds the cumulative byte cap.</summary>
    JsonByteLimitExceeded,
}
