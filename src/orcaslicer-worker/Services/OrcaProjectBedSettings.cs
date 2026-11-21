using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>Reads bed selections from an Orca 3MF without extracting its contents.</summary>
internal sealed record OrcaProjectBedSettings(string? Global, IReadOnlyDictionary<int, string> Plates)
{
    private const long MaximumMetadataBytes = 4 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20000;

    internal string Resolve(int plateId, string? explicitBedType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plateId, 1);

        return explicitBedType is not null
            ? ValidateBedType(explicitBedType)
            : Plates.GetValueOrDefault(plateId) ?? Global ?? "High Temp Plate";
    }

    internal string ApplyExplicitSelection(string sourcePath, string destinationPath, string explicitBedType)
    {
        string bedType = ValidateBedType(explicitBedType);
        if (Plates.Count == 0)
        {
            return sourcePath;
        }

        File.Copy(sourcePath, destinationPath);
        using ZipArchive archive = ZipFile.Open(destinationPath, ZipArchiveMode.Update);
        ZipArchiveEntry entry = FindEntry(archive, "Metadata/model_settings.config")
            ?? throw new InvalidDataException("The 3MF plate settings are missing.");
        XElement root;
        using (Stream stream = entry.Open())
        using (XmlReader reader = CreateXmlReader(stream))
        {
            root = XElement.Load(reader);
        }

        foreach (XElement plate in root.Elements("plate"))
        {
            foreach (XElement metadata in plate.Elements("metadata")
                .Where(e => (string?)e.Attribute("key") == "bed_type"))
            {
                metadata.SetAttributeValue("value", bedType);
            }
        }

        entry.Delete();
        using Stream output = archive.CreateEntry("Metadata/model_settings.config").Open();
        root.Save(output);
        return destinationPath;
    }

    internal static async Task<(OrcaProjectBedSettings Settings, string ProcessPath, IReadOnlyList<string> ModelPaths)> PrepareAsync(
        IReadOnlyList<string> modelPaths,
        string processPath,
        string workDir,
        string? explicitBedType,
        CancellationToken cancellationToken)
    {
        List<string> projects = modelPaths
            .Where(path => string.Equals(Path.GetExtension(path), ".3mf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projects.Count > 0 && modelPaths.Count != 1)
        {
            throw new InvalidDataException("Bed selection cannot be resolved for multiple inputs containing a 3MF project.");
        }

        OrcaProjectBedSettings settings = projects.Count == 1
            ? Read(projects[0])
            : new OrcaProjectBedSettings(null, new Dictionary<int, string>());
        string bedType = explicitBedType is not null
            ? ValidateBedType(explicitBedType)
            : settings.Global ?? "High Temp Plate";

        string source = await File.ReadAllTextAsync(processPath, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Count(p => p.NameEquals("curr_bed_type")) > 1)
        {
            throw new InvalidDataException("The process profile has ambiguous bed settings.");
        }

        JsonObject process = JsonNode.Parse(source) as JsonObject
            ?? throw new InvalidDataException("The process profile is not an object.");
        process["curr_bed_type"] = bedType;
        string runtimeProcess = Path.Join(workDir, "project-bed-process.json");
        await File.WriteAllTextAsync(runtimeProcess, process.ToJsonString(), cancellationToken);

        IReadOnlyList<string> effectivePaths = modelPaths;
        if (projects.Count == 1 && explicitBedType is not null)
        {
            string runtimeProject = Path.Join(workDir, "project-bed-input.3mf");
            effectivePaths = [settings.ApplyExplicitSelection(projects[0], runtimeProject, explicitBedType)];
        }

        return (settings, runtimeProcess, effectivePaths);
    }

    internal static OrcaProjectBedSettings Read(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The 3MF contains too many entries.");
        }

        ValidatePackage(archive);
        ZipArchiveEntry? project = FindEntry(archive, "Metadata/project_settings.config");
        ZipArchiveEntry? model = FindEntry(archive, "Metadata/model_settings.config");
        string? global = null;
        if (project is not null)
        {
            using Stream stream = project.Open();
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The 3MF project settings are not an object.");
            }

            bool found = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("curr_bed_type"))
                {
                    if (found || property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("The 3MF project bed type is ambiguous.");
                    }

                    found = true;
                    global = ValidateBedType(property.Value.GetString());
                }
            }
        }

        Dictionary<int, string> plates = [];
        HashSet<int> seenPlates = [];
        if (model is not null)
        {
            using Stream stream = model.Open();
            using XmlReader reader = CreateXmlReader(stream);
            XElement root = XElement.Load(reader);
            if (root.Name != "config")
            {
                throw new InvalidDataException("The 3MF model settings are invalid.");
            }

            foreach (XElement plate in root.Elements("plate"))
            {
                List<XElement> ids = plate.Elements("metadata")
                    .Where(e => (string?)e.Attribute("key") == "plater_id").ToList();
                List<XElement> beds = plate.Elements("metadata")
                    .Where(e => (string?)e.Attribute("key") == "bed_type").ToList();
                if (ids.Count != 1 || !int.TryParse((string?)ids[0].Attribute("value"), out int id)
                    || id < 1 || !seenPlates.Add(id) || beds.Count > 1)
                {
                    throw new InvalidDataException("The 3MF plate settings are ambiguous.");
                }

                if (beds.Count == 1)
                {
                    plates.Add(id, ValidateBedType((string?)beds[0].Attribute("value")));
                }
            }
        }

        return new OrcaProjectBedSettings(global, plates);
    }

    private static void ValidatePackage(ZipArchive archive)
    {
        ZipArchiveEntry contentTypes = FindEntry(archive, "[Content_Types].xml")
            ?? throw new InvalidDataException("The 3MF content types are missing.");
        ZipArchiveEntry relationships = FindEntry(archive, "_rels/.rels")
            ?? throw new InvalidDataException("The 3MF package relationships are missing.");
        using Stream typesStream = contentTypes.Open();
        using XmlReader typesReader = CreateXmlReader(typesStream);
        XElement types = XElement.Load(typesReader);
        XNamespace contentNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string modelContentType = "application/vnd.ms-package.3dmanufacturing-3dmodel+xml";
        if (types.Name != contentNamespace + "Types")
        {
            throw new InvalidDataException("The 3MF content types are invalid.");
        }

        using Stream relsStream = relationships.Open();
        using XmlReader relsReader = CreateXmlReader(relsStream);
        XElement rels = XElement.Load(relsReader);
        XNamespace relNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        List<XElement> models = rels.Elements(relNamespace + "Relationship")
            .Where(e => (string?)e.Attribute("Type") == "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")
            .ToList();
        if (rels.Name != relNamespace + "Relationships" || models.Count != 1
            || models[0].Attribute("TargetMode") is not null)
        {
            throw new InvalidDataException("The 3MF model relationship is invalid.");
        }

        string? target = (string?)models[0].Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || target.Contains('\\') || target.Contains('%')
            || target.Contains('?') || target.Contains('#') || target.Contains(':'))
        {
            throw new InvalidDataException("The 3MF model target is invalid.");
        }

        string modelPath = target.TrimStart('/');
        if (modelPath.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The 3MF model target is invalid.");
        }

        string extension = Path.GetExtension(modelPath).TrimStart('.');
        bool hasModelType = types.Elements(contentNamespace + "Default").Any(e =>
                string.Equals((string?)e.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)
                && (string?)e.Attribute("ContentType") == modelContentType)
            || types.Elements(contentNamespace + "Override").Any(e =>
                string.Equals(((string?)e.Attribute("PartName"))?.TrimStart('/'), modelPath, StringComparison.OrdinalIgnoreCase)
                && (string?)e.Attribute("ContentType") == modelContentType);
        ZipArchiveEntry model = FindEntry(archive, modelPath, limitSize: false)
            ?? throw new InvalidDataException("The 3MF model is missing.");
        if (!hasModelType)
        {
            throw new InvalidDataException("The 3MF model content type is invalid.");
        }

        using Stream modelStream = model.Open();
        using XmlReader modelReader = CreateXmlReader(modelStream, limitSize: false);
        if (modelReader.MoveToContent() != XmlNodeType.Element
            || modelReader.LocalName != "model"
            || modelReader.NamespaceURI != "http://schemas.microsoft.com/3dmanufacturing/core/2015/02")
        {
            throw new InvalidDataException("The 3MF model is invalid.");
        }
    }

    private static XmlReader CreateXmlReader(Stream stream, bool limitSize = true) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = limitSize ? MaximumMetadataBytes : 0,
    });

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name, bool limitSize = true)
    {
        List<ZipArchiveEntry> matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1 || (limitSize && matches.Any(entry => entry.Length > MaximumMetadataBytes)))
        {
            throw new InvalidDataException($"The 3MF entry {name} is ambiguous or too large.");
        }

        return matches.SingleOrDefault();
    }

    private static string ValidateBedType(string? value) => value switch
    {
        "Cool Plate" or "Engineering Plate" or "High Temp Plate" or "Textured PEI Plate"
            or "Textured Cool Plate" or "Supertack Plate" => value,
        _ => throw new InvalidDataException("The 3MF contains an unsupported bed type."),
    };
}
