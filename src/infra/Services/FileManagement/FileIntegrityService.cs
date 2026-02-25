using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FileManagement;

/// <summary>
/// Implements file integrity verification with hash and size checking.
/// Thread-safe and reusable across requests.
/// </summary>
public class FileIntegrityService(IFileManagementService fileManagementService, ILogger<FileIntegrityService> logger) : IFileIntegrityService
{
    private readonly IFileManagementService _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
    private readonly ILogger<FileIntegrityService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<bool> FileExistsAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            bool exists = File.Exists(filePath);
            return Task.FromResult(exists);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error checking file existence: {Message}", ex.Message);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> VerifyHashAsync(string filePath, string expectedHash, string algorithm = "sha256", CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            string actualHash = await _fileManagementService.ComputeFileHashAsync(filePath, algorithm, ct);
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error verifying file hash: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<bool> VerifySizeAsync(string filePath, long expectedSizeBytes, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            long? actualSize = await GetFileSizeAsync(filePath, ct);
            return actualSize == expectedSizeBytes;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error verifying file size: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<FileIntegrityCheckResult> VerifyIntegrityAsync(
        string filePath,
        string expectedHash,
        long expectedSizeBytes,
        string algorithm = "sha256",
        CancellationToken ct = default)
    {
        try
        {
            // Check 1: File existence
            if (!File.Exists(filePath))
            {
                string msg = $"File not found: {filePath}";
                _logger.LogWarning(msg);
                return new FileIntegrityCheckResult(
                    IsValid: false,
                    ErrorMessage: msg,
                    FailureReason: "Missing");
            }

            // Check 2: File size
            long? actualSize = await GetFileSizeAsync(filePath, ct);
            if (actualSize != expectedSizeBytes)
            {
                string msg = $"File size mismatch. Expected: {expectedSizeBytes} bytes, Actual: {actualSize} bytes";
                _logger.LogWarning(msg);
                return new FileIntegrityCheckResult(
                    IsValid: false,
                    ErrorMessage: msg,
                    FailureReason: "SizeMismatch");
            }

            // Check 3: File hash
            string actualHash = await _fileManagementService.ComputeFileHashAsync(filePath, algorithm, ct);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                string msg = $"File hash mismatch. Expected: {expectedHash}, Actual: {actualHash}";
                _logger.LogWarning(msg);
                return new FileIntegrityCheckResult(
                    IsValid: false,
                    ErrorMessage: msg,
                    FailureReason: "HashMismatch");
            }

            return new FileIntegrityCheckResult(
                IsValid: true,
                ErrorMessage: null,
                FailureReason: null);
        }
        catch (UnauthorizedAccessException)
        {
            string msg = $"Permission denied accessing file: {filePath}";
            _logger.LogWarning(msg);
            return new FileIntegrityCheckResult(
                IsValid: false,
                ErrorMessage: msg,
                FailureReason: "PermissionDenied");
        }
        catch (Exception ex)
        {
            string msg = $"Unexpected error during integrity check: {ex.Message}";
            _logger.LogError(msg);
            return new FileIntegrityCheckResult(
                IsValid: false,
                ErrorMessage: msg,
                FailureReason: "Unknown");
        }
    }

    public async Task<string?> RecomputeHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default)
    {
        try
        {
            return !File.Exists(filePath) ? null : await _fileManagementService.ComputeFileHashAsync(filePath, algorithm, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error recomputing file hash: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<long?> GetFileSizeAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            System.IO.FileInfo fileInfo = new System.IO.FileInfo(filePath);
            return await Task.FromResult(fileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error getting file size: {Message}", ex.Message);
            return null;
        }
    }
}
