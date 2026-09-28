using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Identifies who is reading slicer profiles so every read projection applies the same visibility
/// rule (issue #3174): system and public profiles are visible to everyone, private profiles only
/// to their owner, and administrators see everything.
/// </summary>
/// <param name="UserId">
/// The caller's verified user id, or <see langword="null"/> when the caller has no valid identity
/// claim. A <see langword="null"/> id never matches an owner, so such a caller sees only system and
/// public profiles (fail closed).
/// </param>
/// <param name="IsAdmin">
/// <see langword="true"/> for callers authorized to see every profile (<c>farm_admin</c> or
/// <c>slicer_engines:admin</c>).
/// </param>
public sealed record ProfileViewer(Guid? UserId, bool IsAdmin)
{
    /// <summary>A viewer that can see every profile. Use for administrative or internal callers.</summary>
    public static ProfileViewer Administrator { get; } = new(null, true);

    /// <summary>Returns whether this viewer may see a profile with the given visibility attributes.</summary>
    /// <param name="isSystem">Whether the profile is a system profile.</param>
    /// <param name="isPublic">Whether the profile is shared publicly.</param>
    /// <param name="ownerUserId">The profile owner's user id, if any.</param>
    public bool CanView(bool isSystem, bool isPublic, Guid? ownerUserId) =>
        IsAdmin || isSystem || isPublic || (UserId.HasValue && ownerUserId == UserId);

    /// <summary>Returns whether this viewer may see the given process profile.</summary>
    /// <param name="profile">The process profile.</param>
    public bool CanView(ProcessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CanView(profile.IsSystem, profile.IsPublic, profile.CreatedByUserId);
    }
}
