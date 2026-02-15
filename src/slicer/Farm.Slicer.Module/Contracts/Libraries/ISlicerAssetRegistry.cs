namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Provides access to slicer assets (bed textures, bed models, cover images).
/// </summary>
public interface ISlicerAssetRegistry
{
    /// <summary>
    /// Gets an asset for a specific manufacturer and model.
    /// </summary>
    /// <param name="manufacturerName">Manufacturer name (e.g., "Prusa").</param>
    /// <param name="modelName">Model name (e.g., "CORE One").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The slicer asset, or <c>null</c> if not found.</returns>
    Task<SlicerAsset?> GetAssetAsync(string manufacturerName, string modelName, CancellationToken ct = default);

    /// <summary>
    /// Lists all available slicer assets.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All registered slicer assets.</returns>
    Task<IEnumerable<SlicerAsset>> ListAssetsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the bed 3D model stream for a manufacturer and model.
    /// </summary>
    /// <param name="manufacturerName">Manufacturer name.</param>
    /// <param name="modelName">Model name.</param>
    /// <returns>The stream containing bed model data, or <c>null</c> if unavailable.</returns>
    Stream? GetBedModelStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the bed texture stream for a manufacturer and model.
    /// </summary>
    /// <param name="manufacturerName">Manufacturer name.</param>
    /// <param name="modelName">Model name.</param>
    /// <returns>The stream containing bed texture data, or <c>null</c> if unavailable.</returns>
    Stream? GetBedTextureStream(string manufacturerName, string modelName);

    /// <summary>
    /// Gets the cover image stream for a manufacturer and model.
    /// </summary>
    /// <param name="manufacturerName">Manufacturer name.</param>
    /// <param name="modelName">Model name.</param>
    /// <returns>The stream containing cover image data, or <c>null</c> if unavailable.</returns>
    Stream? GetCoverImageStream(string manufacturerName, string modelName);
}
