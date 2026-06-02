namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Request body for importing one or more files from a Printables model URL.
/// </summary>
public sealed class PrintablesImportRequest
{
    /// <summary>Gets or sets the Printables model URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selected Printables file IDs.
    /// When omitted or empty, all files in the model are imported.
    /// </summary>
    public string[]? FileIds { get; set; }
}
