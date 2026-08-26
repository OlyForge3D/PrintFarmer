using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Owns persistent custom OrcaSlicer bundle files and their overlay links.
/// </summary>
[SuppressMessage(
    "Security",
    "CA3003:Review code for file path injection vulnerabilities",
    Justification = "Bundle names and every relative path segment are allowlist-validated before paths are combined with fixed canonical roots.")]
public sealed partial class CustomProfileBundleStore : IAsyncDisposable
{
    private const int MaxFiles = 10_000;
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private const long MaxBundleBytes = 64L * 1024 * 1024;

    private readonly ILogger<CustomProfileBundleStore> _logger;
    private readonly string _stockProfilesPath;
    private readonly string _overlayProfilesPath;
    private readonly string _customProfilesPath;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    /// <summary>
    /// Creates a custom profile bundle store from the worker's profile-path
    /// environment variables.
    /// </summary>
    /// <param name="logger">Logger for non-secret operational diagnostics.</param>
    /// <param name="stockProfilesPath">Optional stock profile root override.</param>
    /// <param name="overlayProfilesPath">Optional composed profile root override.</param>
    /// <param name="customProfilesPath">Optional persistent custom profile root override.</param>
    public CustomProfileBundleStore(
        ILogger<CustomProfileBundleStore> logger,
        string? stockProfilesPath = null,
        string? overlayProfilesPath = null,
        string? customProfilesPath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stockProfilesPath = Path.GetFullPath(
            stockProfilesPath
            ?? Environment.GetEnvironmentVariable("ORCA_STOCK_PROFILES_PATH")
            ?? "/opt/orcaslicer/resources/profiles");
        _overlayProfilesPath = Path.GetFullPath(
            overlayProfilesPath
            ?? Environment.GetEnvironmentVariable("ORCA_PROFILES_PATH")
            ?? _stockProfilesPath);
        _customProfilesPath = Path.GetFullPath(
            customProfilesPath
            ?? Environment.GetEnvironmentVariable("ORCA_CUSTOM_PROFILES_PATH")
            ?? "/app/custom-profiles");
    }

    /// <summary>
    /// Installs or atomically replaces one custom manufacturer bundle.
    /// </summary>
    /// <param name="bundleName">Manufacturer bundle name.</param>
    /// <param name="request">Rendered manifest and profile documents.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task InstallAsync(
        string bundleName,
        CustomProfileBundleRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateConfiguration();
        ValidateBundleName(bundleName);
        PreparedBundle prepared = PrepareBundle(request);

        await _mutationLock.WaitAsync(ct);
        try
        {
            EnsureStockBundleDoesNotExist(bundleName);
            Directory.CreateDirectory(_customProfilesPath);
            Directory.CreateDirectory(Path.Join(_customProfilesPath, ".printfarmer"));
            Directory.CreateDirectory(_overlayProfilesPath);

            string operationId = Guid.NewGuid().ToString("N");
            string stagingDirectory = Path.Join(
                _customProfilesPath,
                $".install-{operationId}");
            string stagingManifest = Path.Join(
                _customProfilesPath,
                $".install-{operationId}.json");
            string stagingMetadata = Path.Join(
                _customProfilesPath,
                ".printfarmer",
                $".install-{operationId}.families.json");

            try
            {
                await WritePreparedBundleAsync(
                    stagingDirectory,
                    stagingManifest,
                    stagingMetadata,
                    prepared,
                    ct);
                PromotionBackup backup = PromoteBundle(
                    bundleName,
                    stagingDirectory,
                    stagingManifest,
                    stagingMetadata,
                    operationId);
                try
                {
                    EnsureOverlayLinks(bundleName);
                    DeletePromotionBackup(backup);
                }
                catch
                {
                    RollbackPromotion(bundleName, backup);
                    throw;
                }
            }
            finally
            {
                DeletePathIfPresent(stagingDirectory);
                DeletePathIfPresent(stagingManifest);
                DeletePathIfPresent(stagingMetadata);
            }

            _logger.LogInformation(
                "Installed custom OrcaSlicer bundle {BundleName} with {FileCount} profile documents",
                bundleName,
                prepared.Files.Count);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Removes one custom bundle from persistent storage and the composed
    /// overlay.
    /// </summary>
    /// <param name="bundleName">Manufacturer bundle name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the bundle existed.</returns>
    public async Task<bool> RemoveAsync(
        string bundleName,
        CancellationToken ct = default)
    {
        ValidateConfiguration();
        ValidateBundleName(bundleName);

        await _mutationLock.WaitAsync(ct);
        try
        {
            string manifestPath = GetCustomManifestPath(bundleName);
            string directoryPath = GetCustomDirectoryPath(bundleName);
            string metadataPath = GetMetadataPath(bundleName);
            bool existed = File.Exists(manifestPath)
                || Directory.Exists(directoryPath)
                || File.Exists(metadataPath);
            if (!existed)
            {
                return false;
            }

            RemoveOverlayLink(
                Path.Join(_overlayProfilesPath, $"{bundleName}.json"),
                manifestPath);
            RemoveOverlayLink(
                Path.Join(_overlayProfilesPath, bundleName),
                directoryPath);
            DeletePathIfPresent(manifestPath);
            DeletePathIfPresent(directoryPath);
            DeletePathIfPresent(metadataPath);

            _logger.LogInformation(
                "Removed custom OrcaSlicer bundle {BundleName}",
                bundleName);
            return true;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _mutationLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ValidateConfiguration()
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(
                _overlayProfilesPath,
                _stockProfilesPath,
                comparison)
            || string.Equals(
                _overlayProfilesPath,
                _customProfilesPath,
                comparison)
            || string.Equals(
                _stockProfilesPath,
                _customProfilesPath,
                comparison))
        {
            throw new CustomProfileBundleException(
                "custom_profiles_unavailable",
                "Custom profile management requires distinct stock, custom, and overlay roots.");
        }
    }

    private static void ValidateBundleName(string bundleName)
    {
        if (string.IsNullOrWhiteSpace(bundleName)
            || bundleName.Length > 128
            || !BundleNameRegex().IsMatch(bundleName))
        {
            throw new CustomProfileBundleException(
                "invalid_bundle_name",
                "Bundle names may contain only letters, digits, periods, underscores, and hyphens.");
        }
    }

    private PreparedBundle PrepareBundle(CustomProfileBundleRequest request)
    {
        if (request.Manifest.ValueKind != JsonValueKind.Object)
        {
            throw new CustomProfileBundleException(
                "invalid_manifest",
                "The custom bundle manifest must be a JSON object.");
        }

        IReadOnlyList<CustomProfileFileRequest> files = request.Files ?? [];
        if (files.Count > MaxFiles)
        {
            throw new CustomProfileBundleException(
                "bundle_too_large",
                $"A custom profile bundle may contain at most {MaxFiles} files.");
        }

        string manifest = request.Manifest.GetRawText();
        long totalBytes = JsonByteCount(manifest);
        HashSet<string> relativePaths = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        List<PreparedFile> preparedFiles = new(files.Count);
        Dictionary<string, string> familyNames = new(StringComparer.Ordinal);

        foreach (CustomProfileFileRequest file in files)
        {
            string relativePath = NormalizeRelativePath(file.RelativePath);
            if (!relativePaths.Add(relativePath))
            {
                throw new CustomProfileBundleException(
                    "duplicate_profile_path",
                    $"The bundle contains duplicate path '{relativePath}'.");
            }

            if (file.Document.ValueKind != JsonValueKind.Object)
            {
                throw new CustomProfileBundleException(
                    "invalid_profile_document",
                    $"Profile '{relativePath}' must be a JSON object.");
            }

            string document = file.Document.GetRawText();
            int documentBytes = JsonByteCount(document);
            if (documentBytes > MaxDocumentBytes)
            {
                throw new CustomProfileBundleException(
                    "profile_document_too_large",
                    $"Profile '{relativePath}' exceeds {MaxDocumentBytes} bytes.");
            }

            totalBytes += documentBytes;
            preparedFiles.Add(new PreparedFile(relativePath, document));
            if (!string.IsNullOrWhiteSpace(file.FamilyName))
            {
                familyNames[relativePath] = file.FamilyName.Trim();
            }
        }

        if (totalBytes > MaxBundleBytes)
        {
            throw new CustomProfileBundleException(
                "bundle_too_large",
                $"The rendered bundle exceeds {MaxBundleBytes} bytes.");
        }

        return new PreparedBundle(manifest, preparedFiles, familyNames);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomProfileBundleException(
                "invalid_profile_path",
                "Profile paths must be relative, forward-slash-separated JSON paths.");
        }

        string[] segments = relativePath.Split('/');
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.StartsWith('.')))
        {
            throw new CustomProfileBundleException(
                "invalid_profile_path",
                $"Profile path '{relativePath}' contains an invalid segment.");
        }

        return string.Join('/', segments);
    }

    private static int JsonByteCount(string json) =>
        System.Text.Encoding.UTF8.GetByteCount(json);

    private async Task WritePreparedBundleAsync(
        string stagingDirectory,
        string stagingManifest,
        string stagingMetadata,
        PreparedBundle prepared,
        CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDirectory);
        await File.WriteAllTextAsync(stagingManifest, prepared.Manifest, ct);

        foreach (PreparedFile file in prepared.Files)
        {
            string destination = Path.Join(
                stagingDirectory,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("Profile destination has no directory."));
            await File.WriteAllTextAsync(destination, file.Document, ct);
        }

        string metadata = JsonSerializer.Serialize(prepared.FamilyNames);
        await File.WriteAllTextAsync(stagingMetadata, metadata, ct);
    }

    private PromotionBackup PromoteBundle(
        string bundleName,
        string stagingDirectory,
        string stagingManifest,
        string stagingMetadata,
        string operationId)
    {
        string targetDirectory = GetCustomDirectoryPath(bundleName);
        string targetManifest = GetCustomManifestPath(bundleName);
        string targetMetadata = GetMetadataPath(bundleName);
        string backupDirectory = Path.Join(
            _customProfilesPath,
            $".backup-{operationId}");
        string backupManifest = Path.Join(
            _customProfilesPath,
            $".backup-{operationId}.json");
        string backupMetadata = Path.Join(
            _customProfilesPath,
            ".printfarmer",
            $".backup-{operationId}.families.json");

        MoveIfPresent(targetDirectory, backupDirectory);
        MoveIfPresent(targetManifest, backupManifest);
        MoveIfPresent(targetMetadata, backupMetadata);

        try
        {
            Directory.Move(stagingDirectory, targetDirectory);
            File.Move(stagingManifest, targetManifest);
            File.Move(stagingMetadata, targetMetadata);
        }
        catch
        {
            DeletePathIfPresent(targetDirectory);
            DeletePathIfPresent(targetManifest);
            DeletePathIfPresent(targetMetadata);
            MoveIfPresent(backupDirectory, targetDirectory);
            MoveIfPresent(backupManifest, targetManifest);
            MoveIfPresent(backupMetadata, targetMetadata);
            throw;
        }

        return new PromotionBackup(
            backupDirectory,
            backupManifest,
            backupMetadata);
    }

    private void EnsureStockBundleDoesNotExist(string bundleName)
    {
        if (File.Exists(Path.Join(_stockProfilesPath, $"{bundleName}.json"))
            || Directory.Exists(Path.Join(_stockProfilesPath, bundleName)))
        {
            throw new CustomProfileBundleException(
                "stock_bundle_conflict",
                $"Custom bundle '{bundleName}' conflicts with an OrcaSlicer stock bundle.");
        }
    }

    private void EnsureOverlayLinks(string bundleName)
    {
        string manifestLink = Path.Join(
            _overlayProfilesPath,
            $"{bundleName}.json");
        string directoryLink = Path.Join(
            _overlayProfilesPath,
            bundleName);
        bool manifestCreated = false;
        bool directoryCreated = false;

        try
        {
            manifestCreated = EnsureFileLink(
                manifestLink,
                GetCustomManifestPath(bundleName));
            directoryCreated = EnsureDirectoryLink(
                directoryLink,
                GetCustomDirectoryPath(bundleName));
        }
        catch
        {
            if (directoryCreated)
            {
                Directory.Delete(directoryLink);
            }

            if (manifestCreated)
            {
                File.Delete(manifestLink);
            }

            throw;
        }
    }

    private static bool EnsureFileLink(string linkPath, string targetPath)
    {
        if (File.Exists(linkPath))
        {
            EnsureExpectedLink(linkPath, targetPath);
            return false;
        }

        _ = File.CreateSymbolicLink(linkPath, targetPath);
        return true;
    }

    private static bool EnsureDirectoryLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath))
        {
            EnsureExpectedLink(linkPath, targetPath);
            return false;
        }

        _ = Directory.CreateSymbolicLink(linkPath, targetPath);
        return true;
    }

    private void RollbackPromotion(
        string bundleName,
        PromotionBackup backup)
    {
        DeletePathIfPresent(GetCustomDirectoryPath(bundleName));
        DeletePathIfPresent(GetCustomManifestPath(bundleName));
        DeletePathIfPresent(GetMetadataPath(bundleName));
        MoveIfPresent(
            backup.DirectoryPath,
            GetCustomDirectoryPath(bundleName));
        MoveIfPresent(
            backup.ManifestPath,
            GetCustomManifestPath(bundleName));
        MoveIfPresent(
            backup.MetadataPath,
            GetMetadataPath(bundleName));
    }

    private static void DeletePromotionBackup(PromotionBackup backup)
    {
        DeletePathIfPresent(backup.DirectoryPath);
        DeletePathIfPresent(backup.ManifestPath);
        DeletePathIfPresent(backup.MetadataPath);
    }

    private static void EnsureExpectedLink(string linkPath, string targetPath)
    {
        FileSystemInfo link = Directory.Exists(linkPath)
            ? new DirectoryInfo(linkPath)
            : new FileInfo(linkPath);
        FileSystemInfo? resolved = link.ResolveLinkTarget(returnFinalTarget: true);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (resolved is null
            || !string.Equals(
                Path.GetFullPath(resolved.FullName),
                Path.GetFullPath(targetPath),
                comparison))
        {
            throw new CustomProfileBundleException(
                "overlay_path_conflict",
                $"Overlay path '{Path.GetFileName(linkPath)}' is not the expected custom-profile link.");
        }
    }

    private static void RemoveOverlayLink(string linkPath, string targetPath)
    {
        if (!File.Exists(linkPath) && !Directory.Exists(linkPath))
        {
            return;
        }

        EnsureExpectedLink(linkPath, targetPath);
        if (Directory.Exists(linkPath))
        {
            Directory.Delete(linkPath);
        }
        else
        {
            File.Delete(linkPath);
        }
    }

    private string GetCustomManifestPath(string bundleName) =>
        Path.Join(_customProfilesPath, $"{bundleName}.json");

    private string GetCustomDirectoryPath(string bundleName) =>
        Path.Join(_customProfilesPath, bundleName);

    private string GetMetadataPath(string bundleName) =>
        Path.Join(
            _customProfilesPath,
            ".printfarmer",
            $"{bundleName}.families.json");

    private static void MoveIfPresent(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    private static void DeletePathIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BundleNameRegex();

    private sealed record PreparedBundle(
        string Manifest,
        IReadOnlyList<PreparedFile> Files,
        IReadOnlyDictionary<string, string> FamilyNames);

    private sealed record PreparedFile(string RelativePath, string Document);

    private sealed record PromotionBackup(
        string DirectoryPath,
        string ManifestPath,
        string MetadataPath);
}

/// <summary>
/// A rendered custom OrcaSlicer manufacturer bundle.
/// </summary>
/// <param name="Manifest">Top-level manufacturer manifest JSON object.</param>
/// <param name="Files">Profile documents stored below the manufacturer directory.</param>
public sealed record CustomProfileBundleRequest(
    JsonElement Manifest,
    IReadOnlyList<CustomProfileFileRequest>? Files);

/// <summary>
/// One rendered profile document in a custom bundle.
/// </summary>
/// <param name="RelativePath">Forward-slash-separated path below the bundle directory.</param>
/// <param name="FamilyName">Owning PrintFarmer family for failure reporting.</param>
/// <param name="Document">Profile JSON object.</param>
public sealed record CustomProfileFileRequest(
    string RelativePath,
    string? FamilyName,
    JsonElement Document);

/// <summary>
/// A safe, client-visible custom bundle validation or configuration error.
/// </summary>
public sealed class CustomProfileBundleException : Exception
{
    private const string DefaultCode = "invalid_custom_profile_bundle";

    public CustomProfileBundleException()
        : this(DefaultCode, "The custom profile bundle is invalid.")
    {
    }

    public CustomProfileBundleException(string message)
        : this(DefaultCode, message)
    {
    }

    public CustomProfileBundleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = DefaultCode;
    }

    public CustomProfileBundleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine-readable error code.</summary>
    public string Code { get; }
}
