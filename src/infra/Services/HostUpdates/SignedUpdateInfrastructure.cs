using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

#pragma warning disable SA1501, SA1503, SA1513, SA1516, SA1520, SA1407, CA1865

namespace Farm.Infrastructure.Services.HostUpdates;

public sealed record SignedUpdateManifest(
    int Schema,
    string Tag,
    string Version,
    string Channel,
    string SourceBranch,
    string SourceCommit,
    string BuildId,
    long Sequence,
    bool ManagedUpdateEligible,
    IReadOnlyList<SignedUpdateService> Services,
    IReadOnlyList<string> Platforms,
    IReadOnlyDictionary<string, string> PlatformDigests,
    string? MinimumUpdaterVersion = null,
    string? Compatibility = null);

public sealed record SignedUpdateService(string Id, string Image, IReadOnlyList<string>? Platforms = null);

public sealed record SignedUpdateValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SignedUpdateValidationResult Valid { get; } = new(true, []);
}

public sealed record VerifiedSignedUpdateRelease(SignedUpdateManifest Manifest, byte[] ManifestBytes);

public static partial class SignedUpdateManifestValidator
{
    private static readonly HashSet<string> ServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SignedUpdateManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Manifest must be an object.");
        }
        HashSet<string> known = ["schema", "tag", "version", "channel", "sourceBranch", "sourceCommit", "buildId", "sequence",
            "managedUpdateEligible", "services", "platforms", "platformDigests", "minimumUpdaterVersion", "compatibility"];
        JsonProperty[] properties = document.RootElement.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
        {
            throw new JsonException("Manifest contains duplicate fields.");
        }

        string[] required = ["schema", "tag", "version", "channel", "sourceBranch", "sourceCommit", "buildId", "sequence",
            "managedUpdateEligible", "services", "platforms", "platformDigests"];
        if (required.Any(name => !properties.Any(property => property.Name == name)))
        {
            throw new JsonException("Manifest is missing required fields.");
        }

        string[] unknown = properties.Select(property => property.Name).Where(name => !known.Contains(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new JsonException($"Unsupported manifest fields: {string.Join(", ", unknown)}");
        }

        SignedUpdateManifest? manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(json, JsonOptions);
        return manifest ?? throw new JsonException("Manifest must be an object.");
    }

    public static SignedUpdateValidationResult Validate(SignedUpdateManifest? manifest)
    {
        List<string> errors = [];
        if (manifest is null) return new(false, ["manifest_missing"]);
        if (manifest.Schema != 1) errors.Add("schema_invalid");
        if (!CanonicalVersion().IsMatch(manifest.Version) || !IsVersionForChannel(manifest.Version, manifest.Channel)) errors.Add("version_invalid");
        if (manifest.Channel is not ("stable" or "insider")) errors.Add("channel_invalid");
        string expectedTag = $"v{manifest.Version}";
        if (manifest.Tag != expectedTag) errors.Add("tag_version_mismatch");
        string expectedBranch = manifest.Channel == "stable" ? "main" : "development";
        if (manifest.SourceBranch != expectedBranch) errors.Add("source_branch_invalid");
        if (!LowerHex40().IsMatch(manifest.SourceCommit)) errors.Add("source_commit_invalid");
        if (string.IsNullOrWhiteSpace(manifest.BuildId) || manifest.BuildId.Length > 128) errors.Add("build_id_invalid");
        if (manifest.Sequence != DeriveSequence(manifest.Version)) errors.Add("sequence_mismatch");
        if (!manifest.ManagedUpdateEligible) errors.Add("managed_update_ineligible");
        if (manifest.Services is null || manifest.Services.Count != ServiceIds.Count) errors.Add("service_set_invalid");
        else
        {
            string[] ids = manifest.Services.Select(service => service?.Id ?? string.Empty).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !ServiceIds.Contains(id))) errors.Add("service_set_invalid");
            foreach (SignedUpdateService service in manifest.Services)
            {
                if (service is null)
                {
                    errors.Add("service_set_invalid");
                    continue;
                }

                if (!IsApprovedImage(service.Id, service.Image)) errors.Add("image_reference_invalid");
                if (service.Platforms is not null && service.Platforms.Any(platform => !IsPlatform(platform))) errors.Add("platform_invalid");
            }
        }

        if (manifest.Platforms is null || manifest.Platforms.Count == 0 || manifest.Platforms.Distinct(StringComparer.Ordinal).Count() != manifest.Platforms.Count ||
            manifest.Platforms.Any(platform => !IsPlatform(platform))) errors.Add("platform_invalid");
        if (manifest.PlatformDigests is null || manifest.Platforms is null || manifest.PlatformDigests.Count != manifest.Platforms.Count ||
            manifest.Platforms.Any(platform => !manifest.PlatformDigests.TryGetValue(platform, out string? digest) || !IsSha256Digest(digest))) errors.Add("platform_digest_invalid");
        if (manifest.MinimumUpdaterVersion is not null && !HostUpdateValidation.IsSemanticVersion(manifest.MinimumUpdaterVersion)) errors.Add("compatibility_invalid");
        return errors.Count == 0 ? SignedUpdateValidationResult.Valid : new(false, errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static long DeriveSequence(string version)
    {
        Match match = CanonicalVersion().Match(version);
        if (!match.Success) return 0;
        long major = long.Parse(match.Groups["major"].Value);
        long minor = long.Parse(match.Groups["minor"].Value);
        long patch = long.Parse(match.Groups["patch"].Value);
        long insider = match.Groups["insider"].Success ? long.Parse(match.Groups["insider"].Value) : 0;
        return checked(major * 1_000_000_000L + minor * 1_000_000L + patch * 1_000L + insider);
    }

    private static bool IsVersionForChannel(string version, string channel) =>
        channel == "stable" ? CanonicalVersion().Match(version).Groups["insider"].Success is false :
        channel == "insider" && CanonicalVersion().Match(version).Groups["insider"].Success;
    private static bool IsApprovedImage(string id, string? image) =>
        image is not null && image.StartsWith($"ghcr.io/olyforge3d/printfarmer-{id}@", StringComparison.Ordinal) &&
        image.Contains("@sha256:", StringComparison.Ordinal) &&
        !image[(image.IndexOf('/') + 1)..image.IndexOf('@')].Contains(':', StringComparison.Ordinal) &&
        IsSha256Digest(image[(image.IndexOf("@sha256:", StringComparison.Ordinal) + 1)..]);
    private static bool IsSha256Digest(string? value) => value is not null && Sha256().IsMatch(value);
    private static bool IsPlatform(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    [GeneratedRegex(@"^(?<major>[1-9]\d*)\.(?<minor>\d+)\.(?<patch>\d+)(?:-insider\.(?<insider>[1-9]\d*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalVersion();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex40();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}

public sealed record GitHubReleaseAsset(string Name, string BrowserDownloadUrl);
public sealed record GitHubRelease(long Id, string TagName, bool Draft, bool Prerelease, IReadOnlyList<GitHubReleaseAsset> Assets);

public interface ISignedReleaseVerifier
{
    Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken);
}

public sealed class GitHubSignedReleaseDiscovery(HttpClient httpClient, ISignedReleaseVerifier verifier)
{
    private const string Repository = "OlyForge3D/PrintFarmer";
    public async Task<VerifiedSignedUpdateRelease?> DiscoverAsync(string channel, CancellationToken cancellationToken)
    {
        if (channel is not ("stable" or "insider")) throw new ArgumentException("Unsupported channel.", nameof(channel));
        List<GitHubRelease> releases = [];
        for (int page = 1; page <= 10; page++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintFarmer", "1.0"));
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            IReadOnlyList<GitHubRelease>? pageReleases = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubRelease>>(cancellationToken: cancellationToken);
            if (pageReleases is null || pageReleases.Count == 0) break;
            releases.AddRange(pageReleases);
            if (pageReleases.Count < 100) break;
        }

        string identity = channel == "stable"
            ? "https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main"
            : "https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development";
        List<VerifiedSignedUpdateRelease> verified = [];
        foreach (GitHubRelease release in releases.Where(candidate => !candidate.Draft && candidate.Prerelease == (channel == "insider")))
        {
            string? tag = release.TagName;
            if (tag is null || !tag.StartsWith("v", StringComparison.Ordinal)) continue;
            GitHubReleaseAsset? manifestAsset = release.Assets.FirstOrDefault(asset => asset.Name == "update-manifest.json");
            GitHubReleaseAsset? bundleAsset = release.Assets.FirstOrDefault(asset => asset.Name == "update-manifest.sigstore.json");
            if (manifestAsset is null || bundleAsset is null) continue;
            byte[] manifestBytes = await httpClient.GetByteArrayAsync(manifestAsset.BrowserDownloadUrl, cancellationToken);
            byte[] bundleBytes = await httpClient.GetByteArrayAsync(bundleAsset.BrowserDownloadUrl, cancellationToken);
            SignedUpdateManifest parsed;
            try { parsed = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(manifestBytes)); }
            catch (JsonException) { continue; }
            if (parsed.Tag != tag || !SignedUpdateManifestValidator.Validate(parsed).IsValid ||
                !await verifier.VerifyAsync(manifestBytes, bundleBytes, identity, cancellationToken)) continue;
            verified.Add(new(parsed, manifestBytes));
        }
        return verified.OrderByDescending(candidate => candidate.Manifest.Sequence).FirstOrDefault();
    }
}

public sealed record CosignVerifierOptions(string ExecutablePath, TimeSpan Timeout, int MaxDiagnostics = 8192);

public sealed class ProcessCosignVerifier(CosignVerifierOptions options) : ISignedReleaseVerifier
{
    public async Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "printfarmer-cosign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string bundlePath = Path.Combine(directory, "bundle.json");
        try
        {
            await File.WriteAllBytesAsync(manifestPath, manifest.ToArray(), cancellationToken);
            await File.WriteAllBytesAsync(bundlePath, bundle.ToArray(), cancellationToken);
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.ExecutablePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("verify-blob");
            process.StartInfo.ArgumentList.Add("--bundle");
            process.StartInfo.ArgumentList.Add(bundlePath);
            process.StartInfo.ArgumentList.Add("--certificate-oidc-issuer");
            process.StartInfo.ArgumentList.Add("https://token.actions.githubusercontent.com");
            process.StartInfo.ArgumentList.Add("--certificate-identity");
            process.StartInfo.ArgumentList.Add(certificateIdentity);
            process.StartInfo.ArgumentList.Add(manifestPath);
            if (!process.Start()) return false;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = (await output + await error)[..Math.Min(options.MaxDiagnostics, (await output + await error).Length)];
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class VerifiedGitHubReleaseMetadataProvider(GitHubSignedReleaseDiscovery discovery) : IHostUpdateMetadataProvider
{
    public async Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct)
    {
        VerifiedSignedUpdateRelease verifiedRelease = await discovery.DiscoverAsync(channel, ct) ?? throw new InvalidDataException("No verified release is available.");
        SignedUpdateManifest manifest = verifiedRelease.Manifest;
        SignedUpdateValidationResult validation = SignedUpdateManifestValidator.Validate(manifest);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(',', validation.Errors));
        string version = manifest.Version;
        string releaseId = $"{manifest.Channel}:{version}";
        string manifestDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(verifiedRelease.ManifestBytes)).ToLowerInvariant()}";
        Dictionary<string, string> componentPlatformDigests = manifest.Services
            .SelectMany(service => (service.Platforms ?? manifest.Platforms).Select(platform => (Key: $"{service.Id}/{platform}", Value: service.Image[(service.Image.IndexOf("@sha256:", StringComparison.Ordinal) + 1)..])))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        CanonicalReleaseIdentity identity = new(releaseId, version, manifest.Channel, manifest.Tag, manifest.SourceBranch, manifest.SourceCommit,
            manifest.SourceCommit, manifest.BuildId, releaseId, version, manifestDigest);
        return new(manifest.Channel, manifest.Sequence, true, identity, componentPlatformDigests, manifest.MinimumUpdaterVersion ?? "0.0.0");
    }
}
