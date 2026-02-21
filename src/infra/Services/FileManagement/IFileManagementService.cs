using System.Security.Cryptography;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.FileManagement;

/// <summary>
/// Unified file management operations service for path resolution, sanitization, hashing, and utility operations.
/// Eliminates duplication between controller and service layers.
/// </summary>
public interface IFileManagementService
{
    /// <summary>
    /// Resolves and validates virtual paths to prevent directory traversal attacks.
    /// Returns storage root, resolved full path, and normalized virtual path.
    /// </summary>
    /// <param name="virtualPath">The virtual path to resolve</param>
    /// <param name="storageRoot">The storage root directory</param>
    (string StorageRoot, string ResolvedFullPath, string VirtualNormalized) ResolveVirtualPath(
        string? virtualPath,
        string storageRoot);

    /// <summary>
    /// Sanitizes a filename by removing invalid characters and ensuring proper extension.
    /// </summary>
    /// <param name="originalName">The original filename to sanitize</param>
    /// <param name="extension">The file extension to ensure</param>
    string SanitizeFileName(string originalName, string extension);

    /// <summary>
    /// Resolves a unique filename by checking for collisions and appending numbers if needed.
    /// </summary>
    /// <param name="targetDirectory">The target directory to check for collisions</param>
    /// <param name="proposedName">The proposed filename</param>
    string ResolveUniqueFileName(string targetDirectory, string proposedName);

    /// <summary>
    /// Computes file hash using specified algorithm (sha256 or sha1).
    /// </summary>
    /// <param name="filePath">The path to the file to hash</param>
    /// <param name="algorithm">The hash algorithm to use (sha256 or sha1)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<string> ComputeFileHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Converts byte array to hexadecimal string.
    /// </summary>
    /// <param name="bytes">The byte array to convert</param>
    string ToHex(byte[] bytes);

    /// <summary>
    /// Generates ETag header value from file info.
    /// </summary>
    /// <param name="info">The file info to generate ETag from</param>
    /// <param name="weak">Whether to generate a weak ETag</param>
    string GenerateETag(System.IO.FileInfo info, bool weak = false);

    /// <summary>
    /// Validates a file path to prevent directory traversal attacks.
    /// Returns true if the candidate path is within the root directory.
    /// </summary>
    /// <param name="candidatePath">The candidate path to validate</param>
    /// <param name="rootDirectory">The root directory to check against</param>
    bool IsSafePath(string candidatePath, string rootDirectory);

    /// <summary>
    /// Determines file format enum from file extension.
    /// </summary>
    /// <param name="fileExtension">The file extension to check</param>
    ModelFileFormat GetModelFileFormat(string fileExtension);

    /// <summary>
    /// Converts model file format enum back to file extension string.
    /// </summary>
    /// <param name="format">The model file format enum value</param>
    string GetModelFileFormatString(ModelFileFormat format);

    /// <summary>
    /// Gets collection of allowed model file extensions.
    /// </summary>
    IReadOnlyCollection<string> GetAllowedModelExtensions();

    /// <summary>
    /// Validates if a file extension is allowed for models.
    /// Throws ArgumentException if invalid.
    /// </summary>
    /// <param name="fileExtension">The file extension to validate</param>
    void ValidateModelExtension(string fileExtension);
}
