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
#pragma warning restore CA1032

/// <summary>Maps an executor <c>ServiceId</c> to its compose service name and immutable image repository.</summary>
public sealed record HostUpdateApplyServiceMapping(string ServiceId, string ComposeServiceName, string ImageEnvironmentVariable, string ImageRepository);

/// <summary>
/// Applies a set of pinned <c>repository@sha256:digest</c> images through the existing compose
/// template/tooling. Never rewrites a mutable tag and never shells out through string
/// interpolation: image references are passed as process environment variables consumed by
/// the compose file's own <c>${VAR:?...}</c> substitution, and every process argument is passed
/// through <see cref="IHostUpdateProcessRunner"/>'s explicit argument list. Every image is first
/// staged with <c>docker image pull</c> using its immutable digest (and, for forward execution,
/// the signed platform) before <c>docker compose up --pull never</c> is allowed to mutate desired
/// state.
/// </summary>
public sealed class HostUpdateImageApplier(
    IHostUpdateProcessRunner processRunner,
    IHostUpdateExecutableResolver executableResolver,
    IReadOnlyList<string> composeFiles,
    string projectName,
    IReadOnlyDictionary<string, HostUpdateApplyServiceMapping> serviceMappings,
    TimeSpan timeout) : IHostUpdateApplyCoordinator, IHostUpdateDigestApplier
{
    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService = request.Targets.ToDictionary(t => t.ServiceId, StringComparer.Ordinal);
        return ApplyTargetsAsync(targetsByService, cancellationToken);
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
        IReadOnlyDictionary<string, string>? platformsByService = null)
    {
        IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService = digestsByService.ToDictionary(
            pair => pair.Key,
            pair => new HostUpdateExecutionTarget(
                pair.Key,
                platformsByService is not null && platformsByService.TryGetValue(pair.Key, out string? platform) ? platform : string.Empty,
                pair.Value),
            StringComparer.Ordinal);
        return ApplyTargetsAsync(targetsByService, cancellationToken);
    }

    private async Task ApplyTargetsAsync(IReadOnlyDictionary<string, HostUpdateExecutionTarget> targetsByService, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> missing = [.. targetsByService.Keys.Where(id => !serviceMappings.ContainsKey(id))];
        if (missing.Count > 0)
        {
            throw new HostUpdateApplyUnsupportedServiceException(missing);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var composeServiceNames = new List<string>();
        foreach ((string serviceId, HostUpdateExecutionTarget target) in targetsByService)
        {
            HostUpdateApplyServiceMapping mapping = serviceMappings[serviceId];
            string imageReference = $"{mapping.ImageRepository}@{target.ChildDigest}";
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
