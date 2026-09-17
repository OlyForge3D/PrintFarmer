using System.Text.Json;

namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when one or more health checks failed to report healthy within the bounded timeout.</summary>
public sealed class HostUpdateVerificationTimeoutException(IReadOnlyList<string> failedCheckNames)
    : TimeoutException($"health_verification_timeout:{string.Join(',', failedCheckNames)}")
{
    public IReadOnlyList<string> FailedCheckNames { get; } = failedCheckNames;
}
#pragma warning restore CA1032

/// <summary>One independently named readiness signal (API, nginx, TLS, auth, schema, storage, worker, artifact, queue).</summary>
public interface IHostUpdateHealthCheck
{
    string Name { get; }

    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

/// <summary>HTTP-based health check hitting a local readiness endpoint.</summary>
public sealed class HttpHostUpdateHealthCheck(string name, HttpClient client, string relativeUrl) : IHostUpdateHealthCheck
{
    public string Name { get; } = name;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Hits the API's aggregated <c>/health</c> endpoint (which runs <c>ComprehensiveHealthCheck</c>,
/// <c>SignalRHealthCheck</c>, and <c>SpoolmanHealthCheck</c> -- see
/// <c>Farm.Web.Api.Startup.HealthCheckStartup</c>) and requires the top-level report status to
/// be exactly <c>"Healthy"</c>, never merely a 200 response: ASP.NET Core's default health check
/// middleware also returns 200 for <c>"Degraded"</c>. This closes Kane audit P0.5's core gap --
/// the previously wired <c>/healthz</c> liveness probe is a hardcoded <c>{ status = "ok" }</c>
/// response that always returns 200 unconditionally and can never detect a broken
/// database/queue/storage/worker subsystem.
/// </summary>
public sealed class AggregateHostUpdateHealthCheck(string name, HttpClient client, string relativeUrl) : IHostUpdateHealthCheck
{
    public string Name { get; } = name;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(relativeUrl, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);

            // Bishop/Hicks review (issue #2663): the real /health response is serialized with
            // Program.HealthJsonOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase), so
            // the wire property is "status", never "Status". JsonElement.TryGetProperty is
            // ordinal/case-sensitive, so looking up the PascalCase name here always missed --
            // this health check silently reported unhealthy for every real response, which was
            // caught only by feeding it an actual serialized fixture instead of a hand-built one.
            return TryGetStatusProperty(document.RootElement, out JsonElement statusElement) &&
                string.Equals(statusElement.GetString(), "Healthy", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStatusProperty(JsonElement root, out JsonElement statusElement) =>
        root.TryGetProperty("status", out statusElement) || root.TryGetProperty("Status", out statusElement);
}

/// <summary>Verifies the exact running image digest of one container against an expected pinned digest.</summary>
public sealed class DigestHostUpdateHealthCheck(
    string name,
    IHostUpdateProcessRunner processRunner,
    string containerName,
    string expectedDigest) : IHostUpdateHealthCheck
{
    public string Name { get; } = name;

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        HostUpdateProcessResult result = await processRunner.RunAsync(
            "docker",
            ["inspect", "--format", "{{index .RepoDigests 0}}", containerName],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Trim().EndsWith(expectedDigest, StringComparison.Ordinal);
    }
}

/// <summary>
/// Verifies exact running digests for every requested target plus the static readiness signals
/// (API/nginx/TLS/auth/schema/storage/worker/artifact/queue) before the executor is allowed to
/// mark the update <see cref="HostUpdateExecutionState.Completed"/> and reopen writers.
/// </summary>
public sealed class HostUpdateHealthVerifier(
    IReadOnlyList<IHostUpdateHealthCheck> staticChecks,
    Func<string, string, IHostUpdateHealthCheck> digestCheckFactory,
    TimeSpan timeout,
    TimeSpan pollInterval,
    TimeProvider? timeProvider = null) : IHostUpdateHealthVerifier, IHostUpdateDigestVerifier
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digestsByService = request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal);
        return VerifyDigestsAsync(digestsByService, cancellationToken);
    }

    /// <summary>
    /// Verifies a specific service-&gt;digest map directly. Used both by the forward verify step
    /// and by recovery to confirm a rollback or restore actually took effect before reporting
    /// <c>RolledBack</c>.
    /// </summary>
    public async Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken)
    {
        List<IHostUpdateHealthCheck> checks =
        [
            .. staticChecks,
            .. digestsByService.Select(pair => digestCheckFactory(pair.Key, pair.Value)),
        ];

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var failed = new List<string>();
            foreach (IHostUpdateHealthCheck check in checks)
            {
                if (!await check.IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    failed.Add(check.Name);
                }
            }

            if (failed.Count == 0)
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new HostUpdateVerificationTimeoutException(failed);
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Runs the verify step of the host update executor.</summary>
public interface IHostUpdateHealthVerifier
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>Verifies a service-&gt;digest map directly, independent of any signed execution request. Used by recovery.</summary>
public interface IHostUpdateDigestVerifier
{
    Task VerifyDigestsAsync(IReadOnlyDictionary<string, string> digestsByService, CancellationToken cancellationToken);
}
