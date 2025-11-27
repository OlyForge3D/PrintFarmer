using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Filament;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Services.Filament
{
    public class FilamentTypeService : IFilamentTypeService
    {
        private readonly IFilamentTypeRepository _repo;
        private readonly IStartupStatus _startupStatus;
        private readonly ISpoolmanService _spoolmanService;

        public FilamentTypeService(IFilamentTypeRepository repo, IStartupStatus startupStatus, ISpoolmanService spoolmanService)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
            _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        }

        public async Task<IReadOnlyList<FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            return await _repo.GetFilamentTypesAsync(ct);
        }

        public async Task<FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            return await _repo.GetFilamentPresetsAsync(ct);
        }

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
                CreatedAt = DateTime.UtcNow
            };
            await _repo.AddFilamentTypeAsync(filamentType, ct);
            await _repo.SaveChangesAsync(ct);
            return new FilamentTypeDto(filamentType.Id, filamentType.Name, new TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp));
        }

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
            await _repo.UpdateFilamentTypeAsync(entity, ct);
            await _repo.SaveChangesAsync(ct);
        }

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
                ImportedNames: importedNames.ToArray()
            );
        }

        private static int GetDefaultHotendTemp(string material)
        {
            if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
            { return 205; }
            if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
            { return 230; }
            if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
            { return 240; }
            if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
            { return 245; }
            if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
            { return 260; }
            if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
            { return 235; }
            if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
            { return 220; }
            if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
            { return 210; }
            if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
            { return 250; }
            if (material.Contains("CARBON", StringComparison.OrdinalIgnoreCase))
            { return 260; }
            return 210;
        }

        private static int GetDefaultBedTemp(string material)
        {
            if (material.Contains("PLA", StringComparison.OrdinalIgnoreCase))
            { return 60; }
            if (material.Contains("ABS", StringComparison.OrdinalIgnoreCase))
            { return 100; }
            if (material.Contains("PETG", StringComparison.OrdinalIgnoreCase))
            { return 85; }
            if (material.Contains("ASA", StringComparison.OrdinalIgnoreCase))
            { return 100; }
            if (material.Contains("PC", StringComparison.OrdinalIgnoreCase) || material.Contains("POLYCARBONATE", StringComparison.OrdinalIgnoreCase))
            { return 110; }
            if (material.Contains("PCTG", StringComparison.OrdinalIgnoreCase))
            { return 80; }
            if (material.Contains("TPU", StringComparison.OrdinalIgnoreCase) || material.Contains("FLEX", StringComparison.OrdinalIgnoreCase))
            { return 60; }
            if (material.Contains("WOOD", StringComparison.OrdinalIgnoreCase))
            { return 65; }
            if (material.Contains("NYLON", StringComparison.OrdinalIgnoreCase))
            { return 80; }
            if (material.Contains("CARBON", StringComparison.OrdinalIgnoreCase))
            { return 100; }
            return 70;
        }
    }
}
