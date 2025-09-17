using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for automatically discovering printer capabilities from various sources
/// </summary>
public class PrinterCapabilityDiscoveryService : IPrinterCapabilityDiscoveryService
{
    private readonly AppDbContext _context;
    private readonly IMoonrakerClient _moonrakerClient;
    private readonly IPrusaLinkClient _prusaClient;
    private readonly ISdcpClient _sdcpClient;
    private readonly ILogger<PrinterCapabilityDiscoveryService> _logger;

    public PrinterCapabilityDiscoveryService(
        AppDbContext context,
        IMoonrakerClient moonrakerClient,
        IPrusaLinkClient prusaClient,
        ISdcpClient sdcpClient,
        ILogger<PrinterCapabilityDiscoveryService> logger)
    {
        _context = context;
        _moonrakerClient = moonrakerClient;
        _prusaClient = prusaClient;
        _sdcpClient = sdcpClient;
        _logger = logger;
    }

    public async Task<PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting capability discovery for printer {PrinterId} ({PrinterName})", printer.Id, printer.Name);

            // Start with model defaults
            var capabilities = await GetModelDefaultCapabilitiesAsync(printer);
            if (capabilities == null)
            {
                capabilities = new PrinterCapabilities
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    IsAvailable = true,
                    LastUpdated = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }

            // Try to discover from printer API
            var discovered = await DiscoverFromPrinterApiAsync(printer, cancellationToken);
            if (discovered != null)
            {
                ApplyDiscoveredCapabilities(capabilities, discovered);
                _logger.LogInformation("Successfully discovered capabilities from printer API for {PrinterName}", printer.Name);
            }
            else
            {
                _logger.LogWarning("Failed to discover capabilities from printer API for {PrinterName}, using model defaults only", printer.Name);
            }

            capabilities.LastUpdated = DateTime.UtcNow;
            capabilities.UpdatedAt = DateTime.UtcNow;

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering capabilities for printer {PrinterId}", printer.Id);
            return null;
        }
    }

    public async Task<PrinterCapabilities> RefreshCapabilitiesAsync(PrinterCapabilities capabilities, Printer printer, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Refreshing capabilities for printer {PrinterId}", printer.Id);

            var discovered = await DiscoverFromPrinterApiAsync(printer, cancellationToken);
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
                
                _logger.LogInformation("Successfully refreshed capabilities for printer {PrinterId}", printer.Id);
            }

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing capabilities for printer {PrinterId}", printer.Id);
            return capabilities;
        }
    }

    public async Task<CapabilityValidationResult> ValidateCapabilitiesAsync(PrinterCapabilities capabilities, Printer printer)
    {
        var result = new CapabilityValidationResult();

        try
        {
            // Load printer model for validation
            var printerWithModel = await _context.Printers
                .Include(p => p.Model)
                .FirstOrDefaultAsync(p => p.Id == printer.Id);

            if (printerWithModel?.Model != null)
            {
                var model = printerWithModel.Model;

                // Validate build volume against model specifications
                if (capabilities.MaxBuildVolumeX.HasValue && model.MaxX.HasValue && 
                    capabilities.MaxBuildVolumeX > model.MaxX)
                {
                    result.Warnings.Add($"Build volume X ({capabilities.MaxBuildVolumeX}) exceeds model specification ({model.MaxX})");
                }

                if (capabilities.MaxBuildVolumeY.HasValue && model.MaxY.HasValue && 
                    capabilities.MaxBuildVolumeY > model.MaxY)
                {
                    result.Warnings.Add($"Build volume Y ({capabilities.MaxBuildVolumeY}) exceeds model specification ({model.MaxY})");
                }

                if (capabilities.MaxBuildVolumeZ.HasValue && model.MaxZ.HasValue && 
                    capabilities.MaxBuildVolumeZ > model.MaxZ)
                {
                    result.Warnings.Add($"Build volume Z ({capabilities.MaxBuildVolumeZ}) exceeds model specification ({model.MaxZ})");
                }

                // Validate nozzle diameter (common sizes)
                if (capabilities.NozzleDiameter.HasValue)
                {
                    var commonSizes = new[] { 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.6, 0.8, 1.0 };
                    if (!commonSizes.Any(size => Math.Abs(capabilities.NozzleDiameter.Value - size) < 0.01))
                    {
                        result.Warnings.Add($"Unusual nozzle diameter: {capabilities.NozzleDiameter}mm. Common sizes are: {string.Join(", ", commonSizes)}mm");
                    }
                }

                // Validate temperature ranges
                if (capabilities.MaxHotendTemp.HasValue && capabilities.MaxHotendTemp > 500)
                {
                    result.Warnings.Add($"Very high hotend temperature limit: {capabilities.MaxHotendTemp}°C");
                }

                if (capabilities.MaxBedTemp.HasValue && capabilities.MaxBedTemp > 150)
                {
                    result.Warnings.Add($"Very high bed temperature limit: {capabilities.MaxBedTemp}°C");
                }

                // Suggest missing critical capabilities
                if (!capabilities.NozzleDiameter.HasValue)
                {
                    result.Suggestions.Add("Consider specifying nozzle diameter for accurate job matching");
                }

                if (!capabilities.MaxBuildVolumeX.HasValue || !capabilities.MaxBuildVolumeY.HasValue || !capabilities.MaxBuildVolumeZ.HasValue)
                {
                    result.Suggestions.Add("Consider specifying build volume for job compatibility checking");
                }
            }

            result.IsValid = result.Errors.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating capabilities for printer {PrinterId}", printer.Id);
            result.Errors.Add("Failed to validate capabilities due to internal error");
            result.IsValid = false;
        }

        return result;
    }

    public async Task<PrinterCapabilities?> GetModelDefaultCapabilitiesAsync(Printer printer)
    {
        try
        {
            var printerWithModel = await _context.Printers
                .Include(p => p.Model)
                .Include(p => p.Manufacturer)
                .FirstOrDefaultAsync(p => p.Id == printer.Id);

            if (printerWithModel?.Model == null)
            {
                return null;
            }

            var model = printerWithModel.Model;
            var capabilities = new PrinterCapabilities
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                MaxBuildVolumeX = model.MaxX,
                MaxBuildVolumeY = model.MaxY,
                MaxBuildVolumeZ = model.MaxZ,
                IsAvailable = true,
                LastUpdated = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Set defaults based on printer type and manufacturer
            SetDefaultsByManufacturerAndModel(capabilities, printerWithModel);

            return capabilities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting model default capabilities for printer {PrinterId}", printer.Id);
            return null;
        }
    }

    private async Task<DiscoveredCapabilities?> DiscoverFromPrinterApiAsync(Printer printer, CancellationToken cancellationToken)
    {
        var backend = (PrinterBackend)printer.Backend;

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
            _logger.LogError(ex, "Failed to discover capabilities from {Backend} API for printer {PrinterId}", backend, printer.Id);
            return null;
        }
    }

    private async Task<DiscoveredCapabilities?> DiscoverFromMoonrakerAsync(Printer printer, CancellationToken cancellationToken)
    {
        try
        {
            // Try to get printer.cfg file to determine stepper configurations and features
            var printerConfigBytes = await _moonrakerClient.DownloadFileAsync(printer.ServerUrl, "config/printer.cfg", cancellationToken);
            
            if (printerConfigBytes == null)
            {
                // Try alternative config file name
                printerConfigBytes = await _moonrakerClient.DownloadFileAsync(printer.ServerUrl, "printer.cfg", cancellationToken);
            }
            
            if (printerConfigBytes == null)
            {
                return null; // Config file not accessible
            }

            var configContent = System.Text.Encoding.UTF8.GetString(printerConfigBytes);
            var discovered = new DiscoveredCapabilities();

            // Parse Klipper configuration file (INI-style format)
            discovered.MaxBuildVolumeX = ParseConfigValue(configContent, "stepper_x", "position_max", 200.0);
            discovered.MaxBuildVolumeY = ParseConfigValue(configContent, "stepper_y", "position_max", 200.0);
            discovered.MaxBuildVolumeZ = ParseConfigValue(configContent, "stepper_z", "position_max", 200.0);
            
            // Check for heated bed
            discovered.HasHeatedBed = configContent.Contains("[heater_bed]");
            
            // Check for multiple extruders and temperature ranges
            discovered.NumberOfExtruders = CountExtrudersFromConfig(configContent);
            
            // Get temperature limits
            discovered.MaxHotendTemp = (int)ParseConfigValue(configContent, "extruder", "max_temp", 250.0);
            discovered.MaxBedTemp = (int)ParseConfigValue(configContent, "heater_bed", "max_temp", 100.0);
            
            // Get nozzle diameter
            discovered.NozzleDiameter = ParseConfigValue(configContent, "extruder", "nozzle_diameter", 0.4);

            return discovered;
        }
        catch (Exception ex)
        {
            // Log error but don't expose logger dependency issue during build
            return null;
        }
    }

    private double ParseConfigValue(string configContent, string section, string key, double defaultValue)
    {
        try
        {
            // Parse INI-style configuration for Klipper
            var sectionStart = configContent.IndexOf($"[{section}]", StringComparison.OrdinalIgnoreCase);
            if (sectionStart == -1) return defaultValue;

            var sectionEnd = configContent.IndexOf('[', sectionStart + 1);
            if (sectionEnd == -1) sectionEnd = configContent.Length;

            var sectionContent = configContent.Substring(sectionStart, sectionEnd - sectionStart);
            var keyMatch = Regex.Match(sectionContent, $@"{Regex.Escape(key)}\s*[:=]\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            
            if (keyMatch.Success && double.TryParse(keyMatch.Groups[1].Value.Trim(), out var value))
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

    private int CountExtrudersFromConfig(string configContent)
    {
        int count = 0;
        // Count extruder sections: [extruder], [extruder1], [extruder2], etc.
        if (configContent.Contains("[extruder]")) count = 1;
        for (int i = 1; i < 10; i++)
        {
            if (configContent.Contains($"[extruder{i}]"))
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
            var status = await _prusaClient.GetStatusAsync(printer.ServerUrl, printer.ApiKey, cancellationToken);
            if (status == null)
            {
                return null;
            }

            var discovered = new DiscoveredCapabilities
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
            _logger.LogError(ex, "Error discovering capabilities from PrusaLink for printer {PrinterId}", printer.Id);
            return null;
        }
    }

    private Task<DiscoveredCapabilities?> DiscoverFromSdcpAsync(Printer printer, CancellationToken cancellationToken)
    {
        // SDCP provides minimal configuration data
        var discovered = new DiscoveredCapabilities
        {
            HasHeatedBed = true, // Assume heated bed for SDCP printers
            NumberOfExtruders = 1,
            SupportedMaterials = ["PLA", "PETG", "ABS"]
        };
        return Task.FromResult<DiscoveredCapabilities?>(discovered);
    }

    private void ApplyDiscoveredCapabilities(PrinterCapabilities capabilities, DiscoveredCapabilities discovered)
    {
        if (discovered.NozzleDiameter.HasValue)
            capabilities.NozzleDiameter = discovered.NozzleDiameter;

        if (discovered.MaxBuildVolumeX.HasValue)
            capabilities.MaxBuildVolumeX = discovered.MaxBuildVolumeX;

        if (discovered.MaxBuildVolumeY.HasValue)
            capabilities.MaxBuildVolumeY = discovered.MaxBuildVolumeY;

        if (discovered.MaxBuildVolumeZ.HasValue)
            capabilities.MaxBuildVolumeZ = discovered.MaxBuildVolumeZ;

        if (discovered.MaxHotendTemp.HasValue)
            capabilities.MaxHotendTemp = discovered.MaxHotendTemp;

        if (discovered.MaxBedTemp.HasValue)
            capabilities.MaxBedTemp = discovered.MaxBedTemp;

        if (discovered.HasHeatedBed.HasValue)
            capabilities.HasHeatedBed = discovered.HasHeatedBed.Value;

        if (discovered.NumberOfExtruders.HasValue)
            capabilities.NumberOfExtruders = discovered.NumberOfExtruders.Value;

        if (discovered.SupportedMaterials != null)
            capabilities.SupportedMaterials = discovered.SupportedMaterials;

        if (!string.IsNullOrEmpty(discovered.CurrentMaterial))
            capabilities.CurrentMaterial = discovered.CurrentMaterial;
    }

    private void SetDefaultsByManufacturerAndModel(PrinterCapabilities capabilities, Printer printer)
    {
        var manufacturerName = printer.Manufacturer?.Name?.ToLowerInvariant();
        var modelName = printer.Model?.Name?.ToLowerInvariant();

        // Set manufacturer-specific defaults
        switch (manufacturerName)
        {
            case "prusa":
                capabilities.HasHeatedBed = true;
                capabilities.NumberOfExtruders = 1;
                capabilities.NozzleDiameter = 0.4;
                capabilities.MaxHotendTemp = 300;
                capabilities.MaxBedTemp = 120;
                capabilities.MinHotendTemp = 170;
                capabilities.MinBedTemp = 35;
                capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG" };
                break;

            case "voron":
                capabilities.HasHeatedBed = true;
                capabilities.HasEnclosure = modelName?.Contains("v2.4") == true || modelName?.Contains("trident") == true;
                capabilities.NumberOfExtruders = 1;
                capabilities.NozzleDiameter = 0.4;
                capabilities.MaxHotendTemp = 350;
                capabilities.MaxBedTemp = 120;
                capabilities.MinHotendTemp = 180;
                capabilities.MinBedTemp = 40;
                capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG", "PA", "PPS" };
                break;

            case "ratrig":
                capabilities.HasHeatedBed = true;
                capabilities.HasEnclosure = false; // Most RatRig are open frame
                capabilities.NumberOfExtruders = modelName?.Contains("idex") == true ? 2 : 1;
                capabilities.NozzleDiameter = 0.4;
                capabilities.MaxHotendTemp = 300;
                capabilities.MaxBedTemp = 120;
                capabilities.MinHotendTemp = 180;
                capabilities.MinBedTemp = 35;
                capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS", "ASA", "PC", "PCTG" };
                break;

            case "elegoo":
                if (modelName?.Contains("centauri") == true)
                {
                    // Delta printer specifics
                    capabilities.HasHeatedBed = true;
                    capabilities.NumberOfExtruders = 1;
                    capabilities.NozzleDiameter = 0.4;
                    capabilities.MaxHotendTemp = 280;
                    capabilities.MaxBedTemp = 100;
                    capabilities.MinHotendTemp = 180;
                    capabilities.MinBedTemp = 50;
                    capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS" };
                }
                break;

            default:
                // Generic defaults
                capabilities.HasHeatedBed = true;
                capabilities.NumberOfExtruders = 1;
                capabilities.NozzleDiameter = 0.4;
                capabilities.MaxHotendTemp = 280;
                capabilities.MaxBedTemp = 100;
                capabilities.MinHotendTemp = 180;
                capabilities.MinBedTemp = 40;
                capabilities.SupportedMaterials = new[] { "PLA", "PETG", "ABS" };
                break;
        }
    }
}
