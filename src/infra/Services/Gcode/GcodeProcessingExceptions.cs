namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Base exception for gcode file processing failures
/// </summary>
public class GcodeProcessingException : Exception
{
    public string? FileName { get; }
    public string? Step { get; }
    public Dictionary<string, object> ContextData { get; }

    public GcodeProcessingException(string message, string? fileName = null, string? step = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FileName = fileName;
        Step = step;
        ContextData = new();
    }

    public GcodeProcessingException AddContext(string key, object value)
    {
        ContextData[key] = value;
        return this;
    }

    public override string ToString()
    {
        var parts = new List<string> { base.ToString() };
        if (!string.IsNullOrEmpty(FileName))
            parts.Add($"FileName: {FileName}");
        if (!string.IsNullOrEmpty(Step))
            parts.Add($"Step: {Step}");
        if (ContextData.Any())
            parts.Add($"Context: {string.Join(", ", ContextData.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        return string.Join(" | ", parts);
    }
}

/// <summary>
/// Thrown when file download from printer fails
/// </summary>
public class FileDownloadException : GcodeProcessingException
{
    public FileDownloadException(string fileName, string message, Exception? innerException = null)
        : base(message, fileName, "FileDownload", innerException) { }
}

/// <summary>
/// Thrown when metadata extraction fails
/// </summary>
public class MetadataExtractionException : GcodeProcessingException
{
    public MetadataExtractionException(string fileName, string message, Exception? innerException = null)
        : base(message, fileName, "MetadataExtraction", innerException) { }
}

/// <summary>
/// Thrown when thumbnail processing fails
/// </summary>
public class ThumbnailProcessingException : GcodeProcessingException
{
    public ThumbnailProcessingException(string fileName, string message, Exception? innerException = null)
        : base(message, fileName, "ThumbnailProcessing", innerException) { }
}

/// <summary>
/// Thrown when file storage fails
/// </summary>
public class FileStorageException : GcodeProcessingException
{
    public FileStorageException(string fileName, string message, Exception? innerException = null)
        : base(message, fileName, "FileStorage", innerException) { }
}

/// <summary>
/// Thrown when duplicate file is detected
/// </summary>
public class DuplicateFileException : GcodeProcessingException
{
    public string? ExistingFileId { get; }

    public DuplicateFileException(string fileName, string existingFileId, string fileHash)
        : base($"Duplicate file detected: {fileHash}", fileName, "DuplicateCheck")
    {
        ExistingFileId = existingFileId;
        AddContext("ExistingFileId", existingFileId);
        AddContext("FileHash", fileHash);
    }
}

/// <summary>
/// Thrown when database persistence fails
/// </summary>
public class FilePersistenceException : GcodeProcessingException
{
    public FilePersistenceException(string fileName, string message, Exception? innerException = null)
        : base(message, fileName, "DatabasePersistence", innerException) { }
}
