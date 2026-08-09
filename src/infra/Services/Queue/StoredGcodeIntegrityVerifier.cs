// <copyright file="StoredGcodeIntegrityVerifier.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Security.Cryptography;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Result of verifying the exact stored G-code bytes that would be opened for dispatch.
/// </summary>
public sealed record StoredGcodeIntegrityResult(
    bool Success,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static StoredGcodeIntegrityResult Valid() => new(true, null, null);

    public static StoredGcodeIntegrityResult Invalid(string code, string detail) =>
        new(false, code, detail);
}

/// <summary>
/// Verifies immutable G-code bytes against the digest and size pinned on a queue job.
/// </summary>
public interface IStoredGcodeIntegrityVerifier
{
    Task<StoredGcodeIntegrityResult> VerifyAsync(
        GcodeFile file,
        string expectedSha256,
        long? expectedSizeBytes,
        CancellationToken ct = default);
}

/// <summary>
/// Streams the exact library file through SHA-256 without buffering it in memory.
/// </summary>
public sealed class StoredGcodeIntegrityVerifier(
    IStoragePathService storagePaths) : IStoredGcodeIntegrityVerifier
{
    private readonly IStoragePathService _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    public async Task<StoredGcodeIntegrityResult> VerifyAsync(
        GcodeFile file,
        string expectedSha256,
        long? expectedSizeBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_hash_missing",
                "The queue job does not pin a G-code content digest.");
        }

        string root = Path.GetFullPath(_storagePaths.GetGcodeStorageDirectory());
        string relativeDirectory = (file.FilePath ?? string.Empty)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Join(root, relativeDirectory, file.FileName));
        string rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : $"{root}{Path.DirectorySeparatorChar}";
        if (!fullPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_storage_path_invalid",
                "The stored G-code path is outside the configured library root.");
        }

        try
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);

            if (expectedSizeBytes.HasValue && stream.Length != expectedSizeBytes.Value)
            {
                return StoredGcodeIntegrityResult.Invalid(
                    "gcode_size_mismatch",
                    "The stored G-code byte count changed after the job was queued.");
            }

            byte[] digest = await SHA256.HashDataAsync(stream, ct);
            string actual = Convert.ToHexString(digest);
            if (!string.Equals(
                    actual,
                    expectedSha256.Replace("-", string.Empty, StringComparison.Ordinal),
                    StringComparison.OrdinalIgnoreCase))
            {
                return StoredGcodeIntegrityResult.Invalid(
                    "gcode_byte_hash_mismatch",
                    "The exact stored G-code bytes do not match the digest pinned on the queue job.");
            }

            return StoredGcodeIntegrityResult.Valid();
        }
        catch (IOException)
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_bytes_unavailable",
                "The exact stored G-code bytes could not be opened for verification.");
        }
        catch (UnauthorizedAccessException)
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_bytes_unavailable",
                "The exact stored G-code bytes could not be opened for verification.");
        }
    }

    /// <summary>
    /// Verifies an already-opened seekable stream and rewinds it for immediate upload.
    /// The caller must keep this same stream open through adapter consumption.
    /// </summary>
    public static async Task<StoredGcodeIntegrityResult> VerifyOpenedStreamAsync(
        Stream stream,
        string expectedSha256,
        long? expectedSizeBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_bytes_unavailable",
                "The exact upload stream is not readable and seekable.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_hash_missing",
                "The queue job does not pin a G-code content digest.");
        }

        stream.Position = 0;
        if (expectedSizeBytes.HasValue && stream.Length != expectedSizeBytes.Value)
        {
            return StoredGcodeIntegrityResult.Invalid(
                "gcode_size_mismatch",
                "The opened upload stream byte count changed after the job was queued.");
        }

        byte[] digest = await SHA256.HashDataAsync(stream, ct);
        stream.Position = 0;
        string actual = Convert.ToHexString(digest);
        return string.Equals(
            actual,
            expectedSha256.Replace("-", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase)
            ? StoredGcodeIntegrityResult.Valid()
            : StoredGcodeIntegrityResult.Invalid(
                "gcode_byte_hash_mismatch",
                "The exact opened upload stream does not match the digest pinned on the queue job.");
    }
}
