namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>Probes the guarded Obico prediction endpoint, including legacy compatibility.</summary>
public interface IObicoConnectivityProbe
{
    /// <summary>Checks reachability without analyzing a printer image.</summary>
    Task<ObicoConnectivityResult> ProbeAsync(string url, string? apiKey, CancellationToken ct = default);
}

/// <summary>Connectivity diagnostics for the administrative integration-management surface.</summary>
public sealed record ObicoConnectivityResult(bool Healthy, long LatencyMs, string? ErrorMessage);
