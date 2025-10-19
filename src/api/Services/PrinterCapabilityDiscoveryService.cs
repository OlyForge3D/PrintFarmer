using System.Text.RegularExpressions;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for automatically discovering printer capabilities from various sources
/// </summary>
public class PrinterCapabilityDiscoveryService(
    IPrintersRepository printersRepository,
    IMoonrakerClient moonrakerClient,
    IPrusaLinkClient prusaClient,
    IUnifiedLoggingService logger) : IPrinterCapabilityDiscoveryService
{
    private readonly IPrintersRepository _printersRepository = printersRepository;
    private readonly IMoonrakerClient _moonrakerClient = moonrakerClient;
    private readonly IPrusaLinkClient _prusaClient = prusaClient;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task<Farm.Infrastructure.Domain.PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation($"Starting capability discovery for printer {printer.Id} ({printer.Name})");

            // Start with model defaults
            Farm.Infrastructure.Domain.PrinterCapabilities? capabilities = await GetModelDefaultCapabilitiesAsync(printer) ?? new Farm.Infrastructure.Domain.PrinterCapabilities
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                IsAvailable = true,
                LastUpdated = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Try to discover from printer API
            DiscoveredCapabilities? discovered = await DiscoverFromPrinterApiAsync(printer, cancellationToken);
            if (discovered != null)
            {
                ApplyDiscoveredCapabilities(capabilities, discovered);
                _logger.LogInformation($"Successfully discovered capabilities from printer API for {printer.Name}");
            }
            else
            {
                _logger.LogWarning($"Failed to discover capabilities from printer API for {printer.Name}, using model defaults only");
            }

            capabilities.LastUpdated = DateTime.UtcNow;
            capabilities.UpdatedAt = DateTime.UtcNow;

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error discovering capabilities for printer {printer.Id}");
            return null;
        }
    }

    public async Task<Farm.Infrastructure.Domain.PrinterCapabilities> RefreshCapabilitiesAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, Printer printer, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation($"Refreshing capabilities for printer {printer.Id}");

            DiscoveredCapabilities? discovered = await DiscoverFromPrinterApiAsync(printer, cancellationToken);
            if (discovered != null)
            {
                // Only update dynamic properties, preserve manual overrides for static ones
                capabilities.CurrentMaterial = discovered.CurrentMaterial;

                // Update nozzle diameter if discovered and not manually set
                if (discovered.NozzleDiameter.HasValue && !capabilities.NozzleDiameter.HasValue)
                {
                    capabilities.NozzleDiameter = discovered.NozzleDiameter;
                }

                capabilities.LastUpdated = DateTime.UtcNow;
                capabilities.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation($"Successfully refreshed capabilities for printer {printer.Id}");
            }

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error refreshing capabilities for printer {printer.Id}");
            return capabilities;
        }
    }

    public async Task<CapabilityValidationResult> ValidateCapabilitiesAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, Printer printer)
    {
        CapabilityValidationResult result = new();

        try
        {
            // Load printer model for validation
            Printer? printerWithModel = await _printersRepository.FindByIdWithIncludesAsync(printer.Id, CancellationToken.None);

            if (printerWithModel?.Model != null)
            {
                PrinterModel model = printerWithModel.Model;

                // Validate build volume against model specifications
                if (capabilities.MaxBuildVolumeX.HasValue && model.MaxX.HasValue &&
                    capabilities.MaxBuildVolumeX > model.MaxX)
                {
                    result._warnings.Add($"Build volume X ({capabilities.MaxBuildVolumeX}) exceeds model specification ({model.MaxX})");
                }

                if (capabilities.MaxBuildVolumeY.HasValue && model.MaxY.HasValue &&
                    capabilities.MaxBuildVolumeY > model.MaxY)
                {
                    result._warnings.Add($"Build volume Y ({capabilities.MaxBuildVolumeY}) exceeds model specification ({model.MaxY})");
                }

                if (capabilities.MaxBuildVolumeZ.HasValue && model.MaxZ.HasValue &&
                    capabilities.MaxBuildVolumeZ > model.MaxZ)
                {
                    result._warnings.Add($"Build volume Z ({capabilities.MaxBuildVolumeZ}) exceeds model specification ({model.MaxZ})");
                }

                // Validate nozzle diameter (common sizes)
                if (capabilities.NozzleDiameter.HasValue)
                {
                    double[] commonSizes = new[] { 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.6, 0.8, 1.0 };
                    if (!commonSizes.Any(size => Math.Abs(capabilities.NozzleDiameter.Value - size) < 0.01))
                    {
                        result._warnings.Add($"Unusual nozzle diameter: {capabilities.NozzleDiameter}mm. Common sizes are: {string.Join(", ", commonSizes)}mm");
                    }
                }

                // Validate temperature ranges
                if (capabilities.MaxHotendTemp.HasValue && capabilities.MaxHotendTemp > 500)
                {
                    result._warnings.Add($"Very high hotend temperature limit: {capabilities.MaxHotendTemp}°C");
                }

                if (capabilities.MaxBedTemp.HasValue && capabilities.MaxBedTemp > 150)
                {
                    result._warnings.Add($"Very high bed temperature limit: {capabilities.MaxBedTemp}°C");
                }

                // Suggest missing critical capabilities
                if (!capabilities.NozzleDiameter.HasValue)
                {
                    result._suggestions.Add("Consider specifying nozzle diameter for accurate job matching");
                }

                if (!capabilities.MaxBuildVolumeX.HasValue || !capabilities.MaxBuildVolumeY.HasValue || !capabilities.MaxBuildVolumeZ.HasValue)
                {
                    result._suggestions.Add("Consider specifying build volume for job compatibility checking");
                }
            }

            result.IsValid = result.Errors.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error validating capabilities for printer {printer.Id}");
            result._errors.Add("Failed to validate capabilities due to internal error");
            result.IsValid = false;
        }

        return result;
    }

    public async Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetModelDefaultCapabilitiesAsync(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        try
        {
            Printer? printerWithModel = await _printersRepository.FindByIdWithIncludesAsync(printer.Id, CancellationToken.None);

            if (printerWithModel?.Model == null)
            {
                return null;
            }

            PrinterModel model = printerWithModel.Model;
            Farm.Infrastructure.Domain.PrinterCapabilities capabilities = new()
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                // Use model capability defaults
                MaxBuildVolumeX = model.MaxX,
                MaxBuildVolumeY = model.MaxY,
                MaxBuildVolumeZ = model.MaxZ,
                NozzleDiameter = model.DefaultNozzleDiameter,
                HasHeatedBed = model.HasHeatedBed,
                HasEnclosure = model.HasEnclosure,
                MultiMaterial = model.MultiMaterial,
                NumberOfExtruders = model.NumberOfExtruders,
                SupportsAutoLeveling = model.SupportsAutoLeveling,
                MinHotendTemp = model.MinHotendTemp,
                MaxHotendTemp = model.MaxHotendTemp,
                MinBedTemp = model.MinBedTemp,
                MaxBedTemp = model.MaxBedTemp,
                MaxPrintSpeed = model.MaxPrintSpeed,
                IsAvailable = true,
                LastUpdated = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Get supported materials from the filament type relationships
            if (model.SupportedFilamentTypes?.Any() == true)
            {
                // Preserve original casing for display names; comparisons elsewhere should use OrdinalIgnoreCase
                capabilities.SupportedMaterials = model.SupportedFilamentTypes
                    .Where(sft => sft.FilamentType != null)
                    .Select(sft => sft.FilamentType!.Name)
                    .ToArray();
            }
            else
            {
                // Fallback to empty array if no filament types are defined
                capabilities.SupportedMaterials = Array.Empty<string>();
            }

            // Apply additional manufacturer-specific defaults (as fallback for missing model data)
            SetDefaultsByManufacturerAndModel(capabilities, printerWithModel);

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting model default capabilities for printer {printer.Id}");
            return null;
        }
    }

    private async Task<DiscoveredCapabilities?> DiscoverFromPrinterApiAsync(Printer printer, CancellationToken cancellationToken)
    {
        PrinterBackend backend = (PrinterBackend)printer.Backend;

        try
        {
            return backend switch
            {
                PrinterBackend.Moonraker => await DiscoverFromMoonrakerAsync(printer, cancellationToken),
                PrinterBackend.PrusaLink => await DiscoverFromPrusaLinkAsync(printer, cancellationToken),
                PrinterBackend.SDCP => await DiscoverFromSdcpAsync(printer, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to discover capabilities from {backend} API for printer {printer.Id}");
            return null;
        }
    }

    private async Task<DiscoveredCapabilities?> DiscoverFromMoonrakerAsync(Printer printer, CancellationToken cancellationToken)
    {
        try
        {
            // Try to get printer.cfg file to determine stepper configurations and features
            byte[]? printerConfigBytes = await _moonrakerClient.DownloadFileAsync(printer.ServerUrl, "config/printer.cfg", cancellationToken) ?? await _moonrakerClient.DownloadFileAsync(printer.ServerUrl, "printer.cfg", cancellationToken);

            if (printerConfigBytes == null)
            {
                return null; // Config file not accessible
            }

            string configContent = System.Text.Encoding.UTF8.GetString(printerConfigBytes);
            DiscoveredCapabilities discovered = new()
            {
                // Parse Klipper configuration file (INI-style format)
                MaxBuildVolumeX = ParseConfigValue(configContent, "stepper_x", "position_max", 200.0),
                MaxBuildVolumeY = ParseConfigValue(configContent, "stepper_y", "position_max", 200.0),
                MaxBuildVolumeZ = ParseConfigValue(configContent, "stepper_z", "position_max", 200.0),

                // Check for heated bed
                HasHeatedBed = configContent.Contains("[heater_bed]", StringComparison.OrdinalIgnoreCase),

                // Check for multiple extruders and temperature ranges
                NumberOfExtruders = CountExtrudersFromConfig(configContent),

                // Get temperature limits
                MaxHotendTemp = (int)ParseConfigValue(configContent, "extruder", "max_temp", 250.0),
                MaxBedTemp = (int)ParseConfigValue(configContent, "heater_bed", "max_temp", 100.0),

                // Get nozzle diameter
                NozzleDiameter = ParseConfigValue(configContent, "extruder", "nozzle_diameter", 0.4)
            };

            return discovered;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, $"Moonraker capability discovery failed (non-fatal) for printer {printer.Id}");
            return null;
        }
    }

    private static double ParseConfigValue(string configContent, string section, string key, double defaultValue)
    {
        try
        {
            // Parse INI-style configuration for Klipper
            int sectionStart = configContent.IndexOf($"[{section}]", StringComparison.OrdinalIgnoreCase);
            if (sectionStart == -1)
            {
                return defaultValue;
            }

            int sectionEnd = configContent.IndexOf('[', sectionStart + 1);
            if (sectionEnd == -1)
            {
                sectionEnd = configContent.Length;
            }

            string sectionContent = configContent[sectionStart..sectionEnd];
            Match keyMatch = Regex.Match(sectionContent, $@"{Regex.Escape(key)}\s*[:=]\s*([^\r\n]+)", RegexOptions.IgnoreCase);

            if (keyMatch.Success && double.TryParse(keyMatch.Groups[1].Value.Trim(), out double value))
            {
                return value;
            }
        }
        catch
        {
            // Ignore parsing errors and return default
        }
        return defaultValue;
    }

    private static int CountExtrudersFromConfig(string configContent)
    {
        int count = 0;
        // Count extruder sections: [extruder], [extruder1], [extruder2], etc.
        if (configContent.Contains("[extruder]", StringComparison.OrdinalIgnoreCase))
        {
            count = 1;
        }

        for (int i = 1; i < 10; i++)
        {
            if (configContent.Contains($"[extruder{i}]", StringComparison.OrdinalIgnoreCase))
            {
                count = Math.Max(count, i + 1);
            }
        }
        return Math.Max(count, 1); // Default to 1 if no extruders found
    }

    private async Task<DiscoveredCapabilities?> DiscoverFromPrusaLinkAsync(Printer printer, CancellationToken cancellationToken)
    {
        try
        {
            // PrusaLink provides less configuration data, but we can get some basic info from status
            PrusaStatus status = await _prusaClient.GetStatusAsync(printer.ServerUrl, printer.ApiKey, cancellationToken);
            if (status == null)
            {
                return null;
            }

            DiscoveredCapabilities discovered = new()
            {
                HasHeatedBed = true, // Most Prusa printers have heated beds
                NumberOfExtruders = 1, // Most Prusa printers are single extruder
                SupportedMaterials = ["PLA", "PETG", "ABS", "ASA"], // Common Prusa materials
                MaxHotendTemp = 280, // Typical Prusa hotend max temp
                MaxBedTemp = 100, // Typical Prusa bed max temp
                NozzleDiameter = 0.4 // Standard Prusa nozzle diameter
            };

            return discovered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error discovering capabilities from PrusaLink for printer {printer.Id}");
            return null;
        }
    }

#pragma warning disable S1172 // Unused method parameters should be removed
    private static Task<DiscoveredCapabilities?> DiscoverFromSdcpAsync(Printer printer, CancellationToken cancellationToken)
#pragma warning restore S1172 // Unused method parameters should be removed
    {
        // SDCP provides minimal configuration data
        DiscoveredCapabilities discovered = new()
        {
            HasHeatedBed = true, // Assume heated bed for SDCP printers
            NumberOfExtruders = 1,
            SupportedMaterials = ["PLA", "PETG", "ABS"]
        };
        return Task.FromResult<DiscoveredCapabilities?>(discovered);
    }

    private static void ApplyDiscoveredCapabilities(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, DiscoveredCapabilities discovered)
    {
        if (discovered.NozzleDiameter.HasValue)
        {
            capabilities.NozzleDiameter = discovered.NozzleDiameter;
        }

        if (discovered.MaxBuildVolumeX.HasValue)
        {
            capabilities.MaxBuildVolumeX = discovered.MaxBuildVolumeX;
        }

        if (discovered.MaxBuildVolumeY.HasValue)
        {
            capabilities.MaxBuildVolumeY = discovered.MaxBuildVolumeY;
        }

        if (discovered.MaxBuildVolumeZ.HasValue)
        {
            capabilities.MaxBuildVolumeZ = discovered.MaxBuildVolumeZ;
        }

        if (discovered.MaxHotendTemp.HasValue)
        {
            capabilities.MaxHotendTemp = discovered.MaxHotendTemp;
        }

        if (discovered.MaxBedTemp.HasValue)
        {
            capabilities.MaxBedTemp = discovered.MaxBedTemp;
        }

        if (discovered.HasHeatedBed.HasValue)
        {
            capabilities.HasHeatedBed = discovered.HasHeatedBed.Value;
        }

        if (discovered.NumberOfExtruders.HasValue)
        {
            capabilities.NumberOfExtruders = discovered.NumberOfExtruders.Value;
        }

        if (discovered.SupportedMaterials != null)
        {
            capabilities.SupportedMaterials = discovered.SupportedMaterials;
        }

        if (!string.IsNullOrEmpty(discovered.CurrentMaterial))
        {
            capabilities.CurrentMaterial = discovered.CurrentMaterial;
        }
    }

    private static void SetDefaultsByManufacturerAndModel(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, Printer printer)
    {
        // Use OrdinalIgnoreCase when comparing manufacturer/model names elsewhere; avoid creating lowered copies here
        string? manufacturerName = printer.Manufacturer?.Name;
        string? modelName = printer.Model?.Name;

        // Only apply manufacturer defaults if model defaults aren't already set
        // This provides fallbacks for older model entries that may not have complete capability data

        // Set manufacturer-specific defaults as fallbacks
        switch (manufacturerName)
        {
            case "prusa":
                if (!capabilities.HasHeatedBed)
                {
                    capabilities.HasHeatedBed = true;
                }

                if (capabilities.NumberOfExtruders == 0)
                {
                    capabilities.NumberOfExtruders = 1;
                }

                if (!capabilities.NozzleDiameter.HasValue)
                {
                    capabilities.NozzleDiameter = 0.4;
                }

                if (!capabilities.MaxHotendTemp.HasValue)
                {
                    capabilities.MaxHotendTemp = 300;
                }

                if (!capabilities.MaxBedTemp.HasValue)
                {
                    capabilities.MaxBedTemp = 120;
                }

                if (!capabilities.MinHotendTemp.HasValue)
                {
                    capabilities.MinHotendTemp = 170;
                }

                if (!capabilities.MinBedTemp.HasValue)
                {
                    capabilities.MinBedTemp = 35;
                }

                if (capabilities.SupportedMaterials == null || capabilities.SupportedMaterials.Length == 0)
                {
                    capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG" };
                }

                break;

            case "voron":
                if (!capabilities.HasHeatedBed)
                {
                    capabilities.HasHeatedBed = true;
                }

                if (!capabilities.HasEnclosure)
                {
                    capabilities.HasEnclosure = modelName?.Contains("v2.4", StringComparison.OrdinalIgnoreCase) == true || modelName?.Contains("trident", StringComparison.OrdinalIgnoreCase) == true;
                }

                if (capabilities.NumberOfExtruders == 0)
                {
                    capabilities.NumberOfExtruders = 1;
                }

                if (!capabilities.NozzleDiameter.HasValue)
                {
                    capabilities.NozzleDiameter = 0.4;
                }

                if (!capabilities.MaxHotendTemp.HasValue)
                {
                    capabilities.MaxHotendTemp = 350;
                }

                if (!capabilities.MaxBedTemp.HasValue)
                {
                    capabilities.MaxBedTemp = 120;
                }

                if (!capabilities.MinHotendTemp.HasValue)
                {
                    capabilities.MinHotendTemp = 180;
                }

                if (!capabilities.MinBedTemp.HasValue)
                {
                    capabilities.MinBedTemp = 40;
                }

                if (capabilities.SupportedMaterials == null || capabilities.SupportedMaterials.Length == 0)
                {
                    capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG", "PA", "PPS" };
                }

                break;

            case "ratrig":
                if (!capabilities.HasHeatedBed)
                {
                    capabilities.HasHeatedBed = true;
                }

                if (!capabilities.HasEnclosure)
                {
                    capabilities.HasEnclosure = false; // Most RatRig are open frame
                }

                if (capabilities.NumberOfExtruders == 0)
                {
                    capabilities.NumberOfExtruders = modelName?.Contains("idex", StringComparison.OrdinalIgnoreCase) == true ? 2 : 1;
                }

                if (!capabilities.NozzleDiameter.HasValue)
                {
                    capabilities.NozzleDiameter = 0.4;
                }

                if (!capabilities.MaxHotendTemp.HasValue)
                {
                    capabilities.MaxHotendTemp = 300;
                }

                if (!capabilities.MaxBedTemp.HasValue)
                {
                    capabilities.MaxBedTemp = 120;
                }

                if (!capabilities.MinHotendTemp.HasValue)
                {
                    capabilities.MinHotendTemp = 180;
                }

                if (!capabilities.MinBedTemp.HasValue)
                {
                    capabilities.MinBedTemp = 35;
                }

                if (capabilities.SupportedMaterials == null || capabilities.SupportedMaterials.Length == 0)
                {
                    capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG" };
                }

                break;

            case "elegoo":
                if (modelName?.Contains("centauri", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Delta printer specifics
                    if (!capabilities.HasHeatedBed)
                    {
                        capabilities.HasHeatedBed = true;
                    }

                    if (capabilities.NumberOfExtruders == 0)
                    {
                        capabilities.NumberOfExtruders = 1;
                    }

                    if (!capabilities.NozzleDiameter.HasValue)
                    {
                        capabilities.NozzleDiameter = 0.4;
                    }

                    if (!capabilities.MaxHotendTemp.HasValue)
                    {
                        capabilities.MaxHotendTemp = 280;
                    }

                    if (!capabilities.MaxBedTemp.HasValue)
                    {
                        capabilities.MaxBedTemp = 100;
                    }

                    if (!capabilities.MinHotendTemp.HasValue)
                    {
                        capabilities.MinHotendTemp = 180;
                    }

                    if (!capabilities.MinBedTemp.HasValue)
                    {
                        capabilities.MinBedTemp = 50;
                    }

                    if (capabilities.SupportedMaterials == null || capabilities.SupportedMaterials.Length == 0)
                    {
                        capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS" };
                    }
                }
                break;

            default:
                // Generic defaults - only apply if not already set by model
                if (!capabilities.HasHeatedBed)
                {
                    capabilities.HasHeatedBed = true;
                }

                if (capabilities.NumberOfExtruders == 0)
                {
                    capabilities.NumberOfExtruders = 1;
                }

                if (!capabilities.NozzleDiameter.HasValue)
                {
                    capabilities.NozzleDiameter = 0.4;
                }

                if (!capabilities.MaxHotendTemp.HasValue)
                {
                    capabilities.MaxHotendTemp = 280;
                }

                if (!capabilities.MaxBedTemp.HasValue)
                {
                    capabilities.MaxBedTemp = 100;
                }

                if (!capabilities.MinHotendTemp.HasValue)
                {
                    capabilities.MinHotendTemp = 180;
                }

                if (!capabilities.MinBedTemp.HasValue)
                {
                    capabilities.MinBedTemp = 40;
                }

                if (capabilities.SupportedMaterials == null || capabilities.SupportedMaterials.Length == 0)
                {
                    capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS" };
                }

                break;
        }
    }
}
