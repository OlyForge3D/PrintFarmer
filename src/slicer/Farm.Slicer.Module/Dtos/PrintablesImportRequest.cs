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

/// <summary>
/// Request body for one-click imports from browse/search model cards.
/// </summary>
public sealed class PrintablesOneClickImportRequest
{
    /// <summary>Gets or sets the Printables model ID from the selected card.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional model slug from the selected card.</summary>
    public string? Slug { get; set; }

    /// <summary>
    /// Gets or sets an optional source URL. If supplied, it must resolve to the same model ID.
    /// </summary>
    public string? SourceUrl { get; set; }
}
