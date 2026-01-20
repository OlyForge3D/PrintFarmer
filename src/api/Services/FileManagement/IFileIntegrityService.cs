using System;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Verifies file integrity by checking existence, hash consistency, and size validation.
/// Used before critical operations like downloading, processing, or printing files.
/// </summary>
public interface IFileIntegrityService
{
    /// <summary>
    /// Verifies that a file exists at the given path.
    /// </summary>
    /// <param name="filePath">The path to the file to check.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task<bool> FileExistsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Computes and verifies file hash matches the expected value.
    /// Returns false if file doesn't exist or hash mismatches.
    /// </summary>
    /// <param name="filePath">The path to the file to verify.</param>
    /// <param name="expectedHash">The expected hash value to compare against.</param>
    /// <param name="algorithm">The hash algorithm to use (default: sha256).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task<bool> VerifyHashAsync(string filePath, string expectedHash, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Verifies file size matches the expected value.
    /// Returns false if file doesn't exist or size mismatches.
    /// </summary>
    /// <param name="filePath">The path to the file to verify.</param>
    /// <param name="expectedSizeBytes">The expected file size in bytes.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task<bool> VerifySizeAsync(string filePath, long expectedSizeBytes, CancellationToken ct = default);

    /// <summary>
    /// Comprehensive integrity check: existence + hash + size.
    /// Returns detailed result with specific failure reason.
    /// Safe to call before critical operations.
    /// </summary>
    /// <param name="filePath">The path to the file to verify.</param>
    /// <param name="expectedHash">The expected hash value to compare against.</param>
    /// <param name="expectedSizeBytes">The expected file size in bytes.</param>
    /// <param name="algorithm">The hash algorithm to use (default: sha256).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
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
    /// <param name="filePath">The path to the file to hash.</param>
    /// <param name="algorithm">The hash algorithm to use (default: sha256).</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task<string?> RecomputeHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default);

    /// <summary>
    /// Gets current file size.
    /// Returns null if file doesn't exist.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    Task<long?> GetFileSizeAsync(string filePath, CancellationToken ct = default);
}
