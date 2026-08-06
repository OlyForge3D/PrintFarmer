using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure;

/// <summary>
/// Request to create or update a tag (generic for any object type)
/// </summary>
public class CreateTagDto
{
    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Request to update an existing tag's metadata with optimistic concurrency (#844).
/// <see cref="ExpectedRevision"/> must match the tag's current revision or the update is
/// rejected with a concurrency conflict (HTTP 409).
/// </summary>
public class UpdateTagDto
{
    /// <summary>New tag name (normalized to PascalCase). Optional; unchanged when null.</summary>
    public string? Name { get; set; }

    /// <summary>New hex color for UI display. Optional; unchanged when null.</summary>
    public string? Color { get; set; }

    /// <summary>New description. Optional; unchanged when null.</summary>
    public string? Description { get; set; }

    /// <summary>The revision the caller believes is current; enforced for optimistic concurrency.</summary>
    public long ExpectedRevision { get; set; }
}

/// <summary>
/// Request to assign tags to an object (generic - works for models, gcode files, etc.)
/// </summary>
public class AssignTagsDto
{
    public Guid[] TagIds { get; set; } = [];
}

/// <summary>
/// Tag suggestion for autocomplete with usage count (Phase 3D)
/// </summary>
public class TagSuggestionDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; }

    public int UsageCount { get; set; } // Number of models using this tag

    public bool IsPopular { get; set; } // True if in top N tags
}

/// <summary>
/// Tag usage statistics for analytics (Phase 3D)
/// </summary>
public class TagAnalyticsDto
{
    public int TotalTags { get; set; }

    public int TagsInUse { get; set; } // Tags with at least one model

    public int UnusedTags { get; set; } // Tags with no models

    public int TotalModelTagAssociations { get; set; }

    public double AverageTagsPerModel { get; set; }

    public IReadOnlyList<TagStatDto>? TopTags { get; set; } // Most used tags

    public IReadOnlyList<TagStatDto>? UnusedTagsList { get; set; } // For cleanup suggestions
}

/// <summary>
/// Individual tag statistics (Phase 3D)
/// </summary>
public class TagStatDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int ModelCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// Request to filter models by tags (Phase 3D)
/// </summary>
public class FilterModelsByTagsRequestDto
{
    public Guid[]? IncludeTags { get; set; } // Tags to include

    public Guid[]? ExcludeTags { get; set; } // Tags to exclude

    public bool RequireAllTags { get; set; } = false; // If true, ALL include tags required; if false, ANY
}

/// <summary>
/// Response from filtering models by tags (Phase 3D)
/// </summary>
public class FilterModelsResponseDto
{
    public IReadOnlyList<Guid> ModelIds { get; set; } = [];

    public int Count { get; set; }
}

/// <summary>
/// Request to bulk assign tags to multiple models
/// </summary>
public class BulkAssignTagsDto
{
    public Guid[] ModelIds { get; set; } = [];

    public Guid[] TagIds { get; set; } = [];

    public bool ReplaceExisting { get; set; } = false; // If true, replaces all existing tags
}

/// <summary>
/// Result of bulk operation
/// </summary>
public class BulkOperationResultDto
{
    public int SuccessCount { get; set; }

    public int TotalCount { get; set; }
}

/// <summary>
/// Tags assigned to one object, as returned by the fleet-scoped
/// <c>GET /api/tags/objects</c> read. One entry per requested object ID; objects with no
/// tags still appear with an empty <see cref="Tags"/> list so callers can distinguish
/// "no tags" from "object not found".
/// </summary>
public sealed record ObjectTagsDto(Guid ObjectId, IReadOnlyList<TagDto> Tags);
