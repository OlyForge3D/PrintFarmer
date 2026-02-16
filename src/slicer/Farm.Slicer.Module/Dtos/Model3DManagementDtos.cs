namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Request DTO for searching and filtering 3D model files.
/// </summary>
public class Model3DSearchRequestDto
{
    /// <summary>Optional directory path filter.</summary>
    public string? Path { get; set; }

    /// <summary>Sort field: "name", "size", or "date".</summary>
    public string? SortBy { get; set; }

    /// <summary>Sort order: "asc" or "desc".</summary>
    public string? SortOrder { get; set; }

    /// <summary>Optional search query for file name.</summary>
    public string? Search { get; set; }

    /// <summary>Page number (1-based).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Optional tag IDs for filtering (AND logic).</summary>
    public Guid[]? TagIds { get; set; }
}

/// <summary>
/// Request to update a 3D model's metadata.
/// </summary>
public class UpdateModel3DDto
{
    /// <summary>Updated display name.</summary>
    public string? Name { get; set; }

    /// <summary>Updated description.</summary>
    public string? Description { get; set; }

    /// <summary>Updated tag IDs.</summary>
    public List<Guid>? TagIds { get; set; }
}

/// <summary>
/// Request to delete multiple 3D models by their IDs.
/// </summary>
public class DeleteModelsRequest
{
    /// <summary>List of model IDs to delete.</summary>
    public List<Guid> Ids { get; set; } = [];
}

/// <summary>
/// Request to create a virtual folder for 3D models.
/// </summary>
public class CreateFolderRequest
{
    /// <summary>The virtual path for the new folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The folder type: "models" or "gcode".</summary>
    public string FolderType { get; set; } = "models";
}

/// <summary>
/// Request to move 3D models to a different folder.
/// </summary>
public class MoveModelsRequest
{
    /// <summary>List of model IDs to move.</summary>
    public List<Guid> Ids { get; set; } = [];

    /// <summary>Virtual path of the destination folder.</summary>
    public string TargetFolderPath { get; set; } = string.Empty;
}

/// <summary>
/// Result DTO for folder operations (create, move).
/// </summary>
public class FolderOperationResultDto
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>The folder ID created or used.</summary>
    public Guid? FolderId { get; set; }

    /// <summary>Number of items affected.</summary>
    public int AffectedCount { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }
}
