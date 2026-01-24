namespace Farm.Web.Api.Services
{
    /// <summary>
    /// Service interface for managing printer asset images and manifests.
    /// </summary>
    public interface IAssetService
    {
        /// <summary>Gets the complete asset manifest containing all manufacturer and printer assets.</summary>
        Task<AssetManifestDto?> GetManifestAsync(CancellationToken ct = default);

        /// <summary>Gets assets for a specific manufacturer by ID.</summary>
        Task<ManufacturerAssetsDto?> GetManufacturerAsync(string manufacturerId, CancellationToken ct = default);

        /// <summary>Gets asset metadata for a specific printer model.</summary>
        Task<PrinterAssetDto?> GetPrinterAssetAsync(string manufacturerId, string modelId, CancellationToken ct = default);

        /// <summary>Gets the cover/product image URL for a printer model.</summary>
        Task<string?> GetCoverImageUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default);

        /// <summary>Gets the bed texture URL for a printer model (used in 3D visualization).</summary>
        Task<string?> GetBedTextureUrlAsync(string manufacturerId, string modelId, CancellationToken ct = default);
    }
}
