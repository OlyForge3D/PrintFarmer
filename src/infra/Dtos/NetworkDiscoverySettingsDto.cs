using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Network discovery configuration
// Collection types kept as List<T> for JSON binding and Blazor forms compatibility (non-breaking).
// Interface methods return IReadOnlyList<T> to satisfy CA1002 on API surface.
#pragma warning disable CA1002 // Do not expose generic lists in public APIs (kept for JSON binding compatibility)
/// <summary>
/// Configuration for network discovery (ranges, timeouts, ports).
/// </summary>
public record NetworkDiscoverySettingsDto(
    List<string> NetworkRanges,
    int TimeoutMs = 3000,
    int MaxConcurrentScans = 20,
    List<PrinterBackend>? Backends = null)
{
    public NetworkDiscoverySettingsDto() : this(new List<string>(), 3000, 20, null)
    {
    }
}
#pragma warning restore CA1002
