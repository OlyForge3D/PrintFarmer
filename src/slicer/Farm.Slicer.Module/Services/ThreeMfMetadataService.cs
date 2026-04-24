using System.IO.Compression;
using System.Xml.Linq;
using Farm.Slicer.Module.Dtos;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Extracts metadata and generates auto-tags from 3MF file archives.
/// </summary>
public class ThreeMfMetadataService(ILogger<ThreeMfMetadataService> logger) : IThreeMfMetadataService
{
    private static readonly XNamespace ModelNamespace = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private readonly ILogger<ThreeMfMetadataService> _logger = logger;

    public async Task<ThreeMfMetadataDto?> ExtractMetadataAsync(string filePath, CancellationToken ct)
    {
        try
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await ExtractMetadataAsync(fs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract 3MF metadata from file: {FilePath}", filePath);
            return null;
        }
    }

    public async Task<ThreeMfMetadataDto?> ExtractMetadataAsync(Stream stream, CancellationToken ct)
    {
        const long MaxUncompressedEntrySize = 50 * 1024 * 1024; // 50 MB
        const int MaxArchiveEntries = 1000;

        try
        {
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > MaxArchiveEntries)
            {
                _logger.LogWarning("3MF archive has {Count} entries, exceeding limit of {Max}", archive.Entries.Count, MaxArchiveEntries);
                return null;
            }

            ZipArchiveEntry? modelEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase));

            if (modelEntry is null)
            {
                _logger.LogDebug("No 3D/3dmodel.model entry found in 3MF archive");
                return null;
            }

            if (modelEntry.Length > MaxUncompressedEntrySize)
            {
                _logger.LogWarning("3MF model entry is {Size} bytes, exceeding limit of {Max}", modelEntry.Length, MaxUncompressedEntrySize);
                return null;
            }

            var xmlSettings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxUncompressedEntrySize,
                Async = true
            };

            XDocument doc;
            await using (Stream entryStream = await modelEntry.OpenAsync(ct))
            using (var xmlReader = System.Xml.XmlReader.Create(entryStream, xmlSettings))
            {
                doc = await XDocument.LoadAsync(xmlReader, LoadOptions.None, ct);
            }

            XElement? root = doc.Root;
            if (root is null)
            {
                return null;
            }

            var metadataElements = root.Elements(ModelNamespace + "metadata");
            string? title = null, designer = null, description = null, application = null, creationDate = null, modificationDate = null;

            foreach (XElement meta in metadataElements)
            {
                string? name = meta.Attribute("name")?.Value;
                string? value = meta.Value?.Trim();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                switch (name.ToLowerInvariant())
                {
                    case "title":
                        title = value;
                        break;
                    case "designer":
                    case "creator":
                        designer = value;
                        break;
                    case "description":
                        description = value;
                        break;
                    case "application":
                        application = value;
                        break;
                    case "creationdate":
                        creationDate = value;
                        break;
                    case "modificationdate":
                        modificationDate = value;
                        break;
                }
            }

            List<string> materials = [];
            XElement? resources = root.Element(ModelNamespace + "resources");
            if (resources is not null)
            {
                foreach (XElement baseMaterials in resources.Elements(ModelNamespace + "basematerials"))
                {
                    foreach (XElement baseMat in baseMaterials.Elements(ModelNamespace + "base"))
                    {
                        string? materialName = baseMat.Attribute("name")?.Value?.Trim();
                        if (!string.IsNullOrEmpty(materialName) && !materials.Contains(materialName, StringComparer.OrdinalIgnoreCase))
                        {
                            materials.Add(materialName);
                        }
                    }
                }
            }

            List<string> autoTags = [];
            if (!string.IsNullOrWhiteSpace(designer))
            {
                autoTags.Add($"designer:{designer}");
            }

            foreach (string material in materials)
            {
                autoTags.Add($"material:{material}");
            }

            if (!string.IsNullOrWhiteSpace(application))
            {
                autoTags.Add($"app:{application}");
            }

            _logger.LogDebug(
                "Extracted 3MF metadata: Title={Title}, Designer={Designer}, Materials={MaterialCount}, AutoTags={TagCount}",
                title, designer, materials.Count, autoTags.Count);

            return new ThreeMfMetadataDto
            {
                Title = title,
                Designer = designer,
                Description = description,
                Application = application,
                CreationDate = creationDate,
                ModificationDate = modificationDate,
                Materials = materials,
                AutoTags = autoTags,
            };
        }
        catch (InvalidDataException ex)
        {
            _logger.LogDebug(ex, "Stream is not a valid ZIP/3MF archive");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract 3MF metadata from stream");
            return null;
        }
    }
}
