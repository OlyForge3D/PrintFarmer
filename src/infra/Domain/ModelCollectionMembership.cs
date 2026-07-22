namespace Farm.Infrastructure.Domain;

/// <summary>
/// Join row linking a <see cref="ModelCollection"/> to a single 3D model reference.
/// The model reference is a cross-context soft reference to
/// <c>Farm.Slicer.Module.Domain.Model3D</c> (no EF foreign key); existence is validated
/// through <see cref="Farm.Infrastructure.Services.IModel3DQueryProvider"/> at the service
/// layer, mirroring the tag/context-boundary precedent.
/// </summary>
public class ModelCollectionMembership
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning collection identifier (FK to <see cref="ModelCollection"/>).</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Navigation to the owning collection.</summary>
    public ModelCollection? Collection { get; set; }

    /// <summary>Soft reference to the Model3D identifier (no FK constraint).</summary>
    public Guid ModelId { get; set; }

    /// <summary>UTC timestamp when the model was added to the collection.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last mutation to this membership row.</summary>
    public DateTime UpdatedAt { get; set; }
}
