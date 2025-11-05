using System.Security.Cryptography;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.FileManagement;

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
    (string storageRoot, string resolvedFullPath, string virtualNormalized) ResolveVirtualPath(
        string? virtualPath,
        string storageRoot);

    /// <summary>
    /// Sanitizes a filename by removing invalid characters and ensuring proper extension.
    /// </summary>
    string SanitizeFileName(string originalName, string extension);

    /// <summary>
    /// Resolves a unique filename by checking for collisions and appending numbers if needed.
    /// </summary>
    string ResolveUniqueFileName(string targetDirectory, string proposedName);

    /// <summary>
    /// Computes file hash using specified algorithm (sha256 or sha1).
    /// </summary>
    Task<string> ComputeFileHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Converts byte array to hexadecimal string.
    /// </summary>
    string ToHex(byte[] bytes);

    /// <summary>
    /// Generates ETag header value from file info.
    /// </summary>
    string GenerateETag(System.IO.FileInfo info, bool weak = false);

    /// <summary>
    /// Validates a file path to prevent directory traversal attacks.
    /// Returns true if the candidate path is within the root directory.
    /// </summary>
    bool IsSafePath(string candidatePath, string rootDirectory);

    /// <summary>
    /// Determines file format enum from file extension.
    /// </summary>
    ModelFileFormat GetModelFileFormat(string fileExtension);

    /// <summary>
    /// Converts model file format enum back to file extension string.
    /// </summary>
    string GetModelFileFormatString(ModelFileFormat format);

    /// <summary>
    /// Gets collection of allowed model file extensions.
    /// </summary>
    IReadOnlyCollection<string> GetAllowedModelExtensions();

    /// <summary>
    /// Validates if a file extension is allowed for models.
    /// Throws ArgumentException if invalid.
    /// </summary>
    void ValidateModelExtension(string fileExtension);
}
