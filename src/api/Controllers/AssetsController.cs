using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers
{
    /// <summary>
    /// API endpoints for OrcaSlicer printer assets
    /// Provides access to printer cover images and bed textures
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public sealed class AssetsController(IAssetService assetService, ILogger<AssetsController> logger) : ControllerBase
    {
        private readonly IAssetService _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        private readonly ILogger<AssetsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        /// <summary>
        /// Get asset URLs for a printer model
        /// </summary>
        /// <param name="manufacturerId">Manufacturer ID (e.g., "bambu-lab", "creality")</param>
        /// <param name="modelId">Printer model ID (e.g., "x1", "ender-3-v3")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Asset URLs for cover image and bed texture</returns>
        [HttpGet("printer/{manufacturerId}/{modelId}")]
        [ProducesResponseType(typeof(PrinterAssetDto), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<PrinterAssetDto>> GetPrinterAssetAsync(
            string manufacturerId,
            string modelId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(manufacturerId) || string.IsNullOrWhiteSpace(modelId))
            {
                _logger.LogWarning("[AssetsController] Invalid parameters: {ManufacturerId}/{ModelId}", manufacturerId, modelId);
                return BadRequest("Manufacturer ID and Model ID are required");
            }

            PrinterAssetDto? asset = await _assetService.GetPrinterAssetAsync(manufacturerId, modelId, ct);
            if (asset == null)
            {
                _logger.LogInformation("[AssetsController] Asset not found: {ManufacturerId}/{ModelId}", manufacturerId, modelId);
                return NotFound();
            }

            return Ok(asset);
        }

        /// <summary>
        /// Get cover image URL for a printer
        /// </summary>
        /// <param name="manufacturerId">Manufacturer ID</param>
        /// <param name="modelId">Printer model ID</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>URL to printer cover image</returns>
        [HttpGet("printer/{manufacturerId}/{modelId}/cover")]
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<string>> GetCoverImageAsync(
            string manufacturerId,
            string modelId,
            CancellationToken ct = default)
        {
            string? url = await _assetService.GetCoverImageUrlAsync(manufacturerId, modelId, ct);
            return url == null ? NotFound() : Ok(url);
        }

        /// <summary>
        /// Get bed texture image URL for a printer
        /// </summary>
        /// <param name="manufacturerId">Manufacturer ID</param>
        /// <param name="modelId">Printer model ID</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>URL to bed texture image</returns>
        [HttpGet("printer/{manufacturerId}/{modelId}/bed-texture")]
        [ProducesResponseType(typeof(string), 200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<string>> GetBedTextureAsync(
            string manufacturerId,
            string modelId,
            CancellationToken ct = default)
        {
            string? url = await _assetService.GetBedTextureUrlAsync(manufacturerId, modelId, ct);
            return url == null ? NotFound() : Ok(url);
        }

        /// <summary>
        /// Get asset manifest
        /// Lists all available manufacturers and printer models with their asset URLs
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Asset manifest with all available printers and asset URLs</returns>
        [HttpGet("manifest")]
        [ProducesResponseType(typeof(AssetManifestDto), 200)]
        public async Task<ActionResult<AssetManifestDto>> GetManifestAsync(CancellationToken ct = default)
        {
            AssetManifestDto? manifest = await _assetService.GetManifestAsync(ct);
            if (manifest == null)
            {
                // Return response indicating assets are available as static files
                return Ok(new AssetManifestDto
                {
                    Manufacturers = new()
                });
            }

            return Ok(manifest);
        }
    }
}
