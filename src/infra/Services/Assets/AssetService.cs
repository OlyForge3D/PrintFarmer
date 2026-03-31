using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Assets;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Assets;

/// <summary>
/// Service implementation for OrcaSlicer asset management
/// Loads assets from the React app's public folder and provides access to asset URLs
/// </summary>
public sealed class AssetService(ILogger<AssetService> logger) : IAssetService
{
    private readonly ILogger<AssetService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly string _manifestPath = "/assets/orcaslicer/manifest.json";

    /// <summary>
    /// Get the complete asset manifest
    /// In a real implementation, this would be loaded from the React app or a static asset service
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The asset manifest or null if not available.</returns>
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
    /// <param name="manufacturerId">The manufacturer ID to get assets for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The manufacturer's assets or null if not found.</returns>
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
    /// <param name="manufacturerId">The manufacturer ID.</param>
    /// <param name="modelId">The printer model ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer asset or null if not found.</returns>
    public Task<PrinterAssetDto?> GetPrinterAssetAsync(string manufacturerId, string modelId, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manufacturerId) || string.IsNullOrWhiteSpace(modelId))
            {
                return Task.FromResult<PrinterAssetDto?>(null);
            }

            // Construct asset URL pattern
            string normalizedMfgId = NormalizeId(manufacturerId);
            string normalizedModelId = NormalizeId(modelId);

            PrinterAssetDto asset = new PrinterAssetDto
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
                asset.Cover);

            return Task.FromResult<PrinterAssetDto?>(asset);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[AssetService] Error getting printer asset {ManufacturerId}/{ModelId}",
                manufacturerId,
                modelId);
            return Task.FromResult<PrinterAssetDto?>(null);
        }
    }

    /// <summary>
    /// Get cover image URL for a printer
    /// </summary>
    /// <param name="manufacturerId">The manufacturer ID.</param>
    /// <param name="modelId">The printer model ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cover image URL or null if not found.</returns>
    public async Task<string?> GetCoverImageUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default)
    {
        PrinterAssetDto? asset = await GetPrinterAssetAsync(manufacturerId, modelId, ct);
        return asset?.Cover;
    }

    /// <summary>
    /// Get bed texture image URL for a printer
    /// </summary>
    /// <param name="manufacturerId">The manufacturer ID.</param>
    /// <param name="modelId">The printer model ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bed texture URL or null if not found.</returns>
    public async Task<string?> GetBedTextureUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default)
    {
        PrinterAssetDto? asset = await GetPrinterAssetAsync(manufacturerId, modelId, ct);
        return asset?.BedTexture;
    }

    /// <summary>
    /// Normalize ID for URL usage (lowercase, replace spaces with underscores)
    /// </summary>
    /// <param name="id">The ID to normalize.</param>
    /// <returns>The normalized ID suitable for URLs.</returns>
    private static string NormalizeId(string id)
    {
        return id
            .ToLowerInvariant()
            .Replace(' ', '_')
            .Replace("(", string.Empty)
            .Replace(")", string.Empty)
            .Replace("+", "plus")
            .Replace("&", "and");
    }
}
