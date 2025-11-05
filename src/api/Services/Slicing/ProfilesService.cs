using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing
{
    public class ProfilesService : IProfilesService
    {
        private readonly IProfilesRepository _repo;
        private readonly IUnifiedLoggingService _logger;

        public ProfilesService(IProfilesRepository repo, IUnifiedLoggingService logger)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SlicerProfileResponseDto> CreateProfileAsync(CreateSlicerProfileDto req, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(req);
            var profile = new SlicerProfile
            {
                Id = Guid.NewGuid(),
                Name = req.Name ?? string.Empty,
                Description = req.Description,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AdvancedSettings = req.AdvancedSettings,
                SlicerType = Enum.TryParse<SlicerType>(req.SlicerType, true, out var st) ? st : SlicerType.PrusaSlicer,
                LayerHeight = req.LayerHeight,
                InfillPercentage = req.InfillPercentage,
                PrintSpeed = req.PrintSpeed,
                NozzleTemperature = req.NozzleTemperature,
                BedTemperature = req.BedTemperature,
                EnableSupports = req.EnableSupports,
                Material = req.Material ?? "PLA",
                Quality = Enum.TryParse<ProfileQuality>(req.Quality, true, out var q) ? q : ProfileQuality.Standard,
                IsDefault = req.IsDefault,
                IsPublic = req.IsPublic
            };

            await _repo.AddAsync(profile, ct);
            await _repo.SaveChangesAsync(ct);

            return new SlicerProfileResponseDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = profile.Description,
                SlicerType = profile.SlicerType.ToString(),
                LayerHeight = profile.LayerHeight,
                InfillPercentage = profile.InfillPercentage,
                PrintSpeed = (int)profile.PrintSpeed,
                NozzleTemperature = profile.NozzleTemperature,
                BedTemperature = profile.BedTemperature,
                EnableSupports = profile.EnableSupports,
                Material = profile.Material,
                Quality = profile.Quality.ToString(),
                IsDefault = profile.IsDefault,
                IsPublic = profile.IsPublic
            };
        }

        public async Task<SlicerProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct)
        {
            var p = await _repo.GetByIdAsync(id, ct);
            if (p == null)
            {
                return null;
            }
            return new SlicerProfileResponseDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                SlicerType = p.SlicerType.ToString(),
                LayerHeight = p.LayerHeight,
                InfillPercentage = p.InfillPercentage,
                PrintSpeed = (int)p.PrintSpeed,
                NozzleTemperature = p.NozzleTemperature,
                BedTemperature = p.BedTemperature,
                EnableSupports = p.EnableSupports,
                Material = p.Material,
                Quality = p.Quality.ToString(),
                IsDefault = p.IsDefault,
                IsPublic = p.IsPublic,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt
            };
        }

        public async Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct)
        {
            var list = await _repo.ListAsync(ct);
            return list.Select(p => new SlicerProfileDto
            {
                LayerHeight = p.LayerHeight,
                InfillPercentage = p.InfillPercentage,
                PrintSpeed = (int)p.PrintSpeed,
                NozzleTemperature = p.NozzleTemperature,
                BedTemperature = p.BedTemperature,
                Supports = p.EnableSupports,
                Material = p.Material,
                Quality = p.Quality.ToString()
            }).ToList();
        }

        public async Task DeleteProfileAsync(Guid id, CancellationToken ct)
        {
            var p = await _repo.GetByIdAsync(id, ct);
            if (p == null)
            {
                throw new KeyNotFoundException("Profile not found");
            }
            await _repo.RemoveAsync(p, ct);
            await _repo.SaveChangesAsync(ct);
        }

        // mapping helpers intentionally omitted (inline mapping used)
    }
}
