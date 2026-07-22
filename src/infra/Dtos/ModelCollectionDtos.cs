using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Represents a model collection returned by the API. Property names serialize to
/// camelCase to match the React client contract.
/// </summary>
public class ModelCollectionDto
{
    /// <summary>Collection identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Collection name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Soft reference to the owning user.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Whether the collection is shared (readable by all authenticated users).</summary>
    public bool IsShared { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Number of models in the collection.</summary>
    public int MemberCount { get; set; }

    /// <summary>Ordered list of model ids in the collection (oldest membership first).</summary>
    public IReadOnlyList<Guid> ModelIds { get; set; } = [];
}

/// <summary>
/// Represents a single collection membership row.
/// </summary>
public class ModelCollectionMembershipDto
{
    /// <summary>Membership identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning collection identifier.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Soft reference to the model.</summary>
    public Guid ModelId { get; set; }

    /// <summary>UTC timestamp when the model was added.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request payload to create a new collection.
/// </summary>
public class CreateModelCollectionDto
{
    /// <summary>Collection name (required).</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    [StringLength(2000)]
    public string? Description { get; set; }
}

/// <summary>
/// Request payload to update an existing collection's metadata.
/// </summary>
public class UpdateModelCollectionDto
{
    /// <summary>Collection name (required).</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    [StringLength(2000)]
    public string? Description { get; set; }
}

/// <summary>
/// Request payload to add a single model to a collection.
/// </summary>
public class AddModelCollectionMemberDto
{
    /// <summary>Model id to add.</summary>
    [Required]
    public Guid ModelId { get; set; }
}

/// <summary>
/// Request payload to replace a collection's entire membership set.
/// </summary>
public class ReplaceModelCollectionMembersDto
{
    /// <summary>Model ids that should constitute the collection after the operation.</summary>
    public Guid[] ModelIds { get; set; } = [];
}
