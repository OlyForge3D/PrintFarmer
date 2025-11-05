namespace Farm.Web.Api.Services.Slicing;

using Farm.Web.Shared;

/// <summary>
/// Service for exporting PrintFarmer profiles to OrcaSlicer config bundle format.
/// </summary>
public interface IOrcaBundleExportService
{
    /// <summary>
    /// Exports PrintFarmer profiles to an OrcaSlicer config bundle JSON string.
    /// </summary>
    /// <param name="request">Export configuration specifying which profiles to include.</param>
    /// <returns>Valid OrcaSlicer config bundle JSON.</returns>
    Task<string> ExportBundleAsync(ExportOrcaBundleRequest request);
}
