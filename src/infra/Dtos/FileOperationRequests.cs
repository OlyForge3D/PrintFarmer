using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

// ===========================================================================
// File and Folder Operation Request DTOs
// ===========================================================================
// Request records for file system operations in the G-code library and
// model management features.
// ===========================================================================

/// <summary>
/// Request to move files to a different folder in the library.
/// </summary>
/// <param name="FilePaths">List of file paths to move.</param>
/// <param name="TargetPath">Destination folder path.</param>
public record MoveFilesRequest(
    [property: JsonPropertyName("filePaths")] IReadOnlyList<string> FilePaths,
    [property: JsonPropertyName("targetPath")] string TargetPath);

/// <summary>
/// Request to move 3D model files by their database IDs.
/// More efficient than path-based moves for bulk operations.
/// </summary>
/// <param name="ModelIds">List of model IDs to move.</param>
/// <param name="TargetDirectoryId">ID of the target directory.</param>
public record MoveModelsRequest(
    [property: JsonPropertyName("modelIds")] IReadOnlyList<string> ModelIds,
    [property: JsonPropertyName("targetDirectoryId")] string TargetDirectoryId);

/// <summary>
/// Request to create a new folder in the models directory structure.
/// </summary>
/// <param name="Path">Relative path for the new folder.</param>
public record CreateFolderRequest(
    [property: JsonPropertyName("path")] string Path);
