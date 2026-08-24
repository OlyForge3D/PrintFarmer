using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services.Calibration;

/// <summary>
/// Applies per-object flow-ratio overrides to a flow-rate calibration 3MF project (issue #1938).
/// <para>
/// The official flow-rate calibration towers (<c>flowrate-test-pass{1,2}.3mf</c>) encode the
/// target flow ratio for each printable object in the object's name (for example
/// <c>flowrate_95</c> for a 95% flow ratio). In OrcaSlicer's GUI, <c>Plater.cpp</c> parses that
/// name and applies a per-object <c>flow_ratio</c> override when the project is opened
/// interactively — logic the worker's CLI-driven pipeline never runs. This class reimplements
/// that parsing and writes the resulting overrides directly into the 3MF's
/// <c>Metadata/Slic3r_PE_model.config</c>, so the CLI slice applies them exactly as the GUI would.
/// </para>
/// </summary>
public static partial class FlowRateCalibrationConfigurator
{
    [GeneratedRegex(@"flow[_\s-]*rate[_\s-]*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FlowRateObjectNamePattern();

    /// <summary>
    /// Parses an object's name for an embedded flow-rate percentage (for example
    /// <c>"flowrate_95"</c> or <c>"flowrate-102.5"</c>) and returns it as a ratio (0.95, 1.025).
    /// Returns <see langword="null"/> when the name carries no recognizable flow-rate value.
    /// </summary>
    public static double? TryParseFlowRatio(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return null;
        }

        Match match = FlowRateObjectNamePattern().Match(objectName);
        if (!match.Success)
        {
            return null;
        }

        double percent = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return percent / 100.0;
    }

    /// <summary>
    /// Parses the 3MF core <c>3D/3dmodel.model</c> XML document for its top-level object ids and
    /// names.
    /// </summary>
    public static IReadOnlyList<(int Id, string? Name)> ParseObjectNames(string modelXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelXml);
        XDocument doc = XDocument.Parse(modelXml);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var result = new List<(int Id, string? Name)>();
        foreach (XElement objectElement in doc.Descendants(ns + "object"))
        {
            string? idAttr = objectElement.Attribute("id")?.Value;
            if (idAttr is null || !int.TryParse(idAttr, out int id))
            {
                continue;
            }

            string? name = objectElement.Attribute("name")?.Value
                ?? objectElement.Attribute(XNamespace.Get("http://schemas.microsoft.com/3dmanufacturing/production/2015/06") + "name")?.Value;
            result.Add((id, name));
        }

        return result;
    }

    /// <summary>
    /// Resolves the flow-ratio override for each object whose name carries a recognizable
    /// flow-rate value. Objects with unparseable names are skipped (defensive: an unexpected
    /// naming scheme in a future resource file never crashes the slice, it just gets no override).
    /// </summary>
    public static IReadOnlyDictionary<int, double> ParseObjectFlowRatios(IEnumerable<(int Id, string? Name)> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var result = new Dictionary<int, double>();
        foreach ((int id, string? name) in objects)
        {
            double? ratio = TryParseFlowRatio(name);
            if (ratio.HasValue)
            {
                result[id] = ratio.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds (or merges into) the <c>Metadata/Slic3r_PE_model.config</c> XML document, adding a
    /// per-object <c>flow_ratio</c> metadata override for each entry in
    /// <paramref name="flowRatiosByObjectId"/>. Existing per-object metadata for objects not in
    /// <paramref name="flowRatiosByObjectId"/>, and existing metadata keys other than
    /// <c>flow_ratio</c>, are preserved untouched.
    /// </summary>
    public static string BuildObjectConfigXml(
        IReadOnlyDictionary<int, double> flowRatiosByObjectId,
        string? existingConfigXml)
    {
        ArgumentNullException.ThrowIfNull(flowRatiosByObjectId);

        XDocument doc = string.IsNullOrWhiteSpace(existingConfigXml)
            ? new XDocument(new XElement("config"))
            : XDocument.Parse(existingConfigXml);

        XElement root = doc.Root ?? new XElement("config");
        if (doc.Root is null)
        {
            doc.Add(root);
        }

        foreach ((int objectId, double flowRatio) in flowRatiosByObjectId)
        {
            XElement? objectElement = root.Elements("object")
                .FirstOrDefault(e => string.Equals(e.Attribute("id")?.Value, objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (objectElement is null)
            {
                objectElement = new XElement("object", new XAttribute("id", objectId));
                root.Add(objectElement);
            }

            XElement? flowRatioMetadata = objectElement.Elements("metadata")
                .FirstOrDefault(e => e.Attribute("type")?.Value == "object" && e.Attribute("key")?.Value == "flow_ratio");
            if (flowRatioMetadata is null)
            {
                objectElement.Add(new XElement(
                    "metadata",
                    new XAttribute("type", "object"),
                    new XAttribute("key", "flow_ratio"),
                    new XAttribute("value", flowRatio.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
            else
            {
                flowRatioMetadata.SetAttributeValue("value", flowRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var sb = new StringBuilder();
        using (var writer = new System.IO.StringWriter(sb))
        {
            doc.Save(writer);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Copies <paramref name="source3mfPath"/> into <paramref name="destinationDirectory"/> and,
    /// when its object names carry recognizable flow-rate values, injects the corresponding
    /// per-object <c>flow_ratio</c> overrides into the copy's
    /// <c>Metadata/Slic3r_PE_model.config</c>. Parsing failures are logged and treated as
    /// "no overrides available" rather than failing the slice — the calibration model still
    /// slices with whatever flow ratio the active filament profile specifies, it just loses the
    /// per-band differentiation.
    /// </summary>
    public static string ApplyPerObjectFlowRatios(string source3mfPath, string destinationDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source3mfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(source3mfPath));
        File.Copy(source3mfPath, destinationPath, overwrite: true);

        try
        {
            using ZipArchive archive = ZipFile.Open(destinationPath, ZipArchiveMode.Update);
            ZipArchiveEntry? modelEntry = archive.GetEntry("3D/3dmodel.model");
            if (modelEntry is null)
            {
                logger.LogWarning(
                    "Calibration project '{Path}' has no 3D/3dmodel.model entry; slicing without per-object flow-ratio overrides.",
                    source3mfPath);
                return destinationPath;
            }

            string modelXml = ReadEntryText(modelEntry);
            IReadOnlyList<(int Id, string? Name)> objects = ParseObjectNames(modelXml);
            IReadOnlyDictionary<int, double> flowRatios = ParseObjectFlowRatios(objects);
            if (flowRatios.Count == 0)
            {
                logger.LogWarning(
                    "No object in calibration project '{Path}' has a parseable flow-rate name; slicing without per-object flow-ratio overrides.",
                    source3mfPath);
                return destinationPath;
            }

            ZipArchiveEntry? configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config");
            string? existingConfigXml = configEntry is null ? null : ReadEntryText(configEntry);
            string newConfigXml = BuildObjectConfigXml(flowRatios, existingConfigXml);

            configEntry?.Delete();
            ZipArchiveEntry newConfigEntry = archive.CreateEntry("Metadata/Slic3r_PE_model.config");
            WriteEntryText(newConfigEntry, newConfigXml);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
        {
            logger.LogWarning(
                ex,
                "Failed to apply per-object flow-ratio overrides to calibration project '{Path}'; slicing without them.",
                source3mfPath);
        }

        return destinationPath;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteEntryText(ZipArchiveEntry entry, string content)
    {
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
