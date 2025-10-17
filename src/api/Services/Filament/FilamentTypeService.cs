using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Services.Filament
{
    public class FilamentTypeService : IFilamentTypeService
    {
        private readonly AppDbContext _db;
        private readonly Farm.Web.Api.Services.Interfaces.IStartupStatus _startupStatus;
        private readonly ISpoolmanService _spoolmanService;

        public FilamentTypeService(AppDbContext db, Farm.Web.Api.Services.Interfaces.IStartupStatus startupStatus, ISpoolmanService spoolmanService)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _startupStatus = startupStatus ?? throw new ArgumentNullException(nameof(startupStatus));
            _spoolmanService = spoolmanService ?? throw new ArgumentNullException(nameof(spoolmanService));
        }

        public async Task<IReadOnlyList<Shared.FilamentTypeDto>> GetFilamentTypesAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            List<Shared.FilamentTypeDto> list = await _db.FilamentTypes.AsNoTracking().OrderBy(f => f.Name)
                .Select(f => new Shared.FilamentTypeDto(f.Id, f.Name, new Shared.TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp)))
                .ToListAsync(ct);
            return list;
        }

        public async Task<Shared.FilamentPresetsDto> GetFilamentPresetsAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            Dictionary<string, Shared.TempTargets> presets = await _db.FilamentTypes
                .AsNoTracking()
                .OrderBy(f => f.Name)
                .ToDictionaryAsync(
                    f => f.Name,
                    f => new Shared.TempTargets(f.DefaultHotendTemp, f.DefaultBedTemp), ct);
            return new Shared.FilamentPresetsDto(presets);
        }

        public async Task<Shared.FilamentTypeDto> CreateFilamentTypeAsync(Shared.CreateFilamentTypeRequest req, CancellationToken ct)
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
            {
                throw new ArgumentException("Name is required", nameof(req));
            }

            string trimmed = req.Name.Trim();
            FilamentType? existing = await _db.FilamentTypes.AsNoTracking().FirstOrDefaultAsync(f => f.Name == trimmed, ct);
            if (existing is not null)
            {
                return new Shared.FilamentTypeDto(existing.Id, existing.Name, new Shared.TempTargets(existing.DefaultHotendTemp, existing.DefaultBedTemp));
            }

            FilamentType filamentType = new()
            {
                Id = Guid.NewGuid(),
                Name = trimmed,
                DefaultHotendTemp = req.DefaultTemperatures.Hotend,
                DefaultBedTemp = req.DefaultTemperatures.Bed,
                CreatedAt = DateTime.UtcNow
            };

            _ = _db.FilamentTypes.Add(filamentType);
            _ = await _db.SaveChangesAsync(ct);

            return new Shared.FilamentTypeDto(filamentType.Id, filamentType.Name, new Shared.TempTargets(filamentType.DefaultHotendTemp, filamentType.DefaultBedTemp));
        }

        public async Task UpdateFilamentTypeAsync(Guid id, Shared.UpdateFilamentTypeRequest req, CancellationToken ct)
        {
            if (req is null)
            {
                throw new ArgumentException("Request body is required", nameof(req));
            }

            FilamentType? filamentType = await _db.FilamentTypes.FindAsync(new object[] { id }, ct);
            if (filamentType is null)
            {
                throw new KeyNotFoundException("Filament type not found");
            }

            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                filamentType.Name = req.Name.Trim();
            }

            filamentType.DefaultHotendTemp = req.DefaultTemperatures.Hotend;
            filamentType.DefaultBedTemp = req.DefaultTemperatures.Bed;

            _ = await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteFilamentTypeAsync(Guid id, CancellationToken ct)
        {
            FilamentType? filamentType = await _db.FilamentTypes.FindAsync(new object[] { id }, ct);
            if (filamentType is null)
            {
                throw new KeyNotFoundException("Filament type not found");
            }

            _ = _db.FilamentTypes.Remove(filamentType);
            _ = await _db.SaveChangesAsync(ct);
        }

        public async Task SaveFilamentPresetsAsync(Shared.FilamentPresetsDto presets, CancellationToken ct)
        {
            if (presets?.Presets == null)
            {
                throw new ArgumentException("Presets are required", nameof(presets));
            }

            foreach (KeyValuePair<string, Shared.TempTargets> kvp in presets.Presets)
            {
                string name = kvp.Key.Trim();
                Shared.TempTargets tempTargets = kvp.Value;
                FilamentType? filamentType = await _db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name, ct);
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
                    _ = _db.FilamentTypes.Add(filamentType);
                }
                else
                {
                    filamentType.DefaultHotendTemp = tempTargets.Hotend;
                    filamentType.DefaultBedTemp = tempTargets.Bed;
                }
            }
            _ = await _db.SaveChangesAsync(ct);
        }

        public async Task<Shared.SpoolmanFilamentImportResult> ImportFromSpoolmanAsync(CancellationToken ct)
        {
            if (!_startupStatus.IsReady)
            {
                throw new InvalidOperationException("System is still initializing");
            }

            if (_spoolmanService.GetConfig() is not Shared.SpoolmanConfigDto config || string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                throw new InvalidOperationException("Spoolman is not configured");
            }

            IReadOnlyList<Shared.SpoolmanMaterialDto> materials = await _spoolmanService.ListMaterialsAsync(ct);

            HashSet<string> uniqueMaterials = new(StringComparer.OrdinalIgnoreCase);
            foreach (Shared.SpoolmanMaterialDto material in materials)
            {
                if (!string.IsNullOrWhiteSpace(material.Name))
                {
                    _ = uniqueMaterials.Add(material.Name.Trim());
                }
            }

            List<string> existingTypes = await _db.FilamentTypes
                .Select(ft => ft.Name)
                .ToListAsync(ct);

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

                _ = _db.FilamentTypes.Add(newFilamentType);
                importedNames.Add(materialName);
                importedCount++;
            }

            _ = await _db.SaveChangesAsync(ct);

            return new Shared.SpoolmanFilamentImportResult(
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
