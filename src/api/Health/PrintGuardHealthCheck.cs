using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Health;

/// <summary>
/// Checks PrintGuard hub readiness and read-scope access without submitting frames
/// or invoking printer controls. Concurrent requests share a bounded cached probe.
/// </summary>
public sealed class PrintGuardHealthCheck(
    IOptionsMonitor<PrintGuardShadowSettings> settingsMonitor,
    IHttpClientFactory httpClientFactory,
    ILogger<PrintGuardHealthCheck> logger,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    public const string Name = "printguard";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private const int MaximumResponseBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private Task<HealthCheckResult>? _pending;
    private DateTimeOffset _completedAt = DateTimeOffset.MinValue;
    private (string Url, string? Token, int RequestTimeout, int CaptureTimeout)? _configuration;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrintGuardShadowSettings settings = settingsMonitor.CurrentValue;
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy("PrintGuard failure detection disabled", Data(enabled: false));
        }

        if (!settings.TryGetClassificationEndpoint(out Uri? endpoint) || endpoint is null)
        {
            return Degraded("PrintGuard configuration invalid: configure a service URL and request/capture timeouts of 1-60 seconds");
        }

        if (settings.ApiToken is not null && settings.ApiToken.Any(char.IsWhiteSpace))
        {
            return Degraded("PrintGuard configuration invalid: API token must not contain whitespace");
        }

        var configuration = (settings.ApiUrl, settings.ApiToken, settings.RequestTimeoutSeconds, settings.CaptureTimeoutSeconds);
        Task<HealthCheckResult> pending;
        lock (_gate)
        {
            if (_pending is null || _configuration != configuration
                || (_pending.IsCompleted && _clock.GetUtcNow() - _completedAt >= CacheDuration))
            {
                _configuration = configuration;
                _pending = ProbeAndCacheAsync(settings.ApiUrl, settings.ApiToken, settings.RequestTimeoutSeconds);
            }

            pending = _pending;
        }

        // Caller cancellation must not cancel or poison a shared availability probe.
        return await pending.WaitAsync(cancellationToken);
    }

    private async Task<HealthCheckResult> ProbeAndCacheAsync(string url, string? token, int requestTimeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Min(requestTimeoutSeconds, ProbeTimeout.TotalSeconds)));
        try
        {
            using HttpClient client = httpClientFactory.CreateClient("PrintGuard");
            client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using HttpResponseMessage health = await client.GetAsync(
                "api/health", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!health.IsSuccessStatusCode)
            {
                return Degraded($"PrintGuard readiness endpoint returned HTTP {(int)health.StatusCode}");
            }

            using JsonDocument healthBody = JsonDocument.Parse(await BoundedHttpContentReader.ReadAsByteArrayAsync(
                health.Content, MaximumResponseBytes, timeout.Token));
            if (healthBody.RootElement.ValueKind != JsonValueKind.Object
                || !healthBody.RootElement.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind != JsonValueKind.True
                || !healthBody.RootElement.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()))
            {
                return Degraded("PrintGuard readiness endpoint did not report a ready hub");
            }

            // /api/health is auth-exempt. This read route uses the same scope as
            // /api/v1/classify, so a healthy hub alone cannot hide a rejected token.
            using HttpResponseMessage access = await client.GetAsync(
                "api/v1/monitors", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!access.IsSuccessStatusCode)
            {
                return Degraded(access.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "PrintGuard read API access denied: check the API token and its read scope"
                    : $"PrintGuard read API returned HTTP {(int)access.StatusCode}");
            }

            using JsonDocument accessBody = JsonDocument.Parse(await BoundedHttpContentReader.ReadAsByteArrayAsync(
                access.Content, MaximumResponseBytes, timeout.Token));
            if (accessBody.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Degraded("PrintGuard read API returned an unexpected response");
            }

            return HealthCheckResult.Healthy("PrintGuard hub ready and read API accessible; classification not tested", Data());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Degraded("PrintGuard availability check timed out");
        }
        catch (HttpRequestException)
        {
            return Degraded("PrintGuard connection failed: check the service URL and network connectivity");
        }
        catch (JsonException)
        {
            return Degraded("PrintGuard availability endpoint returned invalid JSON");
        }
        catch (InvalidDataException)
        {
            return Degraded("PrintGuard availability response exceeded the 64 KB limit");
        }
        finally
        {
            lock (_gate)
            {
                _completedAt = _clock.GetUtcNow();
            }
        }
    }

    private HealthCheckResult Degraded(string description)
    {
        logger.LogWarning("{PrintGuardHealthFailure}", description);
        return HealthCheckResult.Degraded(description, data: Data());
    }

    private static Dictionary<string, object> Data(bool enabled = true) => new() { ["enabled"] = enabled };
}
