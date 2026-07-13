using Farm.OrcaSlicer.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Health;

/// <summary>
/// Readiness gate for the OrcaSlicer binary. When
/// <c>Worker:VerifyBinaryVersion</c> is <c>true</c> (default) the binary's
/// self-reported version is compared to the worker's advertised engine version
/// and the worker reports Unhealthy on mismatch, preventing a v2.4.0 worker
/// from silently running a v2.3.1 binary — the exact failure mode the
/// pre-PR review flagged for issue #578.
/// </summary>
public sealed class OrcaBinaryHealthCheck(
    IOrcaBinaryDetector detector,
    IConfiguration configuration,
    WorkerCapabilityProvider capabilityProvider,
    ILogger<OrcaBinaryHealthCheck> logger) : IHealthCheck
{
    private readonly IOrcaBinaryDetector _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly WorkerCapabilityProvider _capabilityProvider = capabilityProvider ?? throw new ArgumentNullException(nameof(capabilityProvider));
    private readonly ILogger<OrcaBinaryHealthCheck> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_detector.IsRealBinaryPresent())
        {
            return HealthCheckResult.Unhealthy("OrcaSlicer binary missing or stub only");
        }

        bool verify = _configuration.GetValue("Worker:VerifyBinaryVersion", true);
        if (!verify)
        {
            return HealthCheckResult.Healthy("Real OrcaSlicer binary present (version check disabled)");
        }

        string advertised = _capabilityProvider.EngineVersion;
        if (string.Equals(advertised, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Worker:EngineVersion is unset — skipping binary version verification.");
            return HealthCheckResult.Healthy("Real OrcaSlicer binary present (advertised version unknown)");
        }

        string? binaryVersion;
        try
        {
            binaryVersion = await _detector.GetVersionAsync().WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "OrcaSlicer binary version probe failed.");
            return HealthCheckResult.Degraded($"Binary present but version probe failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(binaryVersion))
        {
            return HealthCheckResult.Degraded("Binary present but did not report a version");
        }

        if (!VersionsMatch(advertised, binaryVersion))
        {
            string message = $"OrcaSlicer binary version mismatch: worker advertises '{advertised}' but binary reports '{binaryVersion}'. Refusing to accept version-pinned jobs.";
            _logger.LogError("{Message}", message);
            return HealthCheckResult.Unhealthy(message);
        }

        return HealthCheckResult.Healthy($"Real OrcaSlicer binary present and version-matched ({binaryVersion})");
    }

    /// <summary>
    /// Compare two version strings tolerating a trailing "+build" or "-suffix"
    /// on either side (Orca sometimes reports "2.4.0+abc" while ORCASLICER_VERSION
    /// is just "2.4.0"). Parsing via System.Version keeps ordering meaningful and
    /// rejects malformed inputs like "2.3.x".
    /// </summary>
    internal static bool VersionsMatch(string advertised, string binary)
    {
        string a = Trim(advertised);
        string b = Trim(binary);
        if (System.Version.TryParse(a, out System.Version? va) &&
            System.Version.TryParse(b, out System.Version? vb))
        {
            return va == vb;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static string Trim(string s)
        {
            int cut = s.AsSpan().IndexOfAny('+', '-', ' ');
            return cut > 0 ? s[..cut].Trim() : s.Trim();
        }
    }
}
