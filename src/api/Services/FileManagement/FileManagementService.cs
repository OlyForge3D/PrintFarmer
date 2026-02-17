using System.Security.Cryptography;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Implementation of unified file management operations.
/// Provides centralized file handling for path validation, sanitization, hashing, and utilities.
/// </summary>
public sealed class FileManagementService : IFileManagementService
{
    // Allowed model file extensions (centralized source of truth)
    private static readonly IReadOnlyCollection<string> AllowedModelExtensions = new[] { ".stl", ".3mf", ".obj", ".ply", ".step" }.AsReadOnly();

    public (string StorageRoot, string ResolvedFullPath, string VirtualNormalized) ResolveVirtualPath(
        string? virtualPath,
        string storageRoot)
    {
        // Normalize incoming virtual path
        string vPath = string.IsNullOrWhiteSpace(virtualPath) ? "/" : virtualPath.Trim();
        if (!vPath.StartsWith('/'))
        {
            vPath = "/" + vPath;
        }

        // Collapse .. segments and remove . segments
        string[] segments = vPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "." && s != "..")
            .ToArray();

        string safeRel = segments.Length == 0 ? string.Empty : Path.Combine(segments);
        string candidate = Path.GetFullPath(Path.Combine(storageRoot, safeRel));

        // Security check: ensure path doesn't escape the storage root
        if (!candidate.StartsWith(storageRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path escapes storage root");
        }

        string virtualNormalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments);

        return (storageRoot, candidate, virtualNormalized);
    }

    public string SanitizeFileName(string originalName, string extension)
    {
        string safeName = originalName;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }

        if (!safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            safeName += extension;
        }

        return safeName;
    }

    public string ResolveUniqueFileName(string targetDirectory, string proposedName)
    {
        string fullPath = Path.GetFullPath(Path.Combine(targetDirectory, proposedName));

        if (!System.IO.File.Exists(fullPath))
        {
            return proposedName;
        }

        // Collision detected - append counter
        string ext = Path.GetExtension(proposedName);
        string baseName = Path.GetFileNameWithoutExtension(proposedName);
        int counter = 1;
        string candidate;

        do
        {
            candidate = $"{baseName} ({counter++}){ext}";
            fullPath = Path.GetFullPath(Path.Combine(targetDirectory, candidate));
        }
        while (System.IO.File.Exists(fullPath));

        return candidate;
    }

    public async Task<string> ComputeFileHashAsync(string filePath, string algorithm = "sha256", CancellationToken ct = default)
    {
        if (!System.IO.File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        algorithm = algorithm.Trim().ToLowerInvariant();
        if (algorithm != "sha256" && algorithm != "sha1")
        {
            throw new ArgumentException($"Unsupported hash algorithm: {algorithm}. Allowed: sha256, sha1");
        }

        HashAlgorithmName hashAlgorithm = algorithm == "sha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;

        using IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(hashAlgorithm);
        using FileStream fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, bufferSize: 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(new Memory<byte>(buffer), ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        byte[] hashBytes = hash.GetHashAndReset();
        return ToHex(hashBytes);
    }

    public string ToHex(byte[] bytes)
    {
        char[] c = new char[bytes.Length * 2];
        int i = 0;
        foreach (byte b in bytes)
        {
            c[i++] = (char)(b >> 4 < 10 ? '0' + (b >> 4) : 'a' + (b >> 4) - 10);
            c[i++] = (char)((b & 0xF) < 10 ? '0' + (b & 0xF) : 'a' + (b & 0xF) - 10);
        }

        return new string(c);
    }

    public string GenerateETag(System.IO.FileInfo info, bool weak = false)
    {
        string core = $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";
        return weak ? $"W/\"{core}\"" : $"\"{core}\"";
    }

    public bool IsSafePath(string candidatePath, string rootDirectory)
    {
        try
        {
            string fullRoot = System.IO.Path.GetFullPath(rootDirectory);
            string fullCandidate = System.IO.Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public ModelFileFormat GetModelFileFormat(string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            return ModelFileFormat.STL;
        }

        string ext = fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;

        return ext.ToLowerInvariant() switch
        {
            ".stl" => ModelFileFormat.STL,
            ".3mf" => ModelFileFormat.TMF,
            ".obj" => ModelFileFormat.OBJ,
            ".ply" => ModelFileFormat.PLY,
            ".step" => ModelFileFormat.STEP,
            _ => ModelFileFormat.STL
        };
    }

    public string GetModelFileFormatString(ModelFileFormat format)
    {
        return format switch
        {
            ModelFileFormat.STL => "stl",
            ModelFileFormat.TMF => "3mf",
            ModelFileFormat.OBJ => "obj",
            ModelFileFormat.PLY => "ply",
            ModelFileFormat.STEP => "step",
            _ => "stl"
        };
    }

    public IReadOnlyCollection<string> GetAllowedModelExtensions()
    {
        return AllowedModelExtensions;
    }

    public void ValidateModelExtension(string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            throw new ArgumentException("File extension is required", nameof(fileExtension));
        }

        string ext = fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;

        if (!AllowedModelExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid file type '{ext}'. Allowed types: {string.Join(", ", AllowedModelExtensions)}",
                nameof(fileExtension));
        }
    }
}
