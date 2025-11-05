using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Represents the result of a file integrity check.
/// </summary>
public record FileIntegrityCheckResult(
    bool IsValid,
    string? ErrorMessage,
    string? FailureReason  // "Missing", "HashMismatch", "SizeMismatch", "PermissionDenied", "Unknown"
);

/// <summary>
/// Verifies file integrity by checking existence, hash consistency, and size validation.
/// Used before critical operations like downloading, processing, or printing files.
/// </summary>
public interface IFileIntegrityService
{
    /// <summary>
    /// Verifies that a file exists at the given path.
    /// </summary>
    Task<bool> FileExistsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Computes and verifies file hash matches the expected value.
    /// Returns false if file doesn't exist or hash mismatches.
    /// </summary>
    Task<bool> VerifyHashAsync(string filePath, string expectedHash, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Verifies file size matches the expected value.
    /// Returns false if file doesn't exist or size mismatches.
    /// </summary>
    Task<bool> VerifySizeAsync(string filePath, long expectedSizeBytes, CancellationToken ct = default);

    /// <summary>
    /// Comprehensive integrity check: existence + hash + size.
    /// Returns detailed result with specific failure reason.
    /// Safe to call before critical operations.
    /// </summary>
    Task<FileIntegrityCheckResult> VerifyIntegrityAsync(
        string filePath,
        string expectedHash,
        long expectedSizeBytes,
        string algorithm = "sha256",
        CancellationToken ct = default);

    /// <summary>
    /// Recomputes file hash to detect corruption.
    /// Returns the computed hash or null if file doesn't exist.
    /// </summary>
    Task<string?> RecomputeHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Gets current file size.
    /// Returns null if file doesn't exist.
    /// </summary>
    Task<long?> GetFileSizeAsync(string filePath, CancellationToken ct = default);
}
