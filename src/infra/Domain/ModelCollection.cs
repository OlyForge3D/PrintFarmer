namespace Farm.Infrastructure.Domain;

/// <summary>
/// A user-owned collection (album) that groups 3D models together for sync and
/// organization. Model references are cross-context soft references to
/// <c>Farm.Slicer.Module.Domain.Model3D</c> (no EF foreign key), following the
/// same context-boundary precedent as <see cref="Tag"/> / <see cref="Model3DTagMapping"/>.
/// </summary>
/// <remarks>
/// Contracts are intentionally additive so downstream sync issues (#844 journaling,
/// #845 cursor sync) can extend this entity — e.g. with revision/journal columns —
/// without breaking existing consumers. <see cref="UpdatedAt"/> is indexed to support
/// cursor-based incremental sync.
/// </remarks>
public class ModelCollection
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of the collection's contents or purpose.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Soft reference to the owning user (no FK constraint). Owners and administrators
    /// may read and mutate the collection; shared collections are readable by any user.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// When true, the collection is readable by all authenticated users (share);
    /// when false it is private to the owner and administrators (unshare).
    /// </summary>
    public bool IsShared { get; set; }

    /// <summary>UTC timestamp when the collection was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last mutation (metadata or membership change).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Membership rows linking this collection to model references.</summary>
    public ICollection<ModelCollectionMembership> Memberships { get; set; } = new List<ModelCollectionMembership>();
}
