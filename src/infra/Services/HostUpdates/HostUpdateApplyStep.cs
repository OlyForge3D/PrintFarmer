using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when the request references a service with no configured compose/image mapping.</summary>
public sealed class HostUpdateApplyUnsupportedServiceException(IReadOnlyList<string> serviceIds)
    : InvalidOperationException($"unsupported_service:{string.Join(',', serviceIds)}")
{
    public IReadOnlyList<string> ServiceIds { get; } = serviceIds;
}

/// <summary>Thrown when a pinned-image staging pull exits with a non-zero status.</summary>
public sealed class HostUpdateImageStagingFailedException(string serviceId, int exitCode, string standardError)
    : InvalidOperationException($"image_staging_failed:{serviceId}:exit={exitCode}")
{
    public string ServiceId { get; } = serviceId;

    public int ExitCode { get; } = exitCode;

    public string StandardError { get; } = standardError;
}

/// <summary>Thrown when the pinned-image apply (docker compose) exits with a non-zero status.</summary>
public sealed class HostUpdateApplyFailedException(int exitCode, string standardError)
    : InvalidOperationException($"apply_failed:exit={exitCode}")
{
    public int ExitCode { get; } = exitCode;

    public string StandardError { get; } = standardError;
}

/// <summary>Thrown when a preloaded image cannot be proven locally before activation mutates compose state.</summary>
public sealed class HostUpdatePreloadedImageVerificationException(string serviceId, string code)
    : InvalidOperationException($"preloaded_image_unverified:{serviceId}:{code}")
{
    public string ServiceId { get; } = serviceId;

    public string Code { get; } = code;
}
#pragma warning restore CA1032

/// <summary>Maps an executor <c>ServiceId</c> to its compose service name and immutable image repository.</summary>
public sealed record HostUpdateApplyServiceMapping(string ServiceId, string ComposeServiceName, string ImageEnvironmentVariable, string ImageRepository);

/// <summary>
/// Applies a set of pinned <c>repository@sha256:digest</c> images through the existing compose
/// template/tooling. Never rewrites a mutable tag and never shells out through string
/// interpolation: image references are passed as process environment variables consumed by
/// the compose file's own <c>${VAR:?...}</c> substitution, and every process argument is passed
/// through <see cref="IHostUpdateProcessRunner"/>'s explicit argument list. Every image is first
/// staged with either a registry pull or a local preloaded-image inspection before
/// <c>docker compose up --pull never</c> is allowed to mutate desired state.
/// </summary>
public sealed class HostUpdateImageApplier(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    IReadOnlyList<string> composeFiles,
    string projectName,
    IReadOnlyDictionary<string, HostUpdateApplyServiceMapping> serviceMappings,
    TimeSpan timeout) : IHostUpdateApplyCoordinator, IHostUpdateImageSourceDigestApplier, IHostUpdateLocalImageVerifier
{
    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService = request.Targets.ToDictionary(t => t.ServiceId, StringComparer.Ordinal);
        return ApplyTargetsAsync(targetsByService, request.ImageSourceMode, cancellationToken);
    }

    public Task VerifyTargetsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService = request.Targets.ToDictionary(t => t.ServiceId, StringComparer.Ordinal);
        return VerifyTargetsAsync(targetsByService, cancellationToken);
    }

    /// <summary>
    /// Applies a specific service-&gt;digest map directly, without an <see cref="HostUpdateExecutionRequest"/>.
    /// Used by recovery's image-only rollback path, which restores prior digests and platform
    /// evidence recorded in <see cref="InstalledHostState"/> rather than reconstructing a new
    /// signed request.
    /// </summary>
    public Task ApplyByDigestsAsync(
        IReadOnlyDictionary<string, string> digestsByService,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? platformsByService = null) =>
        ApplyByDigestsAsync(digestsByService, platformsByService, HostUpdateImageSourceMode.Registry, cancellationToken);

    public Task ApplyByDigestsAsync(
        IReadOnlyDictionary<string, string> digestsByService,
        IReadOnlyDictionary<string, string>? platformsByService,
        HostUpdateImageSourceMode imageSourceMode,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService = digestsByService.ToDictionary(
            pair => pair.Key,
            pair => new HostUpdateExecutionTarget(
                pair.Key,
                platformsByService is not null && platformsByService.TryGetValue(pair.Key, out string? platform) ? platform : string.Empty,
                pair.Value),
            StringComparer.Ordinal);
        return ApplyTargetsAsync(targetsByService, imageSourceMode, cancellationToken);
    }

    private async Task ApplyTargetsAsync(IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService, HostUpdateImageSourceMode imageSourceMode, CancellationToken cancellationToken)
    {
        ValidateMappings(targetsByService);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var composeServiceNames = new List<string>();
        if (imageSourceMode == HostUpdateImageSourceMode.PreloadedLocal)
        {
            await VerifyTargetsAsync(targetsByService, cancellationToken).ConfigureAwait(false);
        }

        foreach ((string serviceId, HostUpdateExecutionTarget target) in targetsByService)
        {
            HostUpdateApplyServiceMapping mapping = serviceMappings[serviceId];
            string imageReference = $"{mapping.ImageRepository}@{target.ChildDigest}";
            if (imageSourceMode == HostUpdateImageSourceMode.Registry)
            {
                var pullArguments = new List<string> { "image", "pull" };
                if (!string.IsNullOrWhiteSpace(target.Platform))
                {
                    pullArguments.Add("--platform");
                    pullArguments.Add(target.Platform);
                }

                pullArguments.Add(imageReference);
                HostUpdateProcessResult pullResult = await processRunner.RunAsync(executableResolver.Resolve("docker"), pullArguments, timeout, cancellationToken).ConfigureAwait(false);
                if (!pullResult.Succeeded)
                {
                    throw new HostUpdateImageStagingFailedException(serviceId, pullResult.ExitCode, pullResult.StandardError);
                }
            }

            environment[mapping.ImageEnvironmentVariable] = imageReference;
            composeServiceNames.Add(mapping.ComposeServiceName);
        }

        var arguments = new List<string> { "compose" };
        foreach (string file in composeFiles)
        {
            arguments.Add("-f");
            arguments.Add(file);
        }

        arguments.AddRange(["-p", projectName, "up", "-d", "--no-build", "--pull", "never"]);
        arguments.AddRange(composeServiceNames);

        HostUpdateProcessResult result = await processRunner.RunAsync(executableResolver.Resolve("docker"), arguments, timeout, cancellationToken, environment)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new HostUpdateApplyFailedException(result.ExitCode, result.StandardError);
        }
    }

    private async Task VerifyTargetsAsync(IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService, CancellationToken cancellationToken)
    {
        ValidateMappings(targetsByService);
        foreach ((string serviceId, HostUpdateExecutionTarget target) in targetsByService)
        {
            HostUpdateApplyServiceMapping mapping = serviceMappings[serviceId];
            string imageReference = $"{mapping.ImageRepository}@{target.ChildDigest}";
            HostUpdateProcessResult inspect = await processRunner.RunAsync(
                executableResolver.Resolve("docker"),
                ["image", "inspect", imageReference, "--format", "{{json .}}"],
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!inspect.Succeeded)
            {
                throw new HostUpdatePreloadedImageVerificationException(serviceId, "missing");
            }

            VerifyInspectOutput(serviceId, target, imageReference, inspect.StandardOutput);
        }
    }

    private static void VerifyInspectOutput(string serviceId, HostUpdateExecutionTarget target, string imageReference, string standardOutput)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                root = root.GetArrayLength() == 1 ? root[0] : default;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new HostUpdatePreloadedImageVerificationException(serviceId, "inspect_invalid");
            }

            if (!root.TryGetProperty("RepoDigests", out JsonElement repoDigests) ||
                repoDigests.ValueKind != JsonValueKind.Array ||
                !repoDigests.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), imageReference, StringComparison.Ordinal)))
            {
                throw new HostUpdatePreloadedImageVerificationException(serviceId, "identity_mismatch");
            }

            (string os, string architecture, string? variant) = ParsePlatform(serviceId, target.Platform);
            string actualOs = root.TryGetProperty("Os", out JsonElement osElement) ? osElement.GetString() ?? string.Empty : string.Empty;
            string actualArchitecture = root.TryGetProperty("Architecture", out JsonElement architectureElement) ? architectureElement.GetString() ?? string.Empty : string.Empty;
            string? actualVariant = root.TryGetProperty("Variant", out JsonElement variantElement) ? variantElement.GetString() : null;
            if (!string.Equals(actualOs, os, StringComparison.Ordinal) ||
                !string.Equals(actualArchitecture, architecture, StringComparison.Ordinal) ||
                !string.Equals(actualVariant ?? string.Empty, variant ?? string.Empty, StringComparison.Ordinal))
            {
                throw new HostUpdatePreloadedImageVerificationException(serviceId, "platform_mismatch");
            }
        }
        catch (JsonException)
        {
            throw new HostUpdatePreloadedImageVerificationException(serviceId, "inspect_invalid");
        }
    }

    private static (string Os, string Architecture, string? Variant) ParsePlatform(string serviceId, string platform)
    {
        string[] parts = platform.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            2 => (parts[0], parts[1], null),
            3 => (parts[0], parts[1], parts[2]),
            _ => throw new HostUpdatePreloadedImageVerificationException(serviceId, "platform_invalid"),
        };
    }

    private void ValidateMappings(IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService)
    {
        IReadOnlyList<string> missing = [.. targetsByService.Keys.Where(id => !serviceMappings.ContainsKey(id))];
        if (missing.Count > 0)
        {
            throw new HostUpdateApplyUnsupportedServiceException(missing);
        }
    }
}

/// <summary>Runs the apply step of the host update executor.</summary>
public interface IHostUpdateApplyCoordinator
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Applies a service-&gt;digest map directly, independent of any signed execution request. Used by recovery.</summary>
public interface IHostUpdateDigestApplier
{
    Task ApplyByDigestsAsync(
        IReadOnlyDictionary<string, string> digestsByService,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? platformsByService = null);
}

/// <summary>Applies digest maps using the same image-source mode as the failed forward request.</summary>
public interface IHostUpdateImageSourceDigestApplier : IHostUpdateDigestApplier
{
    Task ApplyByDigestsAsync(
        IReadOnlyDictionary<string, string> digestsByService,
        IReadOnlyDictionary<string, string>? platformsByService,
        HostUpdateImageSourceMode imageSourceMode,
        CancellationToken cancellationToken);
}

/// <summary>Verifies that a complete request's immutable target images already exist locally.</summary>
public interface IHostUpdateLocalImageVerifier
{
    Task VerifyTargetsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}
