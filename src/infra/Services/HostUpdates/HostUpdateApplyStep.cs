namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when the request references a service with no configured compose/image mapping.</summary>
public sealed class HostUpdateApplyUnsupportedServiceException(IReadOnlyList<string> serviceIds)
    : InvalidOperationException($"unsupported_service:{string.Join(',', serviceIds)}")
{
    public IReadOnlyList<string> ServiceIds { get; } = serviceIds;
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
/// through <see cref="IHostUpdateProcessRunner"/>'s explicit argument list.
/// </summary>
public sealed class HostUpdateImageApplier(
    IHostUpdateProcessRunner processRunner,
    IReadOnlyList<string> composeFiles,
    string projectName,
    IReadOnlyDictionary<string, HostUpdateApplyServiceMapping> serviceMappings,
    TimeSpan timeout) : IHostUpdateApplyCoordinator, IHostUpdateDigestApplier
{
    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digestsByService = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
        return ApplyByDigestsAsync(digestsByService, cancellationToken);
    }

    /// <summary>
    /// Applies a specific service-&gt;digest map directly, without an <see cref="HostUpdateExecutionRequest"/>.
    /// Used both by the forward apply step and by recovery's image-only rollback path, which
    /// restores prior digests recorded in <see cref="InstalledHostState"/> rather than
    /// reconstructing a new signed request.
    /// </summary>
    public async Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> missing = [.. digestsByService.Keys.Where(id => !serviceMappings.ContainsKey(id))];
        if (missing.Count > 0)
        {
            throw new HostUpdateApplyUnsupportedServiceException(missing);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var composeServiceNames = new List<string>();
        foreach ((string serviceId, string digest) in digestsByService)
        {
            HostUpdateApplyServiceMapping mapping = serviceMappings[serviceId];
            environment[mapping.ImageEnvironmentVariable] = $"{mapping.ImageRepository}@{digest}";
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

        HostUpdateProcessResult result = await processRunner.RunAsync("docker", arguments, timeout, cancellationToken, environment)
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
    Task ApplyByDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken);
}
