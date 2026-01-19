using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Request to start a network discovery session with optional backend filtering.
/// </summary>
public record StartDiscoveryRequest(
    IReadOnlyList<PrinterBackend>? Backends = null);
#pragma warning restore CA1002
