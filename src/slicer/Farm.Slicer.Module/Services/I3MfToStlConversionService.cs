namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for converting 3MF files to STL format for viewing.
/// </summary>
public interface I3MfToStlConversionService
{
    /// <summary>
    /// Converts a 3MF file to STL format.
    /// </summary>
    /// <param name="threeMfBytes">The 3MF file content as bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>STL file content as bytes, or <c>null</c> if conversion failed.</returns>
    Task<byte[]?> ConvertToSTLAsync(byte[] threeMfBytes, CancellationToken cancellationToken = default);
}
