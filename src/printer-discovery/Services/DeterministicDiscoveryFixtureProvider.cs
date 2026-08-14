using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace PrinterDiscovery.Services;

/// <summary>
/// Supplies isolated discovery candidates without scanning a physical network.
/// </summary>
public interface IDeterministicDiscoveryFixtureProvider
{
    /// <summary>Whether deterministic discovery is enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns configured candidates matching an optional backend filter.</summary>
    IReadOnlyList<DiscoveredPrinterDto> GetPrinters(IEnumerable<PrinterBackend>? backends);
}

/// <summary>
/// Configuration for deterministic discovery candidates.
/// </summary>
public sealed class DeterministicDiscoveryFixtureSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Discovery:DeterministicFixtures";

    /// <summary>Whether fixture discovery replaces physical network probing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Configured deterministic candidates.</summary>
    public List<DeterministicDiscoveryPrinter> Printers { get; set; } =
    [
        new(
            "Discovered Voron V2.4",
            "moonraker-discovery-voron",
            BuildLocalUrl("moonraker-discovery-voron"),
            "Voron Design",
            "V2.4"),
        new(
            "Discovered Prusa MK4S",
            "moonraker-discovery-prusa",
            BuildLocalUrl("moonraker-discovery-prusa"),
            "Prusa Research",
            "MK4S"),
    ];

    private static string BuildLocalUrl(string host) =>
        new UriBuilder(Uri.UriSchemeHttp, host, 7125).Uri.GetLeftPart(UriPartial.Authority);
}

/// <summary>
/// Describes one deterministic Moonraker discovery candidate.
/// </summary>
public sealed record DeterministicDiscoveryPrinter(
    string Name,
    string Host,
    string ServerUrl,
    string Manufacturer,
    string Model);

/// <summary>
/// Options-backed deterministic discovery provider.
/// </summary>
public sealed class DeterministicDiscoveryFixtureProvider(
    IOptions<DeterministicDiscoveryFixtureSettings> options)
    : IDeterministicDiscoveryFixtureProvider
{
    private static readonly DateTime DeterministicDiscoveryTime =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public bool IsEnabled => options.Value.Enabled;

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredPrinterDto> GetPrinters(
        IEnumerable<PrinterBackend>? backends)
    {
        HashSet<PrinterBackend>? filter = backends?.ToHashSet();
        if (filter is { Count: > 0 } && !filter.Contains(PrinterBackend.Moonraker))
        {
            return [];
        }

        return options.Value.Printers.Select(CreatePrinter).ToArray();
    }

    private static DiscoveredPrinterDto CreatePrinter(DeterministicDiscoveryPrinter fixture)
    {
        if (string.IsNullOrWhiteSpace(fixture.Name) ||
            string.IsNullOrWhiteSpace(fixture.Host) ||
            !Uri.TryCreate(fixture.ServerUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Deterministic discovery fixtures require a name, host, and absolute HTTP URL.");
        }

        DiscoveredPrinterDto printer = DiscoveredPrinterDto.FromProbe(
            ipAddress: fixture.Host,
            serverUrl: fixture.ServerUrl,
            name: fixture.Name,
            backend: PrinterBackend.Moonraker,
            backendPort: 7125,
            frontendPort: 7125,
            manufacturer: fixture.Manufacturer,
            model: fixture.Model,
            cameraStreamUrl: $"{fixture.ServerUrl.TrimEnd('/')}/webcam/stream",
            cameraSnapshotUrl: $"{fixture.ServerUrl.TrimEnd('/')}/webcam/snapshot");
        printer.OriginalServerUrl = fixture.ServerUrl;
        printer.DiscoveredAt = DeterministicDiscoveryTime;
        return printer;
    }
}
