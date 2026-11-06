using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Web.Api.Health;

/// <summary>
/// Reports Obico backend reachability, not model accuracy or per-printer monitoring coverage.
/// Singleton lifetime coalesces anonymous health requests into one bounded probe batch.
/// </summary>
public sealed class ObicoHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<ObicoHealthCheck> logger,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    public const string Name = "obico";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private Task<HealthCheckResult>? _pending;
    private DateTimeOffset _completedAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            if (!scope.ServiceProvider.GetRequiredService<ISettingsService>().Get<ObicoSettings>().Enabled)
            {
                return HealthCheckResult.Healthy("Obico failure detection disabled", Data(enabled: false));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read Obico health configuration");
            return HealthCheckResult.Degraded("Obico failure detection availability could not be determined", data: Data());
        }

        Task<HealthCheckResult> pending;
        lock (_gate)
        {
            if (_pending is null || (_pending.IsCompleted && _clock.GetUtcNow() - _completedAt >= CacheDuration))
            {
                _pending = ProbeAndCacheAsync();
            }

            pending = _pending;
        }

        // A disconnected caller must not cancel or cache a failed shared probe.
        return await pending.WaitAsync(cancellationToken);
    }

    private async Task<HealthCheckResult> ProbeAndCacheAsync()
    {
        HealthCheckResult result;
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            result = await ProbeTargetsAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("Obico health probe batch exceeded its time budget");
            result = HealthCheckResult.Degraded("Obico failure detection availability check timed out", data: Data());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Obico health probe batch failed");
            result = HealthCheckResult.Degraded("Obico failure detection availability could not be determined", data: Data());
        }

        lock (_gate)
        {
            _completedAt = _clock.GetUtcNow();
        }

        return result;
    }

    private async Task<HealthCheckResult> ProbeTargetsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ObicoSettings settings = scope.ServiceProvider.GetRequiredService<ISettingsService>().Get<ObicoSettings>();
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy("Obico failure detection disabled", Data(enabled: false));
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probe = scope.ServiceProvider.GetRequiredService<IObicoConnectivityProbe>();
        var targets = await db.ObicoServers.AsNoTracking()
            .Where(server => server.IsEnabled)
            .Select(server => new { server.Url, server.ApiKey })
            .ToListAsync(ct);

        // The monitor falls back to the global URL (without credentials) for missing or
        // disabled assignments. It does not choose an arbitrary reachable pooled server.
        if (!string.IsNullOrWhiteSpace(settings.ObicoApiUrl))
        {
            targets.Add(new { Url = settings.ObicoApiUrl, ApiKey = (string?)null });
        }

        var distinctTargets = targets
            .Select(target => (Url: NormalizeUrl(target.Url), target.ApiKey))
            .Distinct()
            .ToArray();
        int reachable = 0;
        try
        {
            await Parallel.ForEachAsync(distinctTargets, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = ct
            }, async (target, token) =>
            {
                ObicoConnectivityResult response = await probe.ProbeAsync(target.Url, target.ApiKey, token).WaitAsync(token);
                if (response.Healthy)
                {
                    Interlocked.Increment(ref reachable);
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Obico health probe batch timed out with {ReachableCount} reachable targets", reachable);
        }

        string description = distinctTargets.Length == 0 || reachable == 0
            ? "Obico failure detection unavailable: no backend reachable"
            : reachable == distinctTargets.Length
                ? "Obico failure detection backends reachable"
                : "Obico failure detection partially available: some backends unreachable";
        Dictionary<string, object> data = Data();
        data["targetCount"] = distinctTargets.Length;
        data["reachableCount"] = reachable;
        return reachable > 0 && reachable == distinctTargets.Length
            ? HealthCheckResult.Healthy(description, data)
            : HealthCheckResult.Degraded(description, data: data);
    }

    private static string NormalizeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.AbsoluteUri.TrimEnd('/') : url;

    private static Dictionary<string, object> Data(bool enabled = true) => new() { ["enabled"] = enabled };
}
