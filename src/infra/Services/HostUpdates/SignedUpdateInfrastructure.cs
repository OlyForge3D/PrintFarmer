using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly string[] OrderedServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    // internal (not private) so Farm.Infrastructure.Tests (InternalsVisibleTo) can assert these
    // against scripts/ci/fixtures/release-version-sequence.schema.json's contract.limits /
    // contract.radices, keeping the golden schema and this implementation from silently drifting.
    internal const long SequenceMajorMaximum = 99;
    internal const long SequenceMinorMaximum = 999;
    internal const long SequencePatchMaximum = 99_999;
    internal const long SequenceInsiderMaximum = 99_998;
    internal const long SequenceStableSuffix = 99_999;
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
            "managedUpdateEligible", "services", "platforms", "platformDigests", "minimumUpdaterVersion"];
        if (required.Any(name => !properties.Any(property => property.Name == name)))
        {
            throw new JsonException("Manifest is missing required fields.");
        }

        string[] unknown = properties.Select(property => property.Name).Where(name => !known.Contains(name)).ToArray();
        if (unknown.Length > 0)
        {
            throw new JsonException($"Unsupported manifest fields: {string.Join(", ", unknown)}");
        }

        ValidateManifestShape(document.RootElement);
        SignedUpdateManifest? manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(json, JsonOptions);
        return manifest ?? throw new JsonException("Manifest must be an object.");
    }

    public static SignedUpdateValidationResult Validate(SignedUpdateManifest? manifest)
    {
        List<string> errors = [];
        if (manifest is null)
        {
            return new(false, ["manifest_missing"]);
        }

        if (manifest.Schema != 1)
        {
            errors.Add("schema_invalid");
        }

        if (!CanonicalVersion().IsMatch(manifest.Version) || !IsVersionForChannel(manifest.Version, manifest.Channel))
        {
            errors.Add("version_invalid");
        }

        if (manifest.Channel is not ("stable" or "insider"))
        {
            errors.Add("channel_invalid");
        }

        string expectedTag = $"v{manifest.Version}";
        if (manifest.Tag != expectedTag)
        {
            errors.Add("tag_version_mismatch");
        }

        string expectedBranch = manifest.Channel == "stable" ? "main" : "development";
        if (manifest.SourceBranch != expectedBranch)
        {
            errors.Add("source_branch_invalid");
        }

        if (!LowerHex40().IsMatch(manifest.SourceCommit))
        {
            errors.Add("source_commit_invalid");
        }

        if (string.IsNullOrWhiteSpace(manifest.BuildId) || manifest.BuildId.Length > 128)
        {
            errors.Add("build_id_invalid");
        }

        try
        {
            if (manifest.Sequence != DeriveSequence(manifest.Version))
            {
                errors.Add("sequence_mismatch");
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            errors.Add("sequence_invalid");
        }
        if (!manifest.ManagedUpdateEligible)
        {
            errors.Add("managed_update_ineligible");
        }

        IReadOnlyList<string> manifestPlatforms = manifest.Platforms ?? [];
        string[] expectedTopLevelPlatforms = ["linux-amd64", "linux-arm64"];
        HashSet<string> expectedPlatformDigestKeys = new(StringComparer.Ordinal);
        if (manifest.Services is null || manifest.Services.Count != OrderedServiceIds.Length)
        {
            errors.Add("service_set_invalid");
        }
        else
        {
            string[] ids = manifest.Services.Select(service => service?.Id ?? string.Empty).ToArray();
            if (!ids.SequenceEqual(OrderedServiceIds, StringComparer.Ordinal))
            {
                errors.Add("service_set_invalid");
            }

            foreach (SignedUpdateService service in manifest.Services)
            {
                if (service is null)
                {
                    errors.Add("service_set_invalid");
                    continue;
                }

                if (!IsApprovedImage(service.Id, service.Image))
                {
                    errors.Add("image_reference_invalid");
                }

                IReadOnlyList<string> servicePlatforms = service.Platforms ?? [];
                if (servicePlatforms.Count == 0
                    || servicePlatforms.Distinct(StringComparer.Ordinal).Count() != servicePlatforms.Count
                    || servicePlatforms.Any(platform => !IsPlatform(platform)))
                {
                    errors.Add("platform_invalid");
                }
                else
                {
                    string[] expectedServicePlatforms = service.Id == "orcaslicer-worker"
                        ? ["linux-amd64"]
                        : expectedTopLevelPlatforms;
                    if (!servicePlatforms.SequenceEqual(expectedServicePlatforms, StringComparer.Ordinal))
                    {
                        errors.Add("platform_invalid");
                    }

                    foreach (string platform in servicePlatforms)
                    {
                        string key = PlatformKey(service.Id, platform);
                        expectedPlatformDigestKeys.Add(key);
                    }
                }
            }
        }

        if (manifest.Platforms is null || manifest.Platforms.Count == 0 || manifest.Platforms.Distinct(StringComparer.Ordinal).Count() != manifest.Platforms.Count ||
            manifest.Platforms.Any(platform => !IsPlatform(platform))
            || !manifest.Platforms.SequenceEqual(expectedTopLevelPlatforms, StringComparer.Ordinal))
        {
            errors.Add("platform_invalid");
        }

        if (manifest.PlatformDigests is null
            || manifest.PlatformDigests.Count != expectedPlatformDigestKeys.Count
            || manifest.PlatformDigests.Keys.Any(key => !expectedPlatformDigestKeys.Contains(key))
            || expectedPlatformDigestKeys.Any(key => !manifest.PlatformDigests.TryGetValue(key, out string? digest) || !IsSha256Digest(digest)))
        {
            errors.Add("platform_digest_invalid");
        }
        if (manifest.MinimumUpdaterVersion is null || !HostUpdateValidation.IsSemanticVersion(manifest.MinimumUpdaterVersion))
        {
            errors.Add("compatibility_invalid");
        }

        return errors.Count == 0 ? SignedUpdateValidationResult.Valid : new(false, errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static long DeriveSequence(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Match match = CanonicalVersion().Match(version);
        if (!match.Success)
        {
            throw new FormatException("Sequence version must use unsigned decimal components without leading zeros.");
        }

        long major = ParseBounded(match.Groups["major"].Value, SequenceMajorMaximum, "Major version");
        Group insider = match.Groups["insider"];

        // Mirrors the signer contract (scripts/ci/release-manifest.mjs): only a *stable* signed
        // release must have a non-zero major version; a 0.x insider prerelease is legitimate and
        // must stay derivable here, or the host would reject releases the signer can publish.
        if (!insider.Success && major < 1)
        {
            throw new FormatException("Stable major version must be greater than zero.");
        }

        long minor = ParseBounded(match.Groups["minor"].Value, SequenceMinorMaximum, "Minor version");
        long patch = ParseBounded(match.Groups["patch"].Value, SequencePatchMaximum, "Patch version");
        long suffix = SequenceStableSuffix;
        if (insider.Success)
        {
            suffix = ParseBounded(insider.Value, SequenceInsiderMaximum, "Prerelease sequence");
            if (suffix < 1)
            {
                throw new FormatException("Prerelease sequence must be between 1 and 99998.");
            }
        }

        return checked((((major * 1_000L) + minor) * 100_000L + patch) * 100_000L + suffix);
    }

    internal static string PlatformKey(string serviceId, string platform) => $"{serviceId}/{platform}";

    public static bool IsTagForChannel(string? tag, string channel)
    {
        return tag is not null && channel switch
        {
            "stable" => StableTag().IsMatch(tag),
            "insider" => InsiderTag().IsMatch(tag),
            _ => false,
        };
    }

    private static void ValidateManifestShape(JsonElement root)
    {
        string[] required =
        [
            "schema", "tag", "version", "channel", "sourceBranch", "sourceCommit", "buildId", "sequence",
            "managedUpdateEligible", "services", "platforms", "platformDigests", "minimumUpdaterVersion",
        ];
        string[] optionalProperties = ["compatibility"];
        RequireObjectProperties(root, required, optionalProperties);
        RequireKind(root, "schema", JsonValueKind.Number);
        RequireKind(root, "sequence", JsonValueKind.Number);
        RequireKind(root, "managedUpdateEligible", JsonValueKind.True, JsonValueKind.False);
        foreach (string name in new[] { "tag", "version", "channel", "sourceBranch", "sourceCommit", "buildId" })
        {
            RequireKind(root, name, JsonValueKind.String);
        }

        foreach (string name in new[] { "minimumUpdaterVersion" })
        {
            if (root.TryGetProperty(name, out JsonElement optional) && optional.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"{name} must be a string.");
            }
        }
        if (root.TryGetProperty("compatibility", out JsonElement compatibility)
            && compatibility.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new JsonException("compatibility must be a string.");
        }

        JsonElement services = RequireKind(root, "services", JsonValueKind.Array);
        foreach (JsonElement service in services.EnumerateArray())
        {
            RequireObjectProperties(service, ["id", "image", "platforms"], []);
            RequireKind(service, "id", JsonValueKind.String);
            RequireKind(service, "image", JsonValueKind.String);
            RequireStringArray(service.GetProperty("platforms"), "service platforms");
        }

        RequireStringArray(RequireKind(root, "platforms", JsonValueKind.Array), "platforms");
        JsonElement digests = RequireKind(root, "platformDigests", JsonValueKind.Object);
        EnsureNoDuplicateProperties(digests);
        if (digests.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
        {
            throw new JsonException("Platform digests must be strings.");
        }
    }

    private static JsonElement RequireKind(JsonElement element, string name, params JsonValueKind[] kinds)
    {
        JsonElement value = element.GetProperty(name);
        if (!kinds.Contains(value.ValueKind))
        {
            throw new JsonException($"{name} has an invalid JSON type.");
        }

        return value;
    }

    private static void RequireStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array || element.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new JsonException($"{name} must be an array of strings.");
        }
    }

    private static void RequireObjectProperties(JsonElement element, IReadOnlyList<string> required, IReadOnlyList<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Manifest object expected.");
        }

        EnsureNoDuplicateProperties(element);
        HashSet<string> permitted = [.. required, .. optional];
        JsonProperty[] properties = element.EnumerateObject().ToArray();
        if (required.Any(name => !properties.Any(property => property.Name == name)) ||
            properties.Any(property => !permitted.Contains(property.Name)))
        {
            throw new JsonException("Manifest object has missing or unsupported fields.");
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        string[] names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new JsonException("Manifest contains duplicate fields.");
        }
    }

    private static long ParseBounded(string value, long maximum, string label)
    {
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
        {
            throw new OverflowException($"{label} exceeds the signed 64-bit parsing limit.");
        }

        if (parsed > maximum)
        {
            throw new OverflowException($"{label} exceeds sequence encoding limit of {maximum}.");
        }

        return parsed;
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
    internal static bool IsPlatform(string? value) => value is
        "linux-amd64" or "linux-arm64" or
        "windows-amd64" or "windows-arm64" or
        "darwin-amd64" or "darwin-arm64";
    [GeneratedRegex(@"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-insider\.(?<insider>[1-9]\d*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalVersion();
    [GeneratedRegex(@"^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StableTag();
    [GeneratedRegex(@"^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)-insider\.[1-9]\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex InsiderTag();
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex40();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}

public sealed record GitHubReleaseAsset(long Id, string Name);
public sealed record GitHubRelease(
    long Id,
    [property: JsonPropertyName("tag_name")] string TagName,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public interface ISignedReleaseVerifier
{
    Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken);
}

public sealed class GitHubSignedReleaseDiscovery(HttpClient httpClient, ISignedReleaseVerifier verifier)
{
    private const string Repository = "OlyForge3D/PrintFarmer";
    private const int MaximumCandidates = 25;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumBundleBytes = 1024 * 1024;
    private const int MaximumReleaseListingBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> AllowedAssetRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public async Task<VerifiedSignedUpdateRelease?> DiscoverAsync(string channel, CancellationToken cancellationToken)
    {
        if (channel is not ("stable" or "insider"))
        {
            throw new ArgumentException("Unsupported channel.", nameof(channel));
        }

        List<GitHubRelease> releases = [];
        for (int page = 1; page <= 10; page++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintFarmer", "1.0"));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            byte[] releaseBytes = await ReadBoundedAsync(
                response.Content,
                MaximumReleaseListingBytes,
                "GitHub release listing",
                cancellationToken);
            IReadOnlyList<GitHubRelease>? pageReleases = JsonSerializer.Deserialize<IReadOnlyList<GitHubRelease>>(
                releaseBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (pageReleases is null || pageReleases.Count == 0)
            {
                break;
            }

            releases.AddRange(pageReleases);
            if (pageReleases.Count < 100)
            {
                break;
            }
        }

        string identity = HostUpdateTrustRoot.CertificateIdentity(channel);
        List<ReleaseCandidate> candidates = [];
        foreach (GitHubRelease release in releases
            .Where(candidate => !candidate.Draft && candidate.Prerelease == (channel == "insider")
                && SignedUpdateManifestValidator.IsTagForChannel(candidate.TagName, channel))
            .Take(MaximumCandidates))
        {
            if (release.Assets is null)
            {
                continue;
            }

            GitHubReleaseAsset[] manifestAssets = release.Assets.Where(asset => asset.Name == "update-manifest.json").ToArray();
            GitHubReleaseAsset[] bundleAssets = release.Assets.Where(asset => asset.Name == "update-manifest.sigstore.json").ToArray();
            if (manifestAssets.Length != 1
                || bundleAssets.Length != 1
                || manifestAssets[0].Id <= 0
                || bundleAssets[0].Id <= 0)
            {
                continue;
            }

            try
            {
                byte[] manifestBytes = await DownloadAssetAsync(manifestAssets[0].Id, MaximumManifestBytes, cancellationToken);
                SignedUpdateManifest parsed = SignedUpdateManifestValidator.Parse(Encoding.UTF8.GetString(manifestBytes));
                if (parsed.Tag == release.TagName && SignedUpdateManifestValidator.Validate(parsed).IsValid)
                {
                    candidates.Add(new(parsed, manifestBytes, bundleAssets[0].Id));
                }
            }
            catch (Exception exception) when (IsCandidateLocalFailure(exception, cancellationToken))
            {
                continue;
            }
        }

        foreach (ReleaseCandidate candidate in candidates.OrderByDescending(item => item.Manifest.Sequence))
        {
            byte[] bundleBytes;
            try
            {
                bundleBytes = await DownloadAssetAsync(candidate.BundleAssetId, MaximumBundleBytes, cancellationToken);
            }
            catch (Exception exception) when (IsCandidateLocalFailure(exception, cancellationToken))
            {
                continue;
            }

            try
            {
                if (await verifier.VerifyAsync(candidate.ManifestBytes, bundleBytes, identity, cancellationToken))
                {
                    return new(candidate.Manifest, candidate.ManifestBytes);
                }
            }
            catch (Exception exception) when (IsCandidateLocalFailure(exception, cancellationToken))
            {
                continue;
            }
        }

        return null;
    }

    private async Task<byte[]> DownloadAssetAsync(long assetId, int maximumBytes, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateAssetRequest(
            new Uri($"https://api.github.com/repos/{Repository}/releases/assets/{assetId}", UriKind.Absolute));
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            Uri? location = response.Headers.Location;
            if (location is null
                || !location.IsAbsoluteUri
                || location.Scheme != Uri.UriSchemeHttps
                || !AllowedAssetRedirectHosts.Contains(location.IdnHost))
            {
                throw new InvalidDataException("GitHub release asset redirect origin is not allowed.");
            }

            using HttpRequestMessage redirectedRequest = CreateAssetRequest(location);
            using HttpResponseMessage redirectedResponse = await httpClient.SendAsync(
                redirectedRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)redirectedResponse.StatusCode is >= 300 and < 400)
            {
                throw new InvalidDataException("GitHub release asset returned more than one redirect.");
            }

            redirectedResponse.EnsureSuccessStatusCode();
            return await ReadBoundedAsync(
                redirectedResponse.Content,
                maximumBytes,
                "GitHub release asset",
                cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(
            response.Content,
            maximumBytes,
            "GitHub release asset",
            cancellationToken);
    }

    private static HttpRequestMessage CreateAssetRequest(Uri uri)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PrintFarmer", "1.0"));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        string contentName,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"{contentName} exceeds the {maximumBytes}-byte limit.");
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream destination = new(Math.Min(maximumBytes, 16 * 1024));
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException($"{contentName} exceeds the {maximumBytes}-byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return destination.ToArray();
    }

    private static bool IsCandidateLocalFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private sealed record ReleaseCandidate(SignedUpdateManifest Manifest, byte[] ManifestBytes, long BundleAssetId);
}

public sealed record CosignVerifierOptions(string ExecutablePath, TimeSpan Timeout, int MaxDiagnostics = 8192, string? TrustedRootPath = null);

internal sealed record CosignProcessCommand(string ExecutablePath, IReadOnlyList<string> Arguments);
internal sealed record CosignProcessResult(int ExitCode, string Diagnostics);

internal interface ICosignProcessRunner
{
    Task<CosignProcessResult> RunAsync(CosignProcessCommand command, TimeSpan timeout, int maxDiagnostics, CancellationToken cancellationToken);
}

internal sealed class ProcessCosignRunner : ICosignProcessRunner
{
    /// <summary>
    /// Bound on the post-kill exit wait below. <see cref="Process.Kill(bool)"/> is not
    /// synchronous, so after a timeout/cancellation forces a kill this still needs a short,
    /// explicitly bounded wait for the OS to finish tearing the process tree down. This is
    /// deliberately NOT <see cref="CancellationToken.None"/> with no timeout: an unbounded wait
    /// here would let a process the OS never reaps hang the verifier indefinitely even though
    /// the caller's own timeout has already elapsed.
    /// </summary>
    private static readonly TimeSpan ProcessKillWaitTimeout = TimeSpan.FromSeconds(5);

    public async Task<CosignProcessResult> RunAsync(CosignProcessCommand command, TimeSpan timeout, int maxDiagnostics, CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            return new(-1, string.Empty);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<string> output = ReadLimitedAsync(process.StandardOutput, maxDiagnostics, deadline.Token);
        Task<string> error = ReadLimitedAsync(process.StandardError, maxDiagnostics, deadline.Token);
        Exception? processFailure = null;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (Exception exception)
        {
            processFailure = exception;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                try
                {
                    using CancellationTokenSource killDeadline = new(ProcessKillWaitTimeout);
                    await process.WaitForExitAsync(killDeadline.Token);
                }
                catch (OperationCanceledException)
                {
                    // The OS did not finish reaping the killed process tree within the bounded
                    // cleanup window. Proceed rather than block indefinitely; ExitCode below is
                    // not read in this path because processFailure/timeout handling takes over.
                }
            }
        }

        Task<string[]> drain = Task.WhenAll(output, error);
        string[] diagnostics;
        try
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            diagnostics = await DrainOutputAsync(
                drain,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        catch (TimeoutException) when (processFailure is not null)
        {
            ExceptionDispatchInfo.Capture(processFailure).Throw();
            throw;
        }

        if (processFailure is not null)
        {
            ExceptionDispatchInfo.Capture(processFailure).Throw();
        }

        return new(process.ExitCode, diagnostics[0] + diagnostics[1]);
    }

    internal static async Task<string[]> DrainOutputAsync(Task<string[]> drain, TimeSpan timeout)
    {
        try
        {
            return await drain.WaitAsync(timeout, CancellationToken.None);
        }
        catch
        {
            _ = drain.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        StringBuilder captured = new(Math.Min(maximum, 4096));
        char[] buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            int remaining = maximum - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(remaining, read));
            }
        }

        return captured.ToString();
    }
}

public sealed class ProcessCosignVerifier : ISignedReleaseVerifier
{
    private const string Issuer = HostUpdateTrustRoot.CosignIssuer;
    private readonly CosignVerifierOptions options;
    private readonly ICosignProcessRunner runner;
    private readonly Func<string> directoryFactory;

    public ProcessCosignVerifier(CosignVerifierOptions options)
        : this(options, new ProcessCosignRunner(), CreateSecureDirectory)
    {
    }

    internal ProcessCosignVerifier(CosignVerifierOptions options, ICosignProcessRunner runner, Func<string> directoryFactory)
    {
        this.options = options;
        this.runner = runner;
        this.directoryFactory = directoryFactory;
    }

    public async Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
    {
        if (options.Timeout <= TimeSpan.Zero || options.MaxDiagnostics < 0)
        {
            throw new InvalidOperationException("Cosign verifier options are invalid.");
        }

        string directory = directoryFactory();
        string manifestPath = Path.Combine(directory, "manifest.json");
        string bundlePath = Path.Combine(directory, "bundle.json");
        try
        {
            await File.WriteAllBytesAsync(manifestPath, manifest.ToArray(), cancellationToken);
            await File.WriteAllBytesAsync(bundlePath, bundle.ToArray(), cancellationToken);

            // An operator-supplied Sigstore trusted root makes verification network-free (#3064).
            string[] offline = options.TrustedRootPath is { } trustedRoot ? ["--trusted-root", trustedRoot] : [];
            CosignProcessCommand command = new(
                options.ExecutablePath,
                ["verify-blob", .. offline, "--bundle", bundlePath, "--certificate-oidc-issuer", Issuer, "--certificate-identity", certificateIdentity, manifestPath]);
            return (await runner.RunAsync(command, options.Timeout, options.MaxDiagnostics, cancellationToken)).ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CreateSecureDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "printfarmer-cosign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }
}

public sealed class VerifiedGitHubReleaseMetadataProvider(GitHubSignedReleaseDiscovery discovery) : IHostUpdateMetadataProvider
{
    public async Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct)
    {
        VerifiedSignedUpdateRelease verifiedRelease = await discovery.DiscoverAsync(channel, ct) ?? throw new InvalidDataException("No verified release is available.");
        SignedUpdateManifest manifest = verifiedRelease.Manifest;
        SignedUpdateValidationResult validation = SignedUpdateManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(',', validation.Errors));
        }

        string version = manifest.Version;
        string releaseId = $"{manifest.Channel}:{version}";
        string manifestDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(verifiedRelease.ManifestBytes)).ToLowerInvariant()}";
        Dictionary<string, string> componentPlatformDigests = manifest.PlatformDigests.ToDictionary(StringComparer.Ordinal);
        Dictionary<string, string> componentIndexDigests = manifest.Services.ToDictionary(
            service => service.Id,
            service => service.Image[(service.Image.IndexOf('@', StringComparison.Ordinal) + 1)..],
            StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<string>> componentPlatforms = manifest.Services.ToDictionary(
            service => service.Id,
            service => (IReadOnlyList<string>)[.. service.Platforms ?? []],
            StringComparer.Ordinal);
        CanonicalReleaseIdentity identity = new(releaseId, version, manifest.Channel, manifest.Tag, manifest.SourceBranch, manifest.SourceCommit,
            manifest.SourceCommit, manifest.BuildId, releaseId, version, manifestDigest);
        return new(
            manifest.Channel,
            manifest.Sequence,
            true,
            identity,
            componentPlatformDigests,
            manifest.MinimumUpdaterVersion!,
            componentIndexDigests,
            componentPlatforms);
    }
}
