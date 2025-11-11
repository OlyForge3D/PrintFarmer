using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services
{
    /// <summary>
    /// DTOs for asset responses
    /// </summary>
    public class PrinterAssetDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("bedTexture")]
        public string? BedTexture { get; set; }
    }

    public class ManufacturerAssetsDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("printers")]
        public List<PrinterAssetDto> Printers { get; set; } = new();
    }

    public class AssetManifestDto
    {
        [JsonPropertyName("manufacturers")]
        public List<ManufacturerAssetsDto> Manufacturers { get; set; } = new();
    }

    /// <summary>
    /// Service interface for asset management
    /// </summary>
    public interface IAssetService
    {
        Task<AssetManifestDto?> GetManifestAsync(CancellationToken ct = default);
        Task<ManufacturerAssetsDto?> GetManufacturerAsync(string manufacturerId, CancellationToken ct = default);
        Task<PrinterAssetDto?> GetPrinterAssetAsync(string manufacturerId, string modelId, CancellationToken ct = default);
        Task<string?> GetCoverImageUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default);
        Task<string?> GetBedTextureUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default);
    }

    /// <summary>
    /// Service implementation for OrcaSlicer asset management
    /// Loads assets from the React app's public folder and provides access to asset URLs
    /// </summary>
    public sealed class AssetService : IAssetService
    {
        private readonly ILogger<AssetService> _logger;
        private readonly string _manifestPath;
        private readonly Dictionary<string, ManufacturerAssetsDto> _manufacturerCache;

        public AssetService(ILogger<AssetService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _manufacturerCache = new();

            // Manifest is served from React app public assets
            // For API usage, we'll reference URLs only (no file I/O in API)
            _manifestPath = "/assets/orcaslicer/manifest.json";
        }

        /// <summary>
        /// Get the complete asset manifest
        /// In a real implementation, this would be loaded from the React app or a static asset service
        /// </summary>
        public Task<AssetManifestDto?> GetManifestAsync(CancellationToken ct = default)
        {
            try
            {
                // The manifest is available as a static asset at:
                // GET /assets/orcaslicer/manifest.json (from React app)
                // This method is mainly for documentation purposes in the API
                _logger.LogInformation("[AssetService] Asset manifest available at {ManifestPath}", _manifestPath);
                return Task.FromResult<AssetManifestDto?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AssetService] Error loading manifest");
                return Task.FromResult<AssetManifestDto?>(null);
            }
        }

        /// <summary>
        /// Get assets for a specific manufacturer
        /// </summary>
        public Task<ManufacturerAssetsDto?> GetManufacturerAsync(string manufacturerId, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(manufacturerId))
                {
                    return Task.FromResult<ManufacturerAssetsDto?>(null);
                }

                // In a real implementation, this would load from the manifest
                // For now, we just provide the asset URL pattern
                _logger.LogInformation("[AssetService] Getting manufacturer assets for {ManufacturerId}", manufacturerId);
                return Task.FromResult<ManufacturerAssetsDto?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AssetService] Error getting manufacturer {ManufacturerId}", manufacturerId);
                return Task.FromResult<ManufacturerAssetsDto?>(null);
            }
        }

        /// <summary>
        /// Get printer asset by manufacturer and model
        /// </summary>
        public Task<PrinterAssetDto?> GetPrinterAssetAsync(string manufacturerId, string modelId, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(manufacturerId) || string.IsNullOrWhiteSpace(modelId))
                {
                    return Task.FromResult<PrinterAssetDto?>(null);
                }

                // Construct asset URL pattern
                var normalizedMfgId = NormalizeId(manufacturerId);
                var normalizedModelId = NormalizeId(modelId);

                var asset = new PrinterAssetDto
                {
                    Id = normalizedModelId,
                    Name = modelId,
                    Cover = $"/assets/orcaslicer/printers/{normalizedMfgId}/{normalizedModelId}/cover.png",
                    BedTexture = $"/assets/orcaslicer/printers/{normalizedMfgId}/{normalizedModelId}/bed-texture.png"
                };

                _logger.LogInformation(
                    "[AssetService] Printer asset: {ManufacturerId}/{ModelId} -> {CoverUrl}",
                    manufacturerId,
                    modelId,
                    asset.Cover
                );

                return Task.FromResult<PrinterAssetDto?>(asset);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[AssetService] Error getting printer asset {ManufacturerId}/{ModelId}",
                    manufacturerId,
                    modelId
                );
                return Task.FromResult<PrinterAssetDto?>(null);
            }
        }

        /// <summary>
        /// Get cover image URL for a printer
        /// </summary>
        public async Task<string?> GetCoverImageUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default)
        {
            var asset = await GetPrinterAssetAsync(manufacturerId, modelId, ct);
            return asset?.Cover;
        }

        /// <summary>
        /// Get bed texture image URL for a printer
        /// </summary>
        public async Task<string?> GetBedTextureUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default)
        {
            var asset = await GetPrinterAssetAsync(manufacturerId, modelId, ct);
            return asset?.BedTexture;
        }

        /// <summary>
        /// Normalize ID for URL usage (lowercase, replace spaces with underscores)
        /// </summary>
        private static string NormalizeId(string id)
        {
            return id
                .ToLowerInvariant()
                .Replace(' ', '_')
                .Replace("(", "")
                .Replace(")", "")
                .Replace("+", "plus")
                .Replace("&", "and");
        }
    }
}
