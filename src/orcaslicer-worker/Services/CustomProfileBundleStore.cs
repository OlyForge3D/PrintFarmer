using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Owns persistent custom OrcaSlicer bundle files and their overlay links.
/// </summary>
[SuppressMessage(
    "Security",
    "CA3003:Review code for file path injection vulnerabilities",
    Justification = "Names are allowlist-validated and every resolved path is canonicalized and containment-checked against a fixed root.")]
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
    private readonly HashSet<string> _knownCustomBundles;
    private readonly object _fingerprintSync = new();
    private readonly Dictionary<string, FingerprintFileState>
        _fingerprintFiles = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

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
        _knownCustomBundles = DiscoverCompleteBundleNames();
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
        bundleName = ValidateBundleName(bundleName);
        PreparedBundle prepared = PrepareBundle(bundleName, request);

        await _mutationLock.WaitAsync(ct);
        try
        {
            EnsureStockBundleDoesNotExist(bundleName);
            Directory.CreateDirectory(_customProfilesPath);
            Directory.CreateDirectory(GetMetadataDirectoryPath());
            Directory.CreateDirectory(_overlayProfilesPath);

            string operationId = Guid.NewGuid().ToString("N");
            string stagingDirectory = ResolveContainedPath(
                _customProfilesPath,
                $".install-{operationId}",
                "invalid_bundle_path",
                "The bundle staging directory escaped the custom profile root.");
            string stagingManifest = ResolveContainedPath(
                _customProfilesPath,
                $".install-{operationId}.json",
                "invalid_bundle_path",
                "The bundle staging manifest escaped the custom profile root.");
            string stagingMetadata = ResolveContainedPath(
                GetMetadataDirectoryPath(),
                $".install-{operationId}.families.json",
                "invalid_bundle_path",
                "The bundle staging metadata escaped the custom metadata root.");

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
                    _ = _knownCustomBundles.Add(bundleName);
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
                LogSanitizer.Sanitize(bundleName),
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
        bundleName = ValidateBundleName(bundleName);

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
                GetOverlayManifestPath(bundleName),
                manifestPath,
                isDirectory: false);
            RemoveOverlayLink(
                GetOverlayDirectoryPath(bundleName),
                directoryPath,
                isDirectory: true);
            DeletePathIfPresent(manifestPath);
            DeletePathIfPresent(directoryPath);
            DeletePathIfPresent(metadataPath);
            _ = _knownCustomBundles.Remove(bundleName);

            _logger.LogInformation(
                "Removed custom OrcaSlicer bundle {BundleName}",
                LogSanitizer.Sanitize(bundleName));
            return true;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Reconciles this process's ephemeral overlay links with the shared custom
    /// profile volume.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the visible bundle set changed.</returns>
    public async Task<bool> ReconcileOverlayAsync(
        CancellationToken ct = default)
    {
        ValidateConfiguration();
        await _mutationLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_customProfilesPath);
            Directory.CreateDirectory(GetMetadataDirectoryPath());
            Directory.CreateDirectory(_overlayProfilesPath);

            HashSet<string> currentBundles = DiscoverCompleteBundleNames();
            bool changed = !_knownCustomBundles.SetEquals(currentBundles);

            foreach (string removedBundle in
                _knownCustomBundles.Except(currentBundles).ToArray())
            {
                RemoveOverlayLink(
                    GetOverlayManifestPath(removedBundle),
                    GetCustomManifestPath(removedBundle),
                    isDirectory: false);
                RemoveOverlayLink(
                    GetOverlayDirectoryPath(removedBundle),
                    GetCustomDirectoryPath(removedBundle),
                    isDirectory: true);
            }

            foreach (string bundleName in currentBundles)
            {
                EnsureOverlayLinks(bundleName);
            }

            _knownCustomBundles.Clear();
            _knownCustomBundles.UnionWith(currentBundles);

            if (changed)
            {
                _logger.LogInformation(
                    "Reconciled {BundleCount} custom OrcaSlicer bundles from the shared volume",
                    currentBundles.Count);
            }

            return changed;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Calculates a stable fingerprint of installed custom bundle source files.
    /// </summary>
    /// <returns>Fingerprint used to detect changes made by sibling workers.</returns>
    public string CalculateCustomProfilesFingerprint()
    {
        ValidateConfiguration();
        if (!Directory.Exists(_customProfilesPath))
        {
            return "empty";
        }

        lock (_fingerprintSync)
        {
            StringBuilder fingerprintSource = new();
            HashSet<string> currentPaths = new(
                _fingerprintFiles.Comparer);
            foreach (string path in EnumerateFingerprintFiles()
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                try
                {
                    FileInfo file = new(path);
                    if (!_fingerprintFiles.TryGetValue(
                            path,
                            out FingerprintFileState? cached)
                        || cached is null
                        || cached.Length != file.Length
                        || cached.LastWriteTicks
                            != file.LastWriteTimeUtc.Ticks)
                    {
                        using FileStream stream = File.OpenRead(path);
                        cached = new FingerprintFileState(
                            file.Length,
                            file.LastWriteTimeUtc.Ticks,
                            Convert.ToHexString(SHA256.HashData(stream)));
                        _fingerprintFiles[path] = cached;
                    }

                    _ = currentPaths.Add(path);
                    _ = fingerprintSource
                        .Append(Path.GetRelativePath(
                            _customProfilesPath,
                            path))
                        .Append(':')
                        .Append(cached.ContentHash)
                        .Append(';');
                }
                catch (FileNotFoundException)
                {
                    _ = _fingerprintFiles.Remove(path);
                }
                catch (DirectoryNotFoundException)
                {
                    _ = _fingerprintFiles.Remove(path);
                }
            }

            foreach (string removedPath in
                _fingerprintFiles.Keys.Except(currentPaths).ToArray())
            {
                _ = _fingerprintFiles.Remove(removedPath);
            }

            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprintSource.ToString()));
            return Convert.ToHexString(hash);
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

    private static string ValidateBundleName(string bundleName)
    {
        if (string.IsNullOrWhiteSpace(bundleName))
        {
            throw new CustomProfileBundleException(
                "invalid_bundle_name",
                "Bundle names may contain only letters, digits, periods, underscores, and hyphens.");
        }

        string safeBundleName = Path.GetFileName(bundleName);
        if (!string.Equals(
                safeBundleName,
                bundleName,
                StringComparison.Ordinal)
            || safeBundleName.Length > 128
            || safeBundleName.All(character => character == '.')
            || string.Equals(
                safeBundleName,
                ".printfarmer",
                StringComparison.OrdinalIgnoreCase)
            || safeBundleName.StartsWith(
                ".install-",
                StringComparison.OrdinalIgnoreCase)
            || safeBundleName.StartsWith(
                ".backup-",
                StringComparison.OrdinalIgnoreCase)
            || !BundleNameRegex().IsMatch(safeBundleName))
        {
            throw new CustomProfileBundleException(
                "invalid_bundle_name",
                "Bundle names may contain only letters, digits, periods, underscores, and hyphens.");
        }

        return safeBundleName;
    }

    private PreparedBundle PrepareBundle(
        string bundleName,
        CustomProfileBundleRequest request)
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
            _ = ResolveContainedPath(
                GetCustomDirectoryPath(bundleName),
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar),
                "invalid_profile_path",
                "A profile path escaped its custom bundle root.");
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
            || Path.IsPathFullyQualified(relativePath)
            || DriveQualifiedPathRegex().IsMatch(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomProfileBundleException(
                "invalid_profile_path",
                "Profile paths must be relative, forward-slash-separated JSON paths.");
        }

        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            string safeSegment = Path.GetFileName(segment);
            if (string.IsNullOrWhiteSpace(safeSegment)
                || segment is "." or ".."
                || segment.StartsWith('.')
                || !string.Equals(
                    safeSegment,
                    segment,
                    StringComparison.Ordinal))
            {
                throw new CustomProfileBundleException(
                    "invalid_profile_path",
                    $"Profile path '{relativePath}' contains an invalid segment.");
            }

            segments[index] = safeSegment;
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
            string destination = ResolveContainedPath(
                stagingDirectory,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar),
                "invalid_profile_path",
                "A profile path escaped its staging bundle root.");
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
        string backupDirectory = ResolveContainedPath(
            _customProfilesPath,
            $".backup-{operationId}",
            "invalid_bundle_path",
            "The bundle backup directory escaped the custom profile root.");
        string backupManifest = ResolveContainedPath(
            _customProfilesPath,
            $".backup-{operationId}.json",
            "invalid_bundle_path",
            "The bundle backup manifest escaped the custom profile root.");
        string backupMetadata = ResolveContainedPath(
            GetMetadataDirectoryPath(),
            $".backup-{operationId}.families.json",
            "invalid_bundle_path",
            "The bundle backup metadata escaped the custom metadata root.");

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
        if (File.Exists(GetStockManifestPath(bundleName))
            || Directory.Exists(GetStockDirectoryPath(bundleName)))
        {
            throw new CustomProfileBundleException(
                "stock_bundle_conflict",
                $"Custom bundle '{bundleName}' conflicts with an OrcaSlicer stock bundle.");
        }
    }

    private void EnsureOverlayLinks(string bundleName)
    {
        string manifestLink = GetOverlayManifestPath(bundleName);
        string directoryLink = GetOverlayDirectoryPath(bundleName);
        bool manifestCreated = false;

        try
        {
            manifestCreated = EnsureFileLink(
                manifestLink,
                GetCustomManifestPath(bundleName));
            _ = EnsureDirectoryLink(
                directoryLink,
                GetCustomDirectoryPath(bundleName));
        }
        catch
        {
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
            EnsureExpectedLink(linkPath, targetPath, isDirectory: false);
            return false;
        }

        _ = File.CreateSymbolicLink(linkPath, targetPath);
        return true;
    }

    private static bool EnsureDirectoryLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath))
        {
            EnsureExpectedLink(linkPath, targetPath, isDirectory: true);
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

    private static void EnsureExpectedLink(
        string linkPath,
        string targetPath,
        bool isDirectory)
    {
        FileSystemInfo link = isDirectory
            ? new DirectoryInfo(linkPath)
            : new FileInfo(linkPath);
        string? rawTarget = link.LinkTarget;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? linkDirectory = Path.GetDirectoryName(linkPath);
        string? resolvedTarget = rawTarget is null
            ? null
            : Path.GetFullPath(
                Path.IsPathRooted(rawTarget)
                    ? rawTarget
                    : Path.Join(linkDirectory, rawTarget));
        if (resolvedTarget is null
            || !string.Equals(
                resolvedTarget,
                Path.GetFullPath(targetPath),
                comparison))
        {
            throw new CustomProfileBundleException(
                "overlay_path_conflict",
                $"Overlay path '{Path.GetFileName(linkPath)}' is not the expected custom-profile link.");
        }
    }

    private static void RemoveOverlayLink(
        string linkPath,
        string targetPath,
        bool isDirectory)
    {
        FileSystemInfo link = isDirectory
            ? new DirectoryInfo(linkPath)
            : new FileInfo(linkPath);
        if (!File.Exists(linkPath)
            && !Directory.Exists(linkPath)
            && link.LinkTarget is null)
        {
            return;
        }

        EnsureExpectedLink(linkPath, targetPath, isDirectory);
        link.Delete();
    }

    private string GetCustomManifestPath(string bundleName) =>
        ResolveContainedPath(
            _customProfilesPath,
            $"{bundleName}.json",
            "invalid_bundle_path",
            "The custom manifest path escaped the custom profile root.");

    private string GetCustomDirectoryPath(string bundleName) =>
        ResolveContainedPath(
            _customProfilesPath,
            bundleName,
            "invalid_bundle_path",
            "The custom bundle path escaped the custom profile root.");

    private string GetMetadataPath(string bundleName) =>
        ResolveContainedPath(
            GetMetadataDirectoryPath(),
            $"{bundleName}.families.json",
            "invalid_bundle_path",
            "The family metadata path escaped the custom metadata root.");

    private string GetMetadataDirectoryPath() =>
        ResolveContainedPath(
            _customProfilesPath,
            ".printfarmer",
            "invalid_bundle_path",
            "The metadata directory escaped the custom profile root.");

    private string GetOverlayManifestPath(string bundleName) =>
        ResolveContainedPath(
            _overlayProfilesPath,
            $"{bundleName}.json",
            "invalid_bundle_path",
            "The overlay manifest path escaped the overlay root.");

    private string GetOverlayDirectoryPath(string bundleName) =>
        ResolveContainedPath(
            _overlayProfilesPath,
            bundleName,
            "invalid_bundle_path",
            "The overlay bundle path escaped the overlay root.");

    private string GetStockManifestPath(string bundleName) =>
        ResolveContainedPath(
            _stockProfilesPath,
            $"{bundleName}.json",
            "invalid_bundle_path",
            "The stock manifest path escaped the stock profile root.");

    private string GetStockDirectoryPath(string bundleName) =>
        ResolveContainedPath(
            _stockProfilesPath,
            bundleName,
            "invalid_bundle_path",
            "The stock bundle path escaped the stock profile root.");

    private HashSet<string> DiscoverCompleteBundleNames()
    {
        HashSet<string> bundleNames = new(StringComparer.Ordinal);
        if (!Directory.Exists(_customProfilesPath))
        {
            return bundleNames;
        }

        foreach (string manifestPath in Directory.EnumerateFiles(
            _customProfilesPath,
            "*.json",
            SearchOption.TopDirectoryOnly))
        {
            string bundleName = Path.GetFileNameWithoutExtension(manifestPath);
            if (bundleName.StartsWith(
                    ".install-",
                    StringComparison.OrdinalIgnoreCase)
                || bundleName.StartsWith(
                    ".backup-",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bundleName = ValidateBundleName(bundleName);
            if (!Directory.Exists(GetCustomDirectoryPath(bundleName)))
            {
                _logger.LogWarning(
                    "Ignoring incomplete custom bundle {BundleName}: manifest has no profile directory",
                    bundleName);
                continue;
            }

            _ = bundleNames.Add(bundleName);
        }

        foreach (string bundleNameCandidate in Directory.EnumerateDirectories(
                     _customProfilesPath,
                     "*",
                     SearchOption.TopDirectoryOnly)
                 .Select(directoryPath => Path.GetFileName(directoryPath)))
        {
            string bundleName = bundleNameCandidate;
            if (string.Equals(
                    bundleName,
                    ".printfarmer",
                    StringComparison.OrdinalIgnoreCase)
                || bundleName.StartsWith(
                    ".install-",
                    StringComparison.OrdinalIgnoreCase)
                || bundleName.StartsWith(
                    ".backup-",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bundleName = ValidateBundleName(bundleName);
            if (!File.Exists(GetCustomManifestPath(bundleName)))
            {
                _logger.LogWarning(
                    "Ignoring incomplete custom bundle {BundleName}: profile directory has no manifest",
                    bundleName);
            }
        }

        return bundleNames;
    }

    private IEnumerable<string> EnumerateFingerprintFiles()
    {
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(_customProfilesPath);
        while (pendingDirectories.TryPop(out string? directory))
        {
            string[] files;
            string[] childDirectories;
            try
            {
                files = Directory.GetFiles(directory);
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (string file in files.Where(file => !IsTransientCustomPath(file)))
            {
                yield return file;
            }

            foreach (string childDirectory in childDirectories.Where(
                         childDirectory => !IsTransientCustomPath(childDirectory)))
            {
                pendingDirectories.Push(childDirectory);
            }
        }
    }

    private bool IsTransientCustomPath(string path)
    {
        string relativePath = Path.GetRelativePath(
            _customProfilesPath,
            path);
        return relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                segment.StartsWith(
                    ".install-",
                    StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith(
                    ".backup-",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveContainedPath(
        string root,
        string relativePath,
        string errorCode,
        string errorMessage)
    {
        string canonicalRoot = Path.GetFullPath(root);
        if (Path.IsPathRooted(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || DriveQualifiedPathRegex().IsMatch(relativePath))
        {
            throw new CustomProfileBundleException(
                errorCode,
                errorMessage);
        }

        string rootPrefix = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(
            Path.Join(canonicalRoot, relativePath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new CustomProfileBundleException(
                errorCode,
                errorMessage);
        }

        return candidate;
    }

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

    [GeneratedRegex("^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex DriveQualifiedPathRegex();

    private sealed record PreparedBundle(
        string Manifest,
        IReadOnlyList<PreparedFile> Files,
        IReadOnlyDictionary<string, string> FamilyNames);

    private sealed record PreparedFile(string RelativePath, string Document);

    private sealed record FingerprintFileState(
        long Length,
        long LastWriteTicks,
        string ContentHash);

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
