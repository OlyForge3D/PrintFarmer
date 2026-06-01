using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Provides preview metadata for a Printables.com model URL, without persisting anything.
/// Import (persist to DB) is handled separately in #351.
/// </summary>
public interface IPrintablesImportService
{
    /// <summary>
    /// Queries the Printables public GraphQL API for model metadata.
    /// </summary>
    /// <param name="printablesUrl">
    /// A Printables model URL — accepts both
    /// <c>https://www.printables.com/model/{id}-{slug}</c> and
    /// <c>https://www.printables.com/model/{id}</c> forms.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preview DTO ready to display in the import modal.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="printablesUrl"/> cannot be parsed as a valid Printables model URL.
    /// </exception>
    /// <exception cref="PrintablesApiException">
    /// Thrown when the Printables GraphQL API returns an error or the model is not found.
    /// </exception>
    Task<PrintablesPreviewDto> PreviewAsync(string printablesUrl, CancellationToken ct);
}
