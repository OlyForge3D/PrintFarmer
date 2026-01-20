namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Provides access to slicer assets (bed textures, bed models, cover images).
/// </summary>
public interface ISlicerAssetRegistry
{
    /// <summary>
    /// Gets asset metadata for a printer model.
    /// </summary>
    /// <param name="manufacturerName">The manufacturer name</param>
    /// <param name="modelName">The model name</param>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<SlicerAsset?> GetAssetAsync(
        string manufacturerName,
        string modelName,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all available assets in this slicer version.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the binary stream for a bed model (STL file).
    /// Returns null if not available.
    /// </summary>
    /// <param name="manufacturerName">The manufacturer name</param>
    /// <param name="modelName">The model name</param>
    Stream? GetBedModelStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the binary stream for a bed texture (SVG or PNG).
    /// Returns null if not available.
    /// </summary>
    /// <param name="manufacturerName">The manufacturer name</param>
    /// <param name="modelName">The model name</param>
    Stream? GetBedTextureStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the binary stream for a printer cover image.
    /// Returns null if not available.
    /// </summary>
    /// <param name="manufacturerName">The manufacturer name</param>
    /// <param name="modelName">The model name</param>
    Stream? GetCoverImageStream(string manufacturerName, string modelName);
}
