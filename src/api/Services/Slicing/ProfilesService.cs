using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing
{
    /// <summary>
    /// Service for managing slicer profiles with consolidated mapping and validation logic.
    /// Handles CRUD operations for SlicerProfile entities with proper error handling and logging.
    /// </summary>
    public class ProfilesService : IProfilesService
    {
        private readonly IProfilesRepository _repo;
        private readonly IUnifiedLoggingService _logger;

        public ProfilesService(IProfilesRepository repo, IUnifiedLoggingService logger)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProcessProfileResponseDto> CreateProfileAsync(CreateProcessProfileDto req, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(req);

            var (slicerType, quality) = ValidateAndParseEnums(req.SlicerType, req.Quality);

            var profile = new ProcessProfile
            {
                Id = Guid.NewGuid(),
                Name = NormalizeString(req.Name, "Untitled Profile"),
                Description = req.Description,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                RawJson = req.AdvancedSettings ?? "{}",
                SlicerType = slicerType,
                LayerHeight = req.LayerHeight,
                InfillPercentage = req.InfillPercentage,
                PrintSpeed = req.PrintSpeed,
                EnableSupports = req.EnableSupports,
                Quality = quality,
                IsDefault = req.IsDefault,
                IsPublic = req.IsPublic
            };

            await _repo.AddAsync(profile, ct);

            _logger.LogInformation($"Profile created: {profile.Id} - {profile.Name} ({profile.SlicerType})");

            return ToResponseDto(profile);
        }

        public async Task<ProcessProfileResponseDto?> GetProfileAsync(Guid id, CancellationToken ct)
        {
            var profile = await _repo.FindByIdAsync(id, ct);
            return profile is null ? null : ToResponseDto(profile);
        }

        public async Task<IReadOnlyList<SlicerProfileDto>> GetProfilesAsync(CancellationToken ct)
        {
            var profiles = await _repo.GetAllAsync(ct);
            return profiles.OrderBy(p => p.Name).Select(ToSummaryDto).ToList();
        }

        public async Task DeleteProfileAsync(Guid id, CancellationToken ct)
        {
            var profile = await _repo.FindByIdAsync(id, ct);
            if (profile is null)
            {
                throw new KeyNotFoundException($"Profile with ID {id} not found");
            }

            await _repo.RemoveAsync(profile, ct);

            _logger.LogInformation($"Profile deleted: {id} - {profile.Name}");
        }

        /// <summary>
        /// Maps ProcessProfile to ProcessProfileResponseDto with full details including timestamps.
        /// </summary>
        private static ProcessProfileResponseDto ToResponseDto(ProcessProfile profile)
        {
            return new ProcessProfileResponseDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = profile.Description,
                SlicerType = profile.SlicerType.ToString(),
                LayerHeight = profile.LayerHeight,
                InfillPercentage = profile.InfillPercentage,
                PrintSpeed = (int)profile.PrintSpeed,
                EnableSupports = profile.EnableSupports,
                Quality = profile.Quality.ToString(),
                IsDefault = profile.IsDefault,
                IsPublic = profile.IsPublic,
                CreatedAt = profile.CreatedAt,
                UpdatedAt = profile.UpdatedAt
            };
        }

        /// <summary>
        /// Maps ProcessProfile to SlicerProfileDto (summary view without timestamps).
        /// Used for list operations where minimal data is needed.
        /// </summary>
        private static SlicerProfileDto ToSummaryDto(ProcessProfile profile)
        {
            return new SlicerProfileDto
            {
                ProcessProfile = new ProcessProfileDto
                {
                    Name = profile.Name,
                    LayerHeight = profile.LayerHeight,
                    InfillPercentage = profile.InfillPercentage,
                    PrintSpeed = (int)profile.PrintSpeed,
                    Supports = profile.EnableSupports,
                    Quality = profile.Quality.ToString(),
                    Description = profile.Description,
                    Settings = string.IsNullOrEmpty(profile.AdvancedSettings)
                        ? new Dictionary<string, object>()
                        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(profile.AdvancedSettings) ?? new Dictionary<string, object>()
                }
            };
        }

        /// <summary>
        /// Validates and safely parses SlicerType and ProfileQuality enums.
        /// Returns sensible defaults (PrusaSlicer, Standard) on parse failure.
        /// </summary>
        /// <returns>Tuple of (SlicerType, ProfileQuality)</returns>
        private static (SlicerType SlicerType, ProfileQuality Quality) ValidateAndParseEnums(
            string? slicerTypeStr,
            string? qualityStr)
        {
            var slicerType = Enum.TryParse<SlicerType>(slicerTypeStr, ignoreCase: true, out var st)
                ? st
                : SlicerType.PrusaSlicer;

            var quality = Enum.TryParse<ProfileQuality>(qualityStr, ignoreCase: true, out var q)
                ? q
                : ProfileQuality.Standard;

            return (slicerType, quality);
        }

        /// <summary>
        /// Normalizes string input: trims whitespace and returns fallback if null or empty.
        /// </summary>
        private static string NormalizeString(string? input, string fallback = "")
        {
            return string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        }
    }
}
