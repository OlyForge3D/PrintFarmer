using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Result of a probe attempt with confidence scoring.
/// </summary>
public record ProbeResult(
    DiscoveredPrinterDto Printer,
    int ConfidenceScore,
    string Reason);

/// <summary>
/// Interface for discovering printer backends at a given IP address.
/// Implementations are responsible for probing specific ports and endpoints.
/// </summary>
public interface INetworkDiscoveryProbe
{
    /// <summary>
    /// Display name of this probe (e.g., "Moonraker", "PrusaLink")
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// The backend this probe detects (Moonraker/PrusaLink/SDCP/OctoPrint)
    /// </summary>
    PrinterBackend Backend { get; }

    /// <summary>
    /// Attempt to discover a printer backend at the given IP/port.
    /// Returns a ProbeResult with confidence scoring, or null if not found.
    /// </summary>
    /// <param name="ipAddress">The IP address to probe.</param>
    /// <param name="timeoutMs">The timeout in milliseconds for the probe attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// Confidence scores:
    /// 100: Very high confidence - backend-specific fields validated
    /// 75: High confidence - most key fields present
    /// 50: Medium confidence - basic response structure matches
    /// 25: Low confidence - minimal match (fallback only)
    /// </remarks>
    Task<ProbeResult?> ProbeAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken);
}
