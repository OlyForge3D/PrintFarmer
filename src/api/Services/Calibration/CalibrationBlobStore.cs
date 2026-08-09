using System.Security.Cryptography;
using Farm.Infrastructure.IO;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>Configuration limits for private calibration-photo storage.</summary>
public sealed class CalibrationBlobStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Calibration:BlobStorage";

    /// <summary>Private root. It must not be exposed through a static-file route.</summary>
    public string RootPath { get; init; } = Path.Join(AppContext.BaseDirectory, "calibration-blobs");

    /// <summary>Maximum accepted source upload size.</summary>
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Maximum decoded image width.</summary>
    public int MaxWidth { get; init; } = 8_000;

    /// <summary>Maximum decoded image height.</summary>
    public int MaxHeight { get; init; } = 8_000;

    /// <summary>Maximum decoded image pixels.</summary>
    public long MaxPixels { get; init; } = 32_000_000;
}

/// <summary>Private storage metadata produced by server-side image inspection.</summary>
public sealed record CalibrationBlobMetadata(
    string StorageKey,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int Width,
    int Height,
    string? SourceSha256 = null);

/// <summary>Input needed to create an opaque, owner-scoped photo object.</summary>
public sealed record CalibrationBlobWriteRequest(
    Guid OwnerUserId,
    Guid ProjectId,
    Guid AttemptId,
    Guid PhotoId,
    string OriginalFileName,
    string DeclaredContentType);

/// <summary>Thrown when a caller provides invalid or unsafe image content.</summary>
public sealed class CalibrationBlobValidationException : InvalidOperationException
{
    /// <summary>Creates an exception with the generic safe validation code.</summary>
    public CalibrationBlobValidationException()
        : this("photo_invalid", "The calibration photo is invalid.")
    {
    }

    /// <summary>Creates an exception with the generic safe validation code.</summary>
    public CalibrationBlobValidationException(string message)
        : this("photo_invalid", message)
    {
    }

    /// <summary>Creates an exception with the generic safe validation code and cause.</summary>
    public CalibrationBlobValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "photo_invalid";
    }

    /// <summary>Creates an exception with a stable safe validation code.</summary>
    public CalibrationBlobValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable validation code safe to return in an API problem response.</summary>
    public string Code { get; }
}

/// <summary>
/// Stores private calibration photos. Callers must authorize before passing an
/// opaque key to this infrastructure boundary; the store itself never returns a
/// local path, URL, or caller-controlled filename.
/// </summary>
public interface ICalibrationBlobStore
{
    /// <summary>Validates, metadata-strips, and writes an image under a generated opaque key.</summary>
    Task<CalibrationBlobMetadata> PutAsync(
        CalibrationBlobWriteRequest request,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>Opens an already-authorized private object.</summary>
    Task<Stream> OpenReadAsync(string opaqueStorageKey, CancellationToken cancellationToken);

    /// <summary>Gets metadata for an already-authorized private object.</summary>
    Task<CalibrationBlobMetadata?> GetMetadataAsync(
        string opaqueStorageKey,
        CancellationToken cancellationToken);

    /// <summary>Deletes an already-authorized private object.</summary>
    Task DeleteAsync(string opaqueStorageKey, CancellationToken cancellationToken);

    /// <summary>Checks for an already-authorized private object.</summary>
    Task<bool> ExistsAsync(string opaqueStorageKey, CancellationToken cancellationToken);
}

/// <summary>
/// Local implementation over <see cref="IFileSystem"/>. It writes only generated
/// keys beneath a private root and re-encodes images to remove EXIF/GPS metadata.
/// </summary>
public sealed class CalibrationBlobStore(
    IOptions<CalibrationBlobStorageOptions> options,
    IFileSystem fileSystem)
    : ICalibrationBlobStore
{
    private readonly CalibrationBlobStorageOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly string _rootPath = GetRootPath(options?.Value);
    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public async Task<CalibrationBlobMetadata> PutAsync(
        CalibrationBlobWriteRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRequest(request);
        string temporaryKey = $"temporary/{Guid.NewGuid():N}.upload";
        string temporaryPath = GetPath(temporaryKey);
        string? finalPath = null;
        try
        {
            EnsureDirectory(temporaryPath);
            string sourceSha256 = await WriteTemporaryAsync(content, temporaryPath, cancellationToken);

            ImageInspection inspection = await InspectAsync(temporaryPath, cancellationToken);
            string extension = GetExtension(inspection.ContentType);
            string opaqueKey =
                $"calibration/{request.OwnerUserId:N}/{request.ProjectId:N}/" +
                $"{request.AttemptId:N}/{request.PhotoId:N}.{extension}";
            finalPath = GetPath(opaqueKey);
            EnsureDirectory(finalPath);
            await ReencodeWithoutMetadataAsync(
                temporaryPath,
                finalPath,
                inspection.ContentType,
                cancellationToken);

            CalibrationBlobMetadata persisted = await ReadMetadataAsync(
                opaqueKey,
                inspection.ContentType,
                inspection.Width,
                inspection.Height,
                cancellationToken);
            return persisted with { SourceSha256 = sourceSha256 };
        }
        catch
        {
            if (finalPath is not null && _fileSystem.FileExists(finalPath))
            {
                _fileSystem.DeleteFile(finalPath);
            }

            throw;
        }
        finally
        {
            if (_fileSystem.FileExists(temporaryPath))
            {
                _fileSystem.DeleteFile(temporaryPath);
            }
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string opaqueStorageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(opaqueStorageKey);
        if (!_fileSystem.FileExists(path))
        {
            throw new FileNotFoundException("The requested calibration photo does not exist.");
        }

        return Task.FromResult(_fileSystem.OpenRead(path));
    }

    /// <inheritdoc />
    public async Task<CalibrationBlobMetadata?> GetMetadataAsync(
        string opaqueStorageKey,
        CancellationToken cancellationToken)
    {
        string path = GetPath(opaqueStorageKey);
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        ImageInspection inspection = await InspectAsync(path, cancellationToken);
        return await ReadMetadataAsync(
            opaqueStorageKey,
            inspection.ContentType,
            inspection.Width,
            inspection.Height,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string opaqueStorageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(opaqueStorageKey);
        if (_fileSystem.FileExists(path))
        {
            _fileSystem.DeleteFile(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string opaqueStorageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_fileSystem.FileExists(GetPath(opaqueStorageKey)));
    }

    private static string GetRootPath(CalibrationBlobStorageOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            throw new InvalidOperationException(
                "Calibration blob storage requires a private root path.");
        }

        if (options.MaxBytes <= 0 || options.MaxWidth <= 0 || options.MaxHeight <= 0 ||
            options.MaxPixels <= 0)
        {
            throw new InvalidOperationException(
                "Calibration blob storage limits must be positive.");
        }

        return Path.GetFullPath(options.RootPath);
    }

    private static void ValidateRequest(CalibrationBlobWriteRequest request)
    {
        if (request.OwnerUserId == Guid.Empty || request.ProjectId == Guid.Empty ||
            request.AttemptId == Guid.Empty || request.PhotoId == Guid.Empty)
        {
            throw new ArgumentException(
                "Photo storage requests require stable owner, project, attempt, and photo identifiers.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OriginalFileName) ||
            Path.GetFileName(request.OriginalFileName) != request.OriginalFileName)
        {
            throw new CalibrationBlobValidationException(
                "photo_filename_invalid",
                "The photo filename is invalid.");
        }
    }

    private async Task<string> WriteTemporaryAsync(
        Stream source,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81_920];
        long total = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream destination = _fileSystem.OpenWrite(temporaryPath);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > _options.MaxBytes)
            {
                throw new CalibrationBlobValidationException(
                    "photo_too_large",
                    "The photo exceeds the configured byte limit.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
        {
            throw new CalibrationBlobValidationException(
                "photo_empty",
                "The photo contains no image data.");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<ImageInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        await using Stream source = _fileSystem.OpenRead(path);
        string contentType = await DetectContentTypeAsync(source, cancellationToken);
        source.Position = 0;
        try
        {
            var info = await Image.IdentifyAsync(source, cancellationToken);
            if (info is null)
            {
                throw new CalibrationBlobValidationException(
                    "photo_decode_invalid",
                    "The photo cannot be decoded as a supported image.");
            }

            ValidateDimensions(info.Width, info.Height);
            return new(contentType, info.Width, info.Height);
        }
        catch (Exception exception) when (
            exception is InvalidImageContentException or UnknownImageFormatException)
        {
            throw new CalibrationBlobValidationException(
                "photo_decode_invalid",
                "The photo cannot be decoded as a supported image.");
        }
    }

    private async Task ReencodeWithoutMetadataAsync(
        string sourcePath,
        string destinationPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        await using Stream source = _fileSystem.OpenRead(sourcePath);
        using Image image = await Image.LoadAsync(source, cancellationToken);
        ValidateDimensions(image.Width, image.Height);

        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;

        await using Stream destination = _fileSystem.OpenWrite(destinationPath);
        switch (contentType)
        {
            case "image/jpeg":
                await image.SaveAsJpegAsync(
                    destination,
                    new JpegEncoder { Quality = 90 },
                    cancellationToken);
                break;
            case "image/png":
                await image.SaveAsPngAsync(destination, new PngEncoder(), cancellationToken);
                break;
            case "image/webp":
                await image.SaveAsWebpAsync(destination, new WebpEncoder(), cancellationToken);
                break;
            default:
                throw new CalibrationBlobValidationException(
                    "photo_content_type_unsupported",
                    "The photo format is not supported.");
        }
    }

    private async Task<CalibrationBlobMetadata> ReadMetadataAsync(
        string opaqueStorageKey,
        string contentType,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        string path = GetPath(opaqueStorageKey);
        FileInfoData info = _fileSystem.GetFileInfo(path);
        await using Stream source = _fileSystem.OpenRead(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return new(
            opaqueStorageKey,
            contentType,
            info.Length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            width,
            height);
    }

    private static async Task<string> DetectContentTypeAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[16];
        int count = 0;
        while (count < header.Length)
        {
            int read = await source.ReadAsync(header.AsMemory(count), cancellationToken);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        bool isPng = count >= 8 &&
            header[0] == 137 && header[1] == 80 && header[2] == 78 &&
            header[3] == 71 && header[4] == 13 && header[5] == 10 &&
            header[6] == 26 && header[7] == 10;
        bool isJpeg = count >= 3 &&
            header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        bool isWebp = count >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' &&
            header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' &&
            header[10] == (byte)'B' && header[11] == (byte)'P';

        return isPng
            ? "image/png"
            : isJpeg
                ? "image/jpeg"
                : isWebp
                    ? "image/webp"
                    : throw new CalibrationBlobValidationException(
                        "photo_magic_invalid",
                        "The photo is not a supported image format.");
    }

    private void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 ||
            width > _options.MaxWidth ||
            height > _options.MaxHeight ||
            (long)width * height > _options.MaxPixels)
        {
            throw new CalibrationBlobValidationException(
                "photo_dimensions_invalid",
                "The photo exceeds configured pixel or dimension limits.");
        }
    }

    private static string GetExtension(string contentType) =>
        contentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => throw new CalibrationBlobValidationException(
                "photo_content_type_unsupported",
                "The photo format is not supported."),
        };

    private void EnsureDirectory(string filePath)
    {
        string directory = _fileSystem.GetDirectoryName(filePath);
        if (!_fileSystem.DirectoryExists(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }
    }

    private string GetPath(string opaqueStorageKey)
    {
        if (string.IsNullOrWhiteSpace(opaqueStorageKey) ||
            Path.IsPathFullyQualified(opaqueStorageKey) ||
            opaqueStorageKey.Contains("..", StringComparison.Ordinal) ||
            opaqueStorageKey.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The calibration storage key is invalid.");
        }

        string normalized = opaqueStorageKey
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Join(_rootPath, normalized));
        string relativePath = Path.GetRelativePath(_rootPath, fullPath);
        if (relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison) ||
            Path.IsPathFullyQualified(relativePath))
        {
            throw new UnauthorizedAccessException("The calibration storage key is outside the private root.");
        }

        return fullPath;
    }

    private sealed record ImageInspection(string ContentType, int Width, int Height);
}
