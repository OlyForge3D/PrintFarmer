using System.Collections.ObjectModel;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for automatically discovering and populating printer capabilities
/// </summary>
public interface IPrinterCapabilityDiscoveryService
{
    /// <summary>
    /// Auto-discover printer capabilities from printer API and model defaults
    /// </summary>
    /// <param name="printer">Printer entity with API connection info</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Discovered printer capabilities or null if discovery fails</returns>
    Task<PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update existing capabilities with fresh data from printer API
    /// </summary>
    /// <param name="capabilities">Existing capabilities to update</param>
    /// <param name="printer">Printer entity with API connection info</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated capabilities</returns>
    Task<PrinterCapabilities> RefreshCapabilitiesAsync(PrinterCapabilities capabilities, Printer printer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate capabilities against known printer model specifications
    /// </summary>
    /// <param name="capabilities">Capabilities to validate</param>
    /// <param name="printer">Printer with model information</param>
    /// <returns>Validation results with warnings and errors</returns>
    Task<CapabilityValidationResult> ValidateCapabilitiesAsync(PrinterCapabilities capabilities, Printer printer);

    /// <summary>
    /// Get default capabilities based on printer model
    /// </summary>
    /// <param name="printer">Printer with model information</param>
    /// <returns>Default capabilities based on model data</returns>
    Task<PrinterCapabilities?> GetModelDefaultCapabilitiesAsync(Printer printer);
}

/// <summary>
/// Result of capability validation
/// </summary>
public class CapabilityValidationResult
{
    public bool IsValid { get; set; } = true;
    internal readonly List<string> _errors = new();
    internal readonly List<string> _warnings = new();
    internal readonly List<string> _suggestions = new();
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<string> Suggestions => _suggestions;
}

/// <summary>
/// Auto-discovered capability data from printer API
/// </summary>
public class DiscoveredCapabilities
{
    public double? NozzleDiameter { get; set; }
    public double? MaxBuildVolumeX { get; set; }
    public double? MaxBuildVolumeY { get; set; }
    public double? MaxBuildVolumeZ { get; set; }
    public int? MaxHotendTemp { get; set; }
    public int? MaxBedTemp { get; set; }
    public bool? HasHeatedBed { get; set; }
    public int? NumberOfExtruders { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public string? CurrentMaterial { get; set; }
    public Dictionary<string, object> RawConfigData { get; set; } = new();
}
