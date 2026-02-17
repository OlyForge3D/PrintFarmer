namespace Farm.Infrastructure.Domain;

/// <summary>
/// Explicit join table entity mapping for the Model3D-to-Tag many-to-many relationship.
/// Maps to the existing <c>Model3DTag</c> table created by the original skip-navigation
/// configuration. Required because Model3D has been migrated to
/// <c>Farm.Slicer.Module.Domain</c> and no longer carries a <c>Tags</c> navigation property.
/// </summary>
public class Model3DTagMapping
{
    /// <summary>The Model3D identifier (FK to Model3D table).</summary>
    public Guid Model3DId { get; set; }

    /// <summary>The Tag identifier (FK to Tags table).</summary>
    public Guid TagsId { get; set; }

    /// <summary>Navigation to the associated <see cref="Tag"/>.</summary>
    public Tag? Tag { get; set; }
}
