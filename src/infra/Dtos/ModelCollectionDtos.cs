using System.Collections.Generic;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Representation of a <see cref="ModelCollection"/> returned by the API. Property names serialize
/// as camelCase and <see cref="Visibility"/> serializes as a string enum member.
/// </summary>
public class ModelCollectionDto
{
    /// <summary>Stable unique identifier of the collection.</summary>
    public Guid Id { get; set; }

    /// <summary>Identifier of the user that owns the collection.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Human-readable collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>Current visibility of the collection.</summary>
    public ModelCollectionVisibility Visibility { get; set; }

    /// <summary>Number of models in the collection.</summary>
    public int MemberCount { get; set; }

    /// <summary>UTC timestamp when the collection was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent mutation.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single model membership within a collection.
/// </summary>
public class ModelCollectionMembershipDto
{
    /// <summary>Stable unique identifier of the membership row.</summary>
    public Guid Id { get; set; }

    /// <summary>Identifier of the owning collection.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Cross-context identifier of the member model.</summary>
    public Guid ModelId { get; set; }

    /// <summary>UTC timestamp when the model was added to the collection.</summary>
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// Request to create a new collection. The owner is derived from the authenticated caller.
/// </summary>
public class CreateModelCollectionDto
{
    /// <summary>Human-readable collection name (required).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>Optional initial visibility. Defaults to <see cref="ModelCollectionVisibility.Private"/>.</summary>
    public ModelCollectionVisibility Visibility { get; set; } = ModelCollectionVisibility.Private;
}

/// <summary>
/// Request to update a collection's mutable metadata.
/// </summary>
public class UpdateModelCollectionDto
{
    /// <summary>New collection name (required).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>New description, or null to clear it.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request to add a single model to a collection.
/// </summary>
public class AddModelCollectionMemberDto
{
    /// <summary>Cross-context identifier of the model to add.</summary>
    public Guid ModelId { get; set; }
}

/// <summary>
/// Request to replace the full membership of a collection with the supplied set of models.
/// </summary>
public class ReplaceModelCollectionMembersDto
{
    /// <summary>The complete desired set of model identifiers for the collection.</summary>
    public Guid[] ModelIds { get; set; } = [];
}
