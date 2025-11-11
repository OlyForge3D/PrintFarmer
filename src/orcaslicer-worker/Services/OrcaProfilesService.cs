using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Service for discovering and exporting OrcaSlicer profiles from the local installation.
/// OrcaSlicer stores profiles in:
/// - ~/.config/OrcaSlicer/profiles/printer/ (printer profiles)
/// - ~/.config/OrcaSlicer/profiles/filament/ (filament/material profiles)
/// - ~/.config/OrcaSlicer/profiles/process/ (process/quality profiles)
/// </summary>
public class OrcaProfilesService : ISlicerProfilesService
{
    private readonly IUnifiedLoggingService _logger;
    private readonly string _orcaConfigPath;

    public OrcaProfilesService(IUnifiedLoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // OrcaSlicer config path: ~/.config/OrcaSlicer/
        _orcaConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "OrcaSlicer");
    }

    public async Task<IList<SlicerProfileDto>> ListAvailableProfilesAsync(CancellationToken ct = default)
    {
        var profiles = new List<SlicerProfileDto>();

        try
        {
            if (!Directory.Exists(_orcaConfigPath))
            {
                _logger.LogWarning($"OrcaSlicer config directory not found: {_orcaConfigPath}");
                return profiles;
            }

            // Process profiles are typically in:
            // ~/.config/OrcaSlicer/profiles/process/*.json
            var processPath = Path.Combine(_orcaConfigPath, "profiles", "process");
            if (Directory.Exists(processPath))
            {
                var processProfiles = await LoadProfilesFromDirectoryAsync(processPath, ct);
                profiles.AddRange(processProfiles);
                _logger.LogInformation($"Found {processProfiles.Count} process profiles in {processPath}");
            }
            else
            {
                _logger.LogDebug($"Process profiles directory not found: {processPath}");
            }

            _logger.LogInformation($"Total OrcaSlicer profiles discovered: {profiles.Count}");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error listing OrcaSlicer profiles: {ex.Message}");
            return profiles;
        }
    }

    private async Task<IList<SlicerProfileDto>> LoadProfilesFromDirectoryAsync(string dirPath, CancellationToken ct)
    {
        var profiles = new List<SlicerProfileDto>();

        try
        {
            if (!Directory.Exists(dirPath))
            {
                return profiles;
            }

            var jsonFiles = Directory.GetFiles(dirPath, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var filePath in jsonFiles)
            {
                try
                {
                    var profile = await ParseOrcaProfileAsync(filePath, ct);
                    if (profile != null)
                    {
                        profiles.Add(profile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse OrcaSlicer profile {filePath}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error loading profiles from {dirPath}: {ex.Message}");
        }

        return profiles;
    }

    private async Task<SlicerProfileDto?> ParseOrcaProfileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var fileContent = await File.ReadAllTextAsync(filePath, ct);
            using var doc = JsonDocument.Parse(fileContent);
            var root = doc.RootElement;

            // Extract profile metadata
            string profileName = Path.GetFileNameWithoutExtension(filePath);
            string material = "Unknown";
            double layerHeight = 0.2;
            int infillPercentage = 20;
            int nozzleTemp = 200;
            int bedTemp = 60;
            string quality = "Standard";

            // Try to extract from JSON structure (varies by profile type and OrcaSlicer version)
            if (root.TryGetProperty("name", out var nameElem))
            {
                profileName = nameElem.GetString() ?? profileName;
            }

            if (root.TryGetProperty("filament_type", out var materialElem))
            {
                material = materialElem.GetString() ?? material;
            }
            else if (root.TryGetProperty("material", out var matElem))
            {
                material = matElem.GetString() ?? material;
            }

            if (root.TryGetProperty("layer_height", out var layerElem) && layerElem.TryGetDouble(out var lh))
            {
                layerHeight = lh;
            }

            if (root.TryGetProperty("fill_density", out var infillElem) && infillElem.TryGetInt32(out var inf))
            {
                infillPercentage = inf;
            }

            if (root.TryGetProperty("nozzle_temperature", out var nozzleElem) && nozzleElem.TryGetInt32(out var nt))
            {
                nozzleTemp = nt;
            }

            if (root.TryGetProperty("bed_temperature", out var bedElem) && bedElem.TryGetInt32(out var bt))
            {
                bedTemp = bt;
            }

            // Determine quality based on layer height (rough heuristic)
            if (layerHeight <= 0.15)
            {
                quality = "Fine";
            }
            else if (layerHeight >= 0.28)
            {
                quality = "Draft";
            }
            else
            {
                quality = "Standard";
            }

            return new SlicerProfileDto
            {
                LayerHeight = layerHeight,
                InfillPercentage = infillPercentage,
                NozzleTemperature = nozzleTemp,
                BedTemperature = bedTemp,
                Material = material,
                Quality = quality
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to parse OrcaSlicer profile {filePath}: {ex.Message}");
            return null;
        }
    }
}

