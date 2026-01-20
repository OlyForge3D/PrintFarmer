namespace Farm.Web.Api.Services
{
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
}
