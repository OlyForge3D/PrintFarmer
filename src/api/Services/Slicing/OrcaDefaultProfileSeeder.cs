using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Services.Slicing
{
    public interface IOrcaDefaultProfileSeeder
    {
        Task SeedAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Development-only seeder for default OrcaSlicer printer models, filament types, and slicer profiles.
    /// Idempotent: will skip if any system Orca profiles already exist.
    /// </summary>
    public class OrcaDefaultProfileSeeder : IOrcaDefaultProfileSeeder
    {
        private readonly AppDbContext _db;
        private readonly ILogger<OrcaDefaultProfileSeeder> _logger;
        private readonly IHostEnvironment _env;
        private readonly IProfileDuplicateFilter _duplicateFilter;

        public OrcaDefaultProfileSeeder(AppDbContext db, ILogger<OrcaDefaultProfileSeeder> logger, IHostEnvironment env, IProfileDuplicateFilter duplicateFilter)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _duplicateFilter = duplicateFilter ?? throw new ArgumentNullException(nameof(duplicateFilter));
        }

        public async Task SeedAsync(CancellationToken ct = default)
        {
            // Only run in Development environments (or if explicit override variable set)
            string? force = Environment.GetEnvironmentVariable("ORCA_PROFILE_SEED_FORCE");
            // Allow seeding in both Development and Testing environments (Testing hosts need profiles for integration tests)
            if (!(_env.IsDevelopment() || _env.IsEnvironment("Testing")) && !(force != null && force.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("[OrcaSeeder] Skipping seeding - not development environment.");
                return;
            }

            _logger.LogInformation("[OrcaSeeder] Starting seeding routine. Env={Env} ForceFlag={ForceFlag}", _env.EnvironmentName, force);

            // Basic pre-flight diagnostics: ensure core tables exist (best effort; swallow exceptions)
            try
            {
                int manufacturers = await _db.Manufacturers.CountAsync(ct);
                int models = await _db.Models.CountAsync(ct); // New table name
                int filaments = await _db.FilamentTypes.CountAsync(ct);
                _logger.LogDebug("[OrcaSeeder] Pre-flight counts Manufacturers={Manufacturers} Models={Models} FilamentTypes={FilamentTypes}", manufacturers, models, filaments);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OrcaSeeder] Pre-flight diagnostics failed (tables may not be created yet). Proceeding anyway.");
            }

            bool anySystemOrca = await _db.SlicerProfiles.AnyAsync(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer, ct);
            if (anySystemOrca)
            {
                _logger.LogInformation("[OrcaSeeder] System Orca profiles already present. Skipping (AnySystemOrca=true).");
                return;
            }

            _logger.LogInformation("[OrcaSeeder] Proceeding with seeding (AnySystemOrca=false). Seeding default Orca printer models, filament types, and profiles...");

            // Manufacturers
            Manufacturer bambu = await EnsureManufacturerAsync("Bambu Lab", ct);
            Manufacturer prusa = await EnsureManufacturerAsync("Prusa Research", ct);

            // Printer Models (minimal capability population)
            PrinterModel x1c = new()
            {
                Id = Guid.NewGuid(),
                Name = "X1 Carbon",
                ManufacturerId = bambu.Id,
                MaxX = 256,
                MaxY = 256,
                MaxZ = 256,
                DefaultNozzleDiameter = 0.4,
                HasHeatedBed = true,
                HasEnclosure = true,
                MultiMaterial = true,
                NumberOfExtruders = 1,
                SupportsAutoLeveling = true,
                MaxPrintSpeed = 500 // theoretical high speed capability
            };
            PrinterModel mk4 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Original Prusa MK4",
                ManufacturerId = prusa.Id,
                MaxX = 250,
                MaxY = 210,
                MaxZ = 220,
                DefaultNozzleDiameter = 0.4,
                HasHeatedBed = true,
                HasEnclosure = false,
                MultiMaterial = false,
                NumberOfExtruders = 1,
                SupportsAutoLeveling = true,
                MaxPrintSpeed = 250
            };

            await _db.Models.AddRangeAsync(new[] { x1c, mk4 }, ct);

            // Filament Types (reuse existing if already seeded by DatabaseInitializer to avoid unique constraint violations)
            FilamentType pla = await EnsureFilamentTypeAsync("PLA", 215, 60, ct);
            FilamentType petg = await EnsureFilamentTypeAsync("PETG", 240, 80, ct);
            FilamentType abs = await EnsureFilamentTypeAsync("ABS", 250, 100, ct);

            // Associate filament types with printer models
            await _db.PrinterModelFilamentTypes.AddRangeAsync(new[]
            {
                new PrinterModelFilamentType { PrinterModelId = x1c.Id, FilamentTypeId = pla.Id },
                new PrinterModelFilamentType { PrinterModelId = x1c.Id, FilamentTypeId = petg.Id },
                new PrinterModelFilamentType { PrinterModelId = x1c.Id, FilamentTypeId = abs.Id },
                new PrinterModelFilamentType { PrinterModelId = mk4.Id, FilamentTypeId = pla.Id },
                new PrinterModelFilamentType { PrinterModelId = mk4.Id, FilamentTypeId = petg.Id }
            }, ct);

            DateTime now = DateTime.UtcNow;

            // Process profile helper
            IEnumerable<SlicerProfile> CreateProfiles(PrinterModel model, string material, int nozzleTemp, int bedTemp)
            {
                yield return BuildProfile(model, material, "Fine 0.12mm", 0.12, 20, 60, nozzleTemp, bedTemp, ProfileQuality.Fine, isDefault: false, now);
                yield return BuildProfile(model, material, "Standard 0.20mm", 0.20, 20, 80, nozzleTemp, bedTemp, ProfileQuality.Standard, isDefault: true, now);
                yield return BuildProfile(model, material, "Draft 0.28mm", 0.28, 15, 120, nozzleTemp, bedTemp, ProfileQuality.Draft, isDefault: false, now);
            }

            List<SlicerProfile> profiles = new();
            profiles.AddRange(CreateProfiles(x1c, "PLA", 215, 60));
            profiles.AddRange(CreateProfiles(x1c, "PETG", 240, 80));
            profiles.AddRange(CreateProfiles(mk4, "PLA", 215, 60));
            // Added PETG profiles for MK4 (Phase 6 enhancement for broader material coverage)
            profiles.AddRange(CreateProfiles(mk4, "PETG", 240, 80));

            // Deduplicate using centralized filter service (handles composite key + hash uniqueness)
            var duplicateFilter = _duplicateFilter;
            var filtered = await duplicateFilter.FilterAsync(profiles, ct);
            int skipped = profiles.Count - filtered.Count;
            if (skipped > 0)
            {
                _logger.LogInformation("[OrcaSeeder] Skipped {Skipped} duplicate profile(s) out of {Total} candidates.", skipped, profiles.Count);
            }

            _logger.LogDebug("[OrcaSeeder] Adding {ProfileCount} profiles (expected >=12).", filtered.Count);
            await _db.SlicerProfiles.AddRangeAsync(filtered, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                int postCount = await _db.SlicerProfiles.CountAsync(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer, ct);
                _logger.LogInformation("[OrcaSeeder] Seeded {PrinterModels} printer models, {FilamentTypes} filament types, {Profiles} slicer profiles. PostSeedSystemProfileCount={PostCount}",
                    2, 3, profiles.Count, postCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OrcaSeeder] SaveChanges failed during Orca seeding.");
                try
                {
                    Console.WriteLine("[Console][OrcaSeeder] SaveChanges exception:" + Environment.NewLine + ex);
                }
                catch
                {
                    // ignored
                }
                throw; // rethrow so Program.cs can log warning
            }
        }

        private async Task<Manufacturer> EnsureManufacturerAsync(string name, CancellationToken ct)
        {
            Manufacturer? existing = await _db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing != null)
            {
                return existing;
            }
            Manufacturer m = new() { Id = Guid.NewGuid(), Name = name };
            await _db.Manufacturers.AddAsync(m, ct);
            return m;
        }

        private async Task<FilamentType> EnsureFilamentTypeAsync(string name, int nozzleTemp, int bedTemp, CancellationToken ct)
        {
            FilamentType? existing = await _db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == name, ct);
            if (existing != null)
            {
                // Optionally update temps if zero (keep existing values otherwise)
                if (!existing.DefaultHotendTemp.HasValue)
                {
                    existing.DefaultHotendTemp = nozzleTemp;
                }
                if (!existing.DefaultBedTemp.HasValue)
                {
                    existing.DefaultBedTemp = bedTemp;
                }
                return existing;
            }
            FilamentType ft = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                DefaultHotendTemp = nozzleTemp,
                DefaultBedTemp = bedTemp
            };
            await _db.FilamentTypes.AddAsync(ft, ct);
            return ft;
        }

        private static SlicerProfile BuildProfile(
            PrinterModel model,
            string material,
            string name,
            double layerHeight,
            int infill,
            double printSpeed,
            int nozzleTemp,
            int bedTemp,
            ProfileQuality quality,
            bool isDefault,
            DateTime now)
        {
            var raw = new
            {
                name,
                layer_height = layerHeight,
                infill_percentage = infill,
                material,
                nozzle_temperature = nozzleTemp,
                bed_temperature = bedTemp,
                print_speed = printSpeed,
                // Include printer model identity so hash is unique per model (avoids cross-model hash collisions)
                printer_model_id = model.Id,
                printer_model_name = model.Name
            };
            string rawJson = JsonSerializer.Serialize(raw);
            string hash = ComputeSha256(rawJson);
            return new SlicerProfile
            {
                Id = Guid.NewGuid(),
                Name = name + " " + material + " @ " + model.Name,
                Description = $"Default {quality} profile for {material} on {model.Name}",
                SlicerType = SlicerType.OrcaSlicer,
                PrinterModelId = model.Id,
                LayerHeight = layerHeight,
                InfillPercentage = infill,
                PrintSpeed = printSpeed,
                NozzleTemperature = nozzleTemp,
                BedTemperature = bedTemp,
                EnableSupports = false,
                Material = material,
                Quality = quality,
                RawJson = rawJson,
                MetadataJson = JsonSerializer.Serialize(new { layerHeight, infill, material, nozzleTemp, bedTemp, printSpeed, quality = quality.ToString() }),
                Hash = hash,
                IsDefault = isDefault,
                IsPublic = true,
                IsSystem = true,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        private static string ComputeSha256(string input)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            StringBuilder sb = new();
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
