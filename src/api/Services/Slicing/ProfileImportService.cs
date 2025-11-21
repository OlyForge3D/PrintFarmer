using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Slicing
{
    /// <summary>
    /// Service for handling profile import operations, consolidating parsing and validation logic.
    /// Coordinates ProfileParsingService and profile creation/update operations.
    /// </summary>
    public interface IProfileImportService
    {
        /// <summary>
        /// Imports a raw profile JSON and returns the processed ProcessProfile entity.
        /// Handles parsing, metadata extraction, and persistence logic.
        /// </summary>
        Task<ProcessProfile> ImportProfileAsync(
            ImportProcessProfileDto request,
            Func<ProcessProfile, Task<ProcessProfile>> persistDelegate,
            CancellationToken ct);

        /// <summary>
        /// Extracts metadata dictionary from a JSON string for display purposes.
        /// </summary>
        Dictionary<string, object?> ExtractMetadata(string? jsonStr);
    }

    public class ProfileImportService : IProfileImportService
    {
        private readonly IProfileParsingService _parsingService;
        private readonly IUnifiedLoggingService _logger;

        public ProfileImportService(IProfileParsingService parsingService, IUnifiedLoggingService logger)
        {
            _parsingService = parsingService ?? throw new ArgumentNullException(nameof(parsingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProcessProfile> ImportProfileAsync(
            ImportProcessProfileDto request,
            Func<ProcessProfile, Task<ProcessProfile>> persistDelegate,
            CancellationToken ct)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RawJson))
            {
                throw new ArgumentException("Raw JSON is required", nameof(request));
            }

            if (!Enum.TryParse<SlicerType>(request.SlicerType, ignoreCase: true, out var slicerType))
            {
                throw new ArgumentException($"Invalid slicer type: {request.SlicerType}", nameof(request));
            }

            // Parse and prepare the profile
            var (sanitizedRaw, metadataJson, hash) = _parsingService.ParseAndPrepare(request.RawJson);

            // Extract basic fields from metadata with sensible defaults
            var (layerHeight, infillPct, material, quality) = ExtractProfileDefaults(metadataJson);

            string name = NormalizeProfileName(request.Name, quality.ToString(), layerHeight);

            var imported = new ProcessProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = request.Description,
                SlicerType = slicerType,
                LayerHeight = layerHeight,
                InfillPercentage = infillPct,
                Quality = quality,
                RawJson = sanitizedRaw,
                MetadataJson = metadataJson,
                Hash = hash,
                IsPublic = request.IsPublic,
                IsDefault = request.SetDefault,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Persist using delegate (allows controller/service to decide storage strategy)
            var result = await persistDelegate(imported);

            _logger.LogInformation(
                $"Profile imported: {result.Id} - {result.Name} ({result.SlicerType}) [Hash: {result.Hash}]");

            return result;
        }

        public Dictionary<string, object?> ExtractMetadata(string? jsonStr)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(jsonStr))
            {
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number =>
                            prop.Value.TryGetInt64(out long l) ? l :
                            prop.Value.TryGetDouble(out double d) ? d : null,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse metadata JSON");
            }

            return result;
        }

        /// <summary>
        /// Extracts profile defaults from metadata JSON with fallback values.
        /// Returns (layerHeight, infillPercentage, material, quality).
        /// </summary>
        private (double LayerHeight, int InfillPct, string Material, ProfileQuality Quality) ExtractProfileDefaults(string metadataJson)
        {
            double layerHeight = 0.2;
            int infillPct = 20;
            string material = "PLA";
            string quality = "Standard";

            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                return (layerHeight, infillPct, material, ProfileQuality.Standard);
            }

            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("layerHeight", out var lh) && lh.TryGetDouble(out double lhVal))
                {
                    layerHeight = lhVal;
                }

                if (root.TryGetProperty("infillPercentage", out var inf) && inf.TryGetInt32(out int infVal))
                {
                    infillPct = infVal;
                }

                if (root.TryGetProperty("filamentMaterial", out var mat) && mat.ValueKind == JsonValueKind.String)
                {
                    material = mat.GetString() ?? material;
                }

                if (root.TryGetProperty("profileType", out var qt) && qt.ValueKind == JsonValueKind.String)
                {
                    quality = qt.GetString() ?? quality;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract profile defaults from metadata");
            }

            var parsedQuality = Enum.TryParse<ProfileQuality>(quality, ignoreCase: true, out var q)
                ? q
                : ProfileQuality.Standard;

            return (layerHeight, infillPct, material, parsedQuality);
        }

        /// <summary>
        /// Generates a meaningful profile name from quality and layer height if not provided.
        /// </summary>
        private static string NormalizeProfileName(string? name, string quality, double layerHeight)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return $"{quality} {layerHeight:0.##}mm";
        }
    }
}
