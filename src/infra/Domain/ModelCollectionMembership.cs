namespace Farm.Infrastructure.Domain;

/// <summary>
/// Join row linking a 3D model to a <see cref="ModelCollection"/>. The <see cref="ModelId"/> is a
/// cross-context reference with no EF foreign key (the model lives in the slicer module context);
/// its existence is validated via the model query abstraction, matching the tag boundary precedent.
/// </summary>
public class ModelCollectionMembership
{
    /// <summary>Stable unique identifier for the membership row.</summary>
    public Guid Id { get; set; }

    /// <summary>Identifier of the owning <see cref="ModelCollection"/>.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Cross-context identifier of the member model (no EF foreign key).</summary>
    public Guid ModelId { get; set; }

    /// <summary>UTC timestamp when the model was added to the collection.</summary>
    public DateTime AddedAt { get; set; }

    /// <summary>Navigation to the owning collection.</summary>
    public ModelCollection? Collection { get; set; }
}
