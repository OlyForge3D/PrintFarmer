using System.Text.Json.Serialization;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// ===========================================================================
// Network Discovery DTOs
// ===========================================================================
// DTOs for printer network discovery operations and SignalR streaming events.
// ===========================================================================

/// <summary>
/// States representing the lifecycle of a discovery session.
/// </summary>
public enum DiscoveryStatus
{
    /// <summary>Discovery session is initializing.</summary>
    Starting,

    /// <summary>Actively scanning network ranges for printers.</summary>
    Scanning,

    /// <summary>Discovery completed successfully.</summary>
    Completed,

    /// <summary>Discovery was cancelled by user.</summary>
    Cancelled,

    /// <summary>Discovery failed due to an error.</summary>
    Error
}

/// <summary>
/// Request to start a network discovery session with optional backend filtering.
/// </summary>
/// <param name="Backends">Optional list of backends to scan for. Null scans all supported backends.</param>
public record StartDiscoveryRequest(
    IReadOnlyList<PrinterBackend>? Backends = null);

/// <summary>
/// Registers a server-side discovery result without returning its network target to the client.
/// </summary>
public sealed record RegisterDiscoveredPrinterRequest(
    Guid DiscoveryId,
    Guid? ManufacturerId = null,
    Guid? ModelId = null,
    string? NewManufacturerName = null,
    string? NewModelName = null);

/// <summary>
/// Periodic progress update for an active network discovery session.
/// Published via SignalR for real-time UI updates.
/// </summary>
/// <param name="SessionId">Unique identifier for this discovery session.</param>
/// <param name="CurrentNetwork">Network range currently being scanned.</param>
/// <param name="CurrentIp">IP address currently being probed.</param>
/// <param name="TotalIps">Total number of IPs to scan.</param>
/// <param name="ScannedIps">Number of IPs scanned so far.</param>
/// <param name="PrintersFound">Number of printers discovered.</param>
/// <param name="PrintersExcluded">Number of printers excluded (already registered).</param>
/// <param name="ProgressPercentage">Overall progress as percentage (0-100).</param>
/// <param name="Status">Current discovery status.</param>
/// <param name="Message">Optional status message.</param>
/// <param name="NetworkRanges">List of network ranges being scanned.</param>
/// <param name="AutoDetectedNetworks">Whether networks were auto-detected.</param>
public record DiscoveryProgressDto(
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string CurrentNetwork,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string CurrentIp,
    int TotalIps,
    int ScannedIps,
    int PrintersFound,
    int PrintersExcluded,
    double ProgressPercentage,
    DiscoveryStatus Status,
    string? Message = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false);

/// <summary>
/// Event published when a printer is found during discovery.
/// </summary>
/// <param name="SessionId">Discovery session identifier.</param>
/// <param name="Printer">Details of the discovered printer.</param>
public record DiscoveryPrinterFoundDto(
    string SessionId,
    DiscoveredPrinterSummaryDto Printer);

/// <summary>
/// Redacted printer metadata sent to the authenticated owner of a discovery session.
/// </summary>
public sealed record DiscoveredPrinterSummaryDto(
    Guid DiscoveryId,
    string Name,
    PrinterBackend Backend,
    string? Manufacturer,
    string? Model,
    DateTime DiscoveredAt,
    bool IsReachable);

/// <summary>
/// Authenticated service-to-service request containing a discovered printer target.
/// This request is never returned to API clients or published through SignalR.
/// </summary>
public sealed record InternalDiscoveryPrinterFoundDto(
    string SessionId,
    string Name,
    string ServerUrl,
    string? OriginalServerUrl,
    string IpAddress,
    PrinterBackend Backend,
    int? BackendPort,
    int? FrontendPort,
    string? CameraStreamUrl,
    string? CameraSnapshotUrl,
    string? Manufacturer,
    string? Model,
    string? Notes,
    DateTime DiscoveredAt,
    bool IsReachable);

/// <summary>
/// Authenticated service-to-service discovery progress update without network targets.
/// </summary>
public sealed record InternalDiscoveryProgressDto(
    string SessionId,
    int TotalIps,
    int ScannedIps,
    int PrintersFound,
    int PrintersExcluded,
    double ProgressPercentage,
    DiscoveryStatus Status,
    string? Message,
    bool AutoDetectedNetworks);

/// <summary>
/// Authenticated service-to-service discovery completion update without network targets.
/// </summary>
public sealed record InternalDiscoveryCompletedDto(
    string SessionId,
    int TotalPrintersFound,
    int TotalPrintersExcluded,
    TimeSpan Duration,
    bool WasCancelled,
    bool AutoDetectedNetworks);

/// <summary>
/// Completion summary for a network discovery session.
/// </summary>
/// <param name="SessionId">Unique identifier for this discovery session.</param>
/// <param name="TotalPrintersFound">Total number of printers found.</param>
/// <param name="TotalPrintersExcluded">Number of printers excluded (already registered).</param>
/// <param name="Duration">Total time taken for discovery.</param>
/// <param name="WasCancelled">Whether discovery was cancelled by user.</param>
/// <param name="NetworkRanges">Network ranges that were scanned.</param>
/// <param name="AutoDetectedNetworks">Whether networks were auto-detected.</param>
public record DiscoveryCompletedDto(
    string SessionId,
    int TotalPrintersFound,
    int TotalPrintersExcluded,
    TimeSpan Duration,
    bool WasCancelled = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    IReadOnlyList<string>? NetworkRanges = null,
    bool AutoDetectedNetworks = false);
