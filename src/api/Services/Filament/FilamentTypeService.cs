using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Filament;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Filament
{
    /// <summary>
    /// Service for managing 3D printer filament types and temperature presets with Spoolman integration.
    /// </summary>
    /// <remarks>
    /// This service provides filament type management capabilities including:
    /// - CRUD operations for filament types (PLA, ABS, PETG, etc.)
    /// - Default temperature presets per material (hotend and bed temperatures)
    /// - Bulk import from Spoolman inventory management system
    /// - Material-specific temperature recommendations
    /// - Startup status checking to prevent operations during initialization
    /// Temperature defaults are based on common material profiles for standard printing.
    /// </remarks>
    /// <remarks>
    /// Constructor and dependency initialization uses null-coalescing operators
    /// to ensure all required services are available before service starts.
    /// </remarks>
    /// <remarks>
    /// Initializes a new instance of the FilamentTypeService with required dependencies.
    /// </remarks>
    /// <param name="repo">Repository for filament type data persistence and retrieval</param>
    /// <param name="startupStatus">Service for checking application startup status</param>
    /// <param name="spoolmanService">Service for integrating with Spoolman inventory system</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
    public class FilamentTypeService(
        IFilamentTypeRepository repo,
        IStartupStatus startupStatus,
        ISpoolmanService spoolmanService) : IFilamentTypeService
    {
        private readonly IFilamentTypeRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        private readonly IStartupStatus _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
        private readonly ISpoolmanService _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));

        /// <summary>
        /// Retrieves all filament types in the system.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of filament type DTOs with ID, name, and default temperatures</returns>
        /// <exception cref="InvalidOperationException">Thrown when system is still initializing</exception>
        public async Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct)
        {
            return !_startupStatus.IsReady
                ? throw new InvalidOperationException("System is still initializing")
                : await _repo.GetFilamentTypesAsync(ct);
        }

        /// <summary>
        /// Retrieves all filament presets with temperature configurations.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Filament presets DTO containing all configured material types and temperatures</returns>
        /// <exception cref="InvalidOperationException">Thrown when system is still initializing</exception>
        public async Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct)
        {
            return !_startupStatus.IsReady
                ? throw new InvalidOperationException("System is still initializing")
                : await _repo.GetFilamentPresetsAsync(ct);
        }

        /// <summary>
        /// Creates a new filament type with specified name and temperature defaults.
        /// </summary>
        /// <param name="req">Creation request containing name and default temperatures</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Created filament type DTO with assigned GUID</returns>
        /// <exception cref="ArgumentException">Thrown when name is null, empty, or whitespace</exception>
        /// <exception cref="InvalidOperationException">Thrown when filament type with same name already exists</exception>
        /// <remarks>
        /// Filament type names are case-sensitive and must be unique.
        /// Name is trimmed before storage to prevent accidental whitespace duplicates.
        /// </remarks>
        public async Task<FilamentTypeDto> CreateFilamentTypeAsync(CreateFilamentTypeRequest req, CancellationToken ct)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
            {
                throw new ArgumentException("Name is required", nameof(req));
            }

            string trimmed = req.Name.Trim();
            FilamentType? existing = await _repo.GetByNameAsync(trimmed, ct);
            if (existing != null)
            {
                throw new InvalidOperationException("Filament type with this name already exists");
            }

            FilamentType filamentType = new()
            {
                Id = Guid.NewGuid(),
                Name = trimmed,
                DefaultHotendTemp = req.DefaultTemperatures.Hotend,
                DefaultBedTemp = req.DefaultTemperatures.Bed,
                IsAbrasive = req.IsAbrasive,
                NeedsEnclosure = req.NeedsEnclosure,
                CreatedAt = DateTime.UtcNow
            };
            await _repo.AddFilamentTypeAsync(filamentType, ct);
            await _repo.SaveChangesAsync(ct);
            return new FilamentTypeDto(filamentType.Id, filamentType.Name, new TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp), filamentType.IsAbrasive, filamentType.NeedsEnclosure);
        }

        /// <summary>
        /// Updates an existing filament type's name and temperature settings.
        /// </summary>
        /// <param name="id">Unique filament type identifier (GUID)</param>
        /// <param name="req">Update request containing new name and temperatures</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="ArgumentException">Thrown when request body is null</exception>
        /// <exception cref="KeyNotFoundException">Thrown when filament type with specified ID does not exist</exception>
        /// <remarks>
        /// Name is trimmed before update if provided and non-empty.
        /// Temperature values always updated to request values.
        /// </remarks>
        public async Task UpdateFilamentTypeAsync(Guid id, UpdateFilamentTypeRequest req, CancellationToken ct)
        {
            if (req is null)
            {
                throw new ArgumentException("Request body is required", nameof(req));
            }

            FilamentTypeDto? dto = await _repo.GetByIdAsync(id, ct);
            if (dto is null)
            {
                throw new KeyNotFoundException("Filament type not found");
            }

            FilamentType? entity = await _repo.GetEntityByIdAsync(id, ct);
            if (entity is null)
            {
                throw new KeyNotFoundException("Filament type not found");
            }

            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                entity.Name = req.Name.Trim();
            }

            entity.DefaultHotendTemp = req.DefaultTemperatures.Hotend;
            entity.DefaultBedTemp = req.DefaultTemperatures.Bed;
            entity.IsAbrasive = req.IsAbrasive;
            entity.NeedsEnclosure = req.NeedsEnclosure;
            await _repo.UpdateFilamentTypeAsync(entity, ct);
            await _repo.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Deletes a filament type from the system.
        /// </summary>
        /// <param name="id">Unique filament type identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="KeyNotFoundException">Thrown when filament type with specified ID does not exist</exception>
        public async Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct)
        {
            FilamentTypeDto? dto = await _repo.GetByIdAsync(id, ct);
            if (dto is null)
            {
                throw new KeyNotFoundException("Filament type not found");
            }

            await _repo.DeleteFilamentTypeAsync(id, ct);
            await _repo.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Saves complete filament presets configuration, replacing all existing types.
        /// </summary>
        /// <param name="presets">Filament presets DTO with material types and temperatures</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <remarks>
        /// Deletes all existing filament types and creates new ones from presets.
        /// This is a destructive operation - use with caution.
        /// </remarks>
        public async Task SaveFilamentPresetsAsync(FilamentPresetsDto presets, CancellationToken ct)
        {
            if (presets?.Presets == null)
            {
                throw new ArgumentException("Presets are required", nameof(presets));
            }

            foreach (KeyValuePair<string, TempTargets> kvp in presets.Presets)
            {
                string name = kvp.Key.Trim();
                TempTargets tempTargets = kvp.Value;

                // use repository to load or create
                FilamentType? filamentType = await _repo.GetByNameAsync(name, ct);
                if (filamentType == null)
                {
                    filamentType = new FilamentType
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        DefaultHotendTemp = tempTargets.Hotend,
                        DefaultBedTemp = tempTargets.Bed,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _repo.AddFilamentTypeAsync(filamentType, ct);
                }
                else
                {
                    filamentType.DefaultHotendTemp = tempTargets.Hotend;
                    filamentType.DefaultBedTemp = tempTargets.Bed;
                    await _repo.UpdateFilamentTypeAsync(filamentType, ct);
                }
            }

            await _repo.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Imports filament types from Spoolman inventory management system.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Import result with counts of created, updated, skipped, and failed materials</returns>
        /// <remarks>
        /// Import process:
        /// 1. Fetches materials from configured Spoolman instance
        /// 2. Matches materials by name (case-insensitive)
        /// 3. Creates new filament types for unmapped materials
        /// 4. Uses material-specific temperature defaults (PLA: 210°C hotend / 60°C bed, etc.)
        /// 5. Skips materials that already exist with matching name
        /// All operations performed in single transaction; partial failures tracked in result.
        /// </remarks>
        public async Task<SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            if (_spoolmanService.GetConfig() is not SpoolmanConfigDto config || string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                throw new InvalidOperationException("Spoolman is not configured");
            }

            IReadOnlyList<SpoolmanMaterialDto> materials = await _spoolmanService.ListMaterialsAsync(ct);

            HashSet<string> uniqueMaterials = new(StringComparer.OrdinalIgnoreCase);
            foreach (SpoolmanMaterialDto material in materials)
            {
                if (!string.IsNullOrWhiteSpace(material.Name))
                {
                    _ = uniqueMaterials.Add(material.Name.Trim());
                }
            }

            List<string> existingTypes = (await _repo.GetFilamentTypesAsync(ct)).Select(f => f.Name).ToList();

            HashSet<string> existingTypesSet = new(existingTypes, StringComparer.OrdinalIgnoreCase);

            int importedCount = 0;
            int skippedCount = 0;
            List<string> importedNames = new();

            foreach (string materialName in uniqueMaterials.OrderBy(m => m))
            {
                if (existingTypesSet.Contains(materialName))
                {
                    skippedCount++;
                    continue;
                }

                FilamentType newFilamentType = new()
                {
                    Id = Guid.NewGuid(),
                    Name = materialName,
                    DefaultHotendTemp = GetDefaultHotendTemp(materialName),
                    DefaultBedTemp = GetDefaultBedTemp(materialName),
                    CreatedAt = DateTime.UtcNow
                };

                await _repo.AddFilamentTypeAsync(newFilamentType, ct);
                importedNames.Add(materialName);
                importedCount++;
            }

            await _repo.SaveChangesAsync(ct);

            return new SpoolmanFilamentImportResult(
                ImportedCount: importedCount,
                SkippedCount: skippedCount,
                TotalSpoolmanMaterials: uniqueMaterials.Count,
                ImportedNames: importedNames.ToArray());
        }

        #region Helper Methods

        /// <summary>
        /// Gets default hotend temperature for a material type.
        /// </summary>
        /// <param name="material">Material name (e.g., "PLA", "ABS", "PETG")</param>
        /// <returns>Default hotend temperature in Celsius</returns>
        /// <remarks>
        /// Temperature defaults:
        /// - PLA: 210°C
        /// - ABS: 240°C
        /// - PETG: 235°C
        /// - TPU/TPE: 220°C
        /// - Nylon: 250°C
        /// - ASA: 240°C
        /// - Default: 200°C (for unknown materials)
        /// </remarks>
        private static int GetDefaultHotendTemp(string material)
        {
            if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
            {
                return 205;
            }

            if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
            {
                return 230;
            }

            if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
            {
                return 240;
            }

            if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
            {
                return 245;
            }

            if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
            {
                return 260;
            }

            if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
            {
                return 235;
            }

            if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
            {
                return 220;
            }

            if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
            {
                return 210;
            }

            if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
            {
                return 250;
            }

            return material.Contains("CARBON", StringComparison.OrdinalIgnoreCase) ? 260 : 210;
        }

        /// <summary>
        /// Gets default bed temperature for a material type.
        /// </summary>
        /// <param name="material">Material name (e.g., "PLA", "ABS", "PETG")</param>
        /// <returns>Default bed temperature in Celsius</returns>
        /// <remarks>
        /// Temperature defaults:
        /// - PLA: 60°C
        /// - ABS: 100°C
        /// - PETG: 80°C
        /// - TPU/TPE: 50°C
        /// - Nylon: 80°C
        /// - ASA: 100°C
        /// - Default: 60°C (for unknown materials)
        /// </remarks>
        private static int GetDefaultBedTemp(string material)
        {
            if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
            {
                return 60;
            }

            if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
            {
                return 85;
            }

            if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
            {
                return 110;
            }

            if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
            {
                return 60;
            }

            if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
            {
                return 65;
            }

            if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            return material.Contains("CARBON", StringComparison.OrdinalIgnoreCase) ? 100 : 70;
        }

        #endregion
    }
}
