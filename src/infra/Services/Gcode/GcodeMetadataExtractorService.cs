using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Gcode;

public class GcodeMetadataExtractorService(ILogger<GcodeMetadataExtractorService> logger) : IGcodeMetadataExtractorService
{
    private readonly ILogger<GcodeMetadataExtractorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public GcodeMetadataExtractionResult ExtractBoundedMetadata(
        string gcodeContent, bool captureThumbnail, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(gcodeContent) > PrinterFileEnrichmentLimits.MaximumDecodedBytes)
        {
            return new(new(), PrinterFileEnrichmentState.Partial, PrinterFileEnrichmentState.Deferred,
                "decoded-limit", "decoded-limit");
        }

        string[] lines = gcodeContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        List<string> comments = new();
        int metadataBytes = 0;
        bool metadataLimited = false;
        bool inThumbnail = false;
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith(';'))
            {
                continue;
            }

            string comment = line[1..].TrimStart();
            if (comment.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                inThumbnail = comment.Contains("begin", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inThumbnail)
            {
                continue;
            }

            int bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > PrinterFileEnrichmentLimits.MetadataBytes - metadataBytes)
            {
                metadataLimited = true;
                break;
            }

            metadataBytes += bytes;
            comments.Add(line);
        }

        GcodeMetadataExtracted metadata = new();
        ExtractAllProperties(comments, metadata, completeContent: false, ct);
        var image = captureThumbnail
            ? ExtractThumbnail(lines, metadata, bounded: true, ct)
            : (PrinterFileEnrichmentState.Deferred, "capture-disabled");
        return new(metadata, PrinterFileEnrichmentState.Partial, image.Item1,
            metadataLimited ? "metadata-limit" : "bounded-source", image.Item2);
    }

    /// <summary>
    /// Extract metadata from G-code file content by parsing comment lines.
    /// Handles metadata from PrusaSlicer, Cura, OrcaSlicer, and generic patterns.
    /// </summary>
    public async Task<GcodeMetadataExtracted> ExtractMetadataAsync(string gcodeContent)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("ExtractMetadataAsync: Starting metadata extraction");
            GcodeMetadataExtracted metadata = new();

            if (string.IsNullOrWhiteSpace(gcodeContent))
            {
                _logger.LogWarning("ExtractMetadataAsync: G-code content is empty or null");
                return metadata;
            }

            try
            {
                string[] allLines = gcodeContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                _logger.LogInformation("ExtractMetadataAsync: Scanning all {LineCount} lines", allLines.Length.ToString());

                // Single-pass extraction of all key-value metadata properties
                ExtractAllProperties(allLines, metadata);
                _logger.LogInformation("ExtractMetadataAsync: About to extract thumbnail");
                ExtractThumbnail(allLines, metadata);
                _logger.LogInformation("ExtractMetadataAsync: Thumbnail extraction complete, ThumbnailData={HasData}", metadata.ThumbnailData != null ? $"{metadata.ThumbnailData.Length} bytes" : "null");

                _logger.LogInformation("ExtractMetadataAsync: Extracted metadata - Slicer={MetadataSlicerName} {MetadataSlicerVersion}, Material={MetadataMaterial}, NozzleDiameter={MetadataNozzleDiameter}, PrintTime={MetadataEstimatedPrintTimeMinutes}min, Filament={MetadataFilamentWeightGrams}g, LayerHeight={MetadataLayerHeight}, BedTemp={MetadataBedTemperature}°C, PrintTemp={MetadataPrintTemperature}°C", metadata.SlicerName ?? "(unknown)", metadata.SlicerVersion ?? string.Empty, metadata.Material ?? "(unknown)", metadata.NozzleDiameter?.ToString("F1") ?? "0", metadata.EstimatedPrintTimeMinutes?.ToString("F0") ?? "0", metadata.FilamentWeightGrams?.ToString("F1") ?? "0", metadata.LayerHeight?.ToString("F2") ?? "0", metadata.BedTemperature?.ToString("F0") ?? "0", metadata.PrintTemperature?.ToString("F0") ?? "0");

                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extracting G-code metadata");
                return metadata;
            }
        });
    }

    // Pre-compiled regex patterns for efficient single-pass extraction
    private static readonly Regex SlicerGeneratedByPattern = new(@"generated by\s+(.+?)\s+(\d+(?:\.\d+)*(?:[-+][0-9A-Za-z.-]+)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CuraVersionPattern = new(@"Cura_Version:(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaterialPattern = new(@";\s*(?:filament_type|MATERIAL|material)\s*[:=]\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NozzleDiameterPattern = new(@";\s*nozzle_diameter\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LayerHeightPattern = new(@";\s*layer_height\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrintTimeUnitPattern = new(@"(?<![\w.])(\d+(?:\.\d+)?)\s*([dhms])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrintTimeSecondsPattern = new(@"(?:TIME|Time):\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentLengthPattern = new(@";\s*filament_length\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentLengthConfigPattern = new(@"^;\s*filament used \[mm\]\s*[:=]\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentWeightPattern = new(@";\s*(?:filament_g|filament_weight)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentWeightConfigPattern = new(@"^;\s*filament used \[g\]\s*[:=]\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentTotalWeightPattern = new(@"^;\s*total filament used \[g\]\s*[:=]\s*(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentColorConfigPattern = new(@";\s*filament_colou?r\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentTypeConfigPattern = new(@";\s*filament_type\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexColorPattern = new(@"^[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);
    private static readonly Regex BedTempPattern = new(@";\s*first_layer_bed_temperature\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NozzleTempPattern = new(@";\s*first_layer_temperature\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrinterModelPattern = new(@"; ?(?:printer_model|machine_name)\s*[:=]\s*(?!~\/)([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrintSettingsPattern = new(@";\s*print_settings_id\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SparseInfillPattern = new(@";\s*sparse_infill_density\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FillDensityPattern = new(@";\s*fill_density\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InfillPatternPattern = new(@"^;\s*(?:sparse_infill_pattern|fill_pattern)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WallLoopsPattern = new(@";\s*wall_loops\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerimetersPattern = new(@";\s*perimeters\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingEscapePattern = new(@"(\\[nrt])+$", RegexOptions.Compiled);

    // --- New patterns for expanded metadata ---
    private static readonly Regex TotalLayersPattern = new(@";\s*(?:total layers count|LAYER_COUNT|total_layer_count)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FirstLayerHeightPattern = new(@";\s*first_layer_height\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportEnabledPattern = new(@"^;\s*(?:support_material|enable_support)\s*[:=]\s*(0|1|true|false)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportTypePattern = new(@";\s*support_type\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportStylePattern = new(@"^;\s*(?:support_style|support_material_style)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectDimensionXPattern = new(@";\s*(?:max_print_width|model_size_x)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectDimensionYPattern = new(@";\s*(?:max_print_depth|model_size_y)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectDimensionZPattern = new(@";\s*(?:max_print_height|model_size_z)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetractionLengthPattern = new(@";\s*(?:retraction_length|retract_length)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetractionSpeedPattern = new(@";\s*(?:retraction_speed|retract_speed)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TopSolidLayersPattern = new(@";\s*(?:top_solid_layers|top_shell_layers)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BottomSolidLayersPattern = new(@";\s*(?:bottom_solid_layers|bottom_shell_layers)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxVolumetricSpeedPattern = new(@";\s*max_volumetric_speed\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IroningEnabledPattern = new(@";\s*(?:ironing|ironing_type)\s*[:=]\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Single-pass extraction of all key-value metadata from G-code comment lines.
    /// Replaces 11 separate Extract* methods that each iterated the full line set.
    /// </summary>
    private void ExtractAllProperties(
        IReadOnlyList<string> lines, GcodeMetadataExtracted metadata,
        bool completeContent = true, CancellationToken ct = default)
    {
        // Print time uses priority: normal mode (3) > TIME: seconds (2) > generic estimated (1)
        int printTimePriority = 0;

        // Tracking for counted fields
        HashSet<string> uniqueObjects = new(StringComparer.OrdinalIgnoreCase);
        int toolChangeCount = 0;
        int? lastTool = null;
        double?[]? weightSlots = null;
        double?[]? lengthSlots = null;
        double? explicitTotalWeight = null;

        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Track tool changes from non-comment G-code commands (e.g., "T0", "T1")
            if (!line.StartsWith(';'))
            {
                if (completeContent && line.Length >= 2 && line[0] == 'T' && char.IsDigit(line[1])
                    && (line.Length == 2 || line[2] == ' ' || line[2] == ';'))
                {
                    int tool = line[1] - '0';
                    if (lastTool != null && tool != lastTool)
                    {
                        toolChangeCount++;
                    }

                    lastTool = tool;
                }

                continue;
            }

            // Track unique object/mesh names from slicer comments
            if (completeContent && (line.StartsWith("; printing object", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(";MESH:", StringComparison.OrdinalIgnoreCase)))
            {
                int separatorIdx = line.IndexOf(':');
                if (separatorIdx < 0)
                {
                    separatorIdx = line.IndexOf(' ', 2);
                }

                if (separatorIdx >= 0 && separatorIdx + 1 < line.Length)
                {
                    string objectName = line[(separatorIdx + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(objectName)
                        && !string.Equals(objectName, "NONMESH", StringComparison.OrdinalIgnoreCase))
                    {
                        uniqueObjects.Add(objectName);
                    }
                }
            }

            bool isGcodeTemplateLine = line.Contains("_gcode =", StringComparison.OrdinalIgnoreCase) ||
                                      line.Contains("_gcode=", StringComparison.OrdinalIgnoreCase);

            // --- Slicer Info ---
            if (metadata.SlicerName == null)
            {
                if (line.Contains("generated by", StringComparison.OrdinalIgnoreCase))
                {
                    Match match = SlicerGeneratedByPattern.Match(line);
                    if (match.Success)
                    {
                        metadata.SlicerName = match.Groups[1].Value.Trim();
                        metadata.SlicerVersion = match.Groups[2].Value;
                    }
                }

                if (metadata.SlicerName == null && line.Contains("Cura_Version", StringComparison.OrdinalIgnoreCase))
                {
                    Match match = CuraVersionPattern.Match(line);
                    if (match.Success)
                    {
                        metadata.SlicerName = "Cura";
                        metadata.SlicerVersion = match.Groups[1].Value.Trim();
                    }
                }
            }

            // --- Material ---
            if (metadata.Material == null)
            {
                Match match = MaterialPattern.Match(line);
                if (match.Success)
                {
                    metadata.Material = match.Groups[1].Value.Trim().Trim(';', ' ', '"');
                }
            }

            // --- Per-extruder filament material/type ---
            if (metadata.FilamentPerExtruderType == null)
            {
                Match match = FilamentTypeConfigPattern.Match(line);
                if (match.Success)
                {
                    string[]? perExtruder = ParseSemicolonDelimitedStrings(match.Groups[1].Value);
                    if (perExtruder is { Length: > 0 })
                    {
                        metadata.FilamentPerExtruderType = perExtruder;
                    }
                }
            }

            // --- Nozzle Diameter ---
            if (metadata.NozzleDiameter == null)
            {
                Match match = NozzleDiameterPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double diameter))
                {
                    metadata.NozzleDiameter = diameter;
                }
            }

            // --- Layer Height ---
            if (metadata.LayerHeight == null)
            {
                Match match = LayerHeightPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double height))
                {
                    metadata.LayerHeight = height;
                }
            }

            // --- Print Time (priority-based: normal mode > TIME: > generic) ---
            if (printTimePriority < 3 && line.Contains("estimated printing time (normal mode)", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParsePrintTime(line, out double minutes))
                {
                    metadata.EstimatedPrintTimeMinutes = minutes;
                    printTimePriority = 3;
                }
            }
            else if (printTimePriority < 2 &&
                     (line.Contains("TIME:", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("; Time:", StringComparison.OrdinalIgnoreCase)))
            {
                Match match = PrintTimeSecondsPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int seconds))
                {
                    metadata.EstimatedPrintTimeMinutes = seconds / 60.0;
                    printTimePriority = 2;
                }
            }
            else if (printTimePriority < 1 &&
                     line.Contains("estimated printing time", StringComparison.OrdinalIgnoreCase) &&
                     !line.Contains("remaining", StringComparison.OrdinalIgnoreCase) &&
                     !line.Contains("first layer", StringComparison.OrdinalIgnoreCase) &&
                     TryParsePrintTime(line, out double minutes))
            {
                metadata.EstimatedPrintTimeMinutes = minutes;
                printTimePriority = 1;
            }

            // --- Filament Length ---
            if (lengthSlots is null)
            {
                Match match = FilamentLengthPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double length))
                {
                    metadata.FilamentLengthMm = length;
                    lengthSlots = [length];
                }
                else
                {
                    match = FilamentLengthConfigPattern.Match(line);
                    if (match.Success)
                    {
                        lengthSlots = ParseCommaDelimitedNumbers(match.Groups[1].Value);
                    }
                }
            }

            // --- Filament Weight ---
            Match totalWeightMatch = FilamentTotalWeightPattern.Match(line);
            if (totalWeightMatch.Success && TryParseMeasurement(totalWeightMatch.Groups[1].Value, out double totalWeight))
            {
                explicitTotalWeight = totalWeight;
            }

            if (weightSlots is null &&
                !line.Contains("mm3", StringComparison.OrdinalIgnoreCase))
            {
                Match match = FilamentWeightPattern.Match(line);
                if (match.Success && !line.Contains("[", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(match.Groups[1].Value, out double weight))
                {
                    explicitTotalWeight ??= weight;
                    weightSlots = [weight];
                }
                else
                {
                    match = FilamentWeightConfigPattern.Match(line);
                    if (match.Success)
                    {
                        weightSlots = ParseCommaDelimitedNumbers(match.Groups[1].Value);
                    }
                }
            }

            Match infillPatternMatch = InfillPatternPattern.Match(line);
            if (metadata.InfillPattern is null && infillPatternMatch.Success)
            {
                metadata.InfillPattern = infillPatternMatch.Groups[1].Value.Trim();
            }

            // --- Per-extruder filament color ---
            if (metadata.FilamentPerExtruderColorHex == null)
            {
                Match match = FilamentColorConfigPattern.Match(line);
                if (match.Success)
                {
                    string[]? perExtruder = ParseSemicolonDelimitedStrings(match.Groups[1].Value, NormalizeHexColorToken);
                    if (perExtruder is { Length: > 0 })
                    {
                        metadata.FilamentPerExtruderColorHex = perExtruder;
                    }
                }
            }

            // --- Temperatures (always overwrite — takes last match from config block) ---
            Match bedMatch = BedTempPattern.Match(line);
            if (bedMatch.Success && double.TryParse(bedMatch.Groups[1].Value, out double bedTemp))
            {
                metadata.BedTemperature = bedTemp;
            }

            Match nozzleTempMatch = NozzleTempPattern.Match(line);
            if (nozzleTempMatch.Success && double.TryParse(nozzleTempMatch.Groups[1].Value, out double nozzleTemp))
            {
                metadata.PrintTemperature = nozzleTemp;
            }

            // --- Printer Model (tightened regex: ; followed by optional single space) ---
            if (metadata.PrinterModel == null && !isGcodeTemplateLine)
            {
                Match match = PrinterModelPattern.Match(line);
                if (match.Success)
                {
                    string model = match.Groups[1].Value.Trim().Trim(';', ' ', '"');
                    model = TrailingEscapePattern.Replace(model, string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(model) &&
                        !Regex.IsMatch(model, @"^\[.+\]$") &&
                        !Regex.IsMatch(model, @"^\{.+\}$"))
                    {
                        metadata.PrinterModel = model;
                    }
                }
            }

            // --- Print Settings ID ---
            if (metadata.PrintSettingsId == null)
            {
                Match match = PrintSettingsPattern.Match(line);
                if (match.Success)
                {
                    metadata.PrintSettingsId = match.Groups[1].Value.Trim().Trim(';', ' ', '"');
                }
            }

            // --- Infill (sparse_infill_density > fill_density, excluding skeleton/skin) ---
            if (metadata.InfillPercentage == null)
            {
                Match match = SparseInfillPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double infill))
                {
                    metadata.InfillPercentage = infill;
                }
                else if (!line.Contains("skeleton", StringComparison.OrdinalIgnoreCase) &&
                         !line.Contains("skin", StringComparison.OrdinalIgnoreCase))
                {
                    match = FillDensityPattern.Match(line);
                    if (match.Success && double.TryParse(match.Groups[1].Value, out infill))
                    {
                        metadata.InfillPercentage = infill;
                    }
                }
            }

            // --- Perimeters / Wall Loops ---
            if (metadata.Perimeters == null)
            {
                Match match = WallLoopsPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int wallLoops))
                {
                    metadata.Perimeters = wallLoops;
                }
                else
                {
                    match = PerimetersPattern.Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int perimeters))
                    {
                        metadata.Perimeters = perimeters;
                    }
                }
            }

            // --- Total Layers ---
            if (metadata.TotalLayers == null)
            {
                Match match = TotalLayersPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int totalLayers))
                {
                    metadata.TotalLayers = totalLayers;
                }
            }

            // --- First Layer Height ---
            if (metadata.FirstLayerHeight == null)
            {
                Match match = FirstLayerHeightPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double firstLayerHeight))
                {
                    metadata.FirstLayerHeight = firstLayerHeight;
                }
            }

            // --- Support Enabled ---
            if (metadata.SupportEnabled == null)
            {
                Match match = SupportEnabledPattern.Match(line);
                if (match.Success)
                {
                    string val = match.Groups[1].Value.Trim();
                    metadata.SupportEnabled = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            Match supportTypeMatch = SupportTypePattern.Match(line);
            if (metadata.SupportType is null && supportTypeMatch.Success)
            {
                metadata.SupportType = supportTypeMatch.Groups[1].Value.Trim();
            }

            Match supportStyleMatch = SupportStylePattern.Match(line);
            if (metadata.SupportStyle is null && supportStyleMatch.Success)
            {
                metadata.SupportStyle = supportStyleMatch.Groups[1].Value.Trim();
            }

            // --- Object Dimensions (bounding box) ---
            if (metadata.ObjectDimensionX == null)
            {
                Match match = ObjectDimensionXPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double dimX))
                {
                    metadata.ObjectDimensionX = dimX;
                }
            }

            if (metadata.ObjectDimensionY == null)
            {
                Match match = ObjectDimensionYPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double dimY))
                {
                    metadata.ObjectDimensionY = dimY;
                }
            }

            if (metadata.ObjectDimensionZ == null)
            {
                Match match = ObjectDimensionZPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double dimZ))
                {
                    metadata.ObjectDimensionZ = dimZ;
                }
            }

            // --- Retraction Length ---
            if (metadata.RetractionLength == null && !isGcodeTemplateLine)
            {
                Match match = RetractionLengthPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double retractLen))
                {
                    metadata.RetractionLength = retractLen;
                }
            }

            // --- Retraction Speed ---
            if (metadata.RetractionSpeed == null && !isGcodeTemplateLine)
            {
                Match match = RetractionSpeedPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double retractSpeed))
                {
                    metadata.RetractionSpeed = retractSpeed;
                }
            }

            // --- Top/Bottom Solid Layers ---
            if (metadata.TopSolidLayers == null)
            {
                Match match = TopSolidLayersPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int topLayers))
                {
                    metadata.TopSolidLayers = topLayers;
                }
            }

            if (metadata.BottomSolidLayers == null)
            {
                Match match = BottomSolidLayersPattern.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int bottomLayers))
                {
                    metadata.BottomSolidLayers = bottomLayers;
                }
            }

            // --- Max Volumetric Speed ---
            if (metadata.MaxVolumetricSpeed == null)
            {
                Match match = MaxVolumetricSpeedPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double maxVolSpeed) && maxVolSpeed > 0)
                {
                    metadata.MaxVolumetricSpeed = maxVolSpeed;
                }
            }

            // --- Ironing Enabled ---
            if (metadata.IroningEnabled == null)
            {
                Match match = IroningEnabledPattern.Match(line);
                if (match.Success)
                {
                    string val = match.Groups[1].Value.Trim();
                    metadata.IroningEnabled = val == "1"
                                           || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)
                                           || (val != "0" && !string.Equals(val, "false", StringComparison.OrdinalIgnoreCase)
                                                          && !string.Equals(val, "no ironing", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        // Post-loop: assign counted fields
        if (toolChangeCount > 0)
        {
            metadata.ToolChangesCount = toolChangeCount;
        }

        if (uniqueObjects.Count > 0)
        {
            metadata.ObjectCount = uniqueObjects.Count;
        }

        // Post-loop: derive ExtruderCount from per-extruder arrays
        int weightCount = weightSlots?.Length ?? 0;
        int lengthCount = lengthSlots?.Length ?? 0;
        int colorCount = metadata.FilamentPerExtruderColorHex?.Length ?? 0;
        int typeCount = metadata.FilamentPerExtruderType?.Length ?? 0;
        int maxExtruders = Math.Max(Math.Max(weightCount, lengthCount), Math.Max(colorCount, typeCount));
        if (maxExtruders > 1)
        {
            metadata.ExtruderCount = maxExtruders;
        }

        metadata.FilamentPerExtruderWeightG = CompleteNumbers(weightSlots);
        metadata.FilamentPerExtruderLengthMm = CompleteNumbers(lengthSlots);
        metadata.FilamentLengthMm = lengthCount == maxExtruders
            ? metadata.FilamentPerExtruderLengthMm?.Sum() : null;
        metadata.FilamentWeightGrams = explicitTotalWeight ?? (weightCount == maxExtruders
            ? metadata.FilamentPerExtruderWeightG?.Sum() : null);
        metadata.FilamentWeightIsDerived = metadata.FilamentWeightGrams.HasValue
            ? !explicitTotalWeight.HasValue : null;
        if (maxExtruders > 0)
        {
            metadata.Tools = Enumerable.Range(0, maxExtruders).Select(index =>
            {
                string? color = metadata.FilamentPerExtruderColorHex?.ElementAtOrDefault(index);
                string? material = metadata.FilamentPerExtruderType?.ElementAtOrDefault(index);
                return new PrinterFilamentTool
                {
                    Index = index,
                    Color = PrinterFilamentTool.IsValidColor(color) ? color : null,
                    Material = PrinterPrintMetadata.IsValidText(material) ? material : null,
                    FilamentUsedG = weightSlots?.ElementAtOrDefault(index),
                };
            }).ToArray();
        }
    }

    /// <summary>
    /// Parses a comma-delimited string of doubles (e.g., "10.30,3.80" or "10.30, 3.80,").
    /// Keeps missing or invalid middle values in their source slots.
    /// Uses invariant culture to ensure '.' is always the decimal separator.
    /// </summary>
    private static double?[]? ParseCommaDelimitedNumbers(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        int count = parts.Length;
        while (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        return count == 0 ? null : parts.Take(count)
            .Select(part => TryParseMeasurement(part, out double number) ? (double?)number : null).ToArray();
    }

    private static bool TryParseMeasurement(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        PrinterPrintMetadata.IsValidNumber(number);

    private static double[]? CompleteNumbers(double?[]? values) =>
        values is { Length: > 0 } && values.All(value => value.HasValue)
            ? values.Select(value => value!.Value).ToArray() : null;

    /// <summary>
    /// Parses semicolon-delimited string metadata while preserving empty middle positions.
    /// Empty trailing delimiters are ignored because they do not represent extruders.
    /// </summary>
    private static string[]? ParseSemicolonDelimitedStrings(string value, Func<string, string>? normalize = null)
    {
        string[] parts = value.Split(';', StringSplitOptions.TrimEntries);
        int count = parts.Length;
        while (count > 0 && parts[count - 1].Length == 0)
        {
            count--;
        }

        if (count == 0)
        {
            return null;
        }

        string[] values = new string[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = normalize?.Invoke(parts[i]) ?? parts[i].Trim().Trim('"');
        }

        return values;
    }

    /// <summary>
    /// Normalizes slicer color tokens to uppercase #RRGGBB when possible.
    /// Invalid tokens are returned trimmed so extruder positions remain intact.
    /// </summary>
    private static string NormalizeHexColorToken(string token)
    {
        string trimmed = token.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string hex = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (!HexColorPattern.IsMatch(hex))
        {
            return trimmed;
        }

        return "#" + hex[..6].ToUpperInvariant();
    }

    /// <summary>
    /// Parses explicit duration units, including hour-only and prerelease slicer output.
    /// </summary>
    private static bool TryParsePrintTime(string line, out double minutes)
    {
        minutes = 0;
        MatchCollection matches = PrintTimeUnitPattern.Matches(line);
        foreach (Match match in matches)
        {
            if (!TryParseMeasurement(match.Groups[1].Value, out double value))
            {
                return false;
            }

            minutes += value * char.ToLowerInvariant(match.Groups[2].Value[0]) switch
            {
                'd' => 1440,
                'h' => 60,
                's' => 1.0 / 60,
                _ => 1,
            };
        }

        return matches.Count > 0 && double.IsFinite(minutes);
    }

    private static readonly Regex ThumbnailStartPattern = new(
        @"^thumbnail(?<format>_[A-Za-z]+)?\s+begin\s+(?<width>\d+)x(?<height>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThumbnailEndPattern = new(
        @"^thumbnail(?:_[A-Za-z]+)?\s+end\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private (PrinterFileEnrichmentState State, string? Reason) ExtractThumbnail(
        IReadOnlyList<string> lines, GcodeMetadataExtracted metadata,
        bool bounded = false, CancellationToken ct = default)
    {
        StringBuilder data = new();
        bool inBlock = false;
        bool skipBlock = false;
        bool isQoi = false;
        bool selectedQoi = false;
        long pixels = 0;
        long selectedPixels = -1;
        string? failure = null;
        int encodedLimit = ((PrinterFileEnrichmentLimits.MaximumImageBytes + 2) / 3) * 4;
        foreach (string line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith(';'))
            {
                continue;
            }

            string comment = line[1..].Trim();
            Match start = ThumbnailStartPattern.Match(comment);
            if (start.Success)
            {
                if (inBlock)
                {
                    failure = "incomplete-thumbnail";
                }

                inBlock = true;
                skipBlock = false;
                data.Clear();
                string format = start.Groups["format"].Value;
                isQoi = format.Equals("_QOI", StringComparison.OrdinalIgnoreCase);
                if (format.Length > 0 && !isQoi &&
                    !format.Equals("_PNG", StringComparison.OrdinalIgnoreCase) &&
                    !format.Equals("_JPG", StringComparison.OrdinalIgnoreCase) &&
                    !format.Equals("_JPEG", StringComparison.OrdinalIgnoreCase))
                {
                    skipBlock = true;
                    failure = "unsupported-thumbnail";
                }

                if (!int.TryParse(start.Groups["width"].Value, out int width) ||
                    !int.TryParse(start.Groups["height"].Value, out int height))
                {
                    skipBlock = true;
                    failure = "image-dimensions";
                    continue;
                }

                pixels = (long)width * height;
                if (bounded && (width is <= 0 or > PrinterFileEnrichmentLimits.MaximumImageEdge ||
                    height is <= 0 or > PrinterFileEnrichmentLimits.MaximumImageEdge ||
                    pixels > PrinterFileEnrichmentLimits.MaximumImagePixels))
                {
                    skipBlock = true;
                    failure = "image-dimensions";
                }

                continue;
            }

            if (ThumbnailEndPattern.IsMatch(comment) && inBlock)
            {
                inBlock = false;
                bool selectedPreferredFormat = !selectedQoi && isQoi;
                bool selectedLargerSameFormat = selectedQoi == isQoi && selectedPixels >= pixels;
                bool keepSelected = metadata.ThumbnailData is not null &&
                    (selectedPreferredFormat || selectedLargerSameFormat);
                if (skipBlock || keepSelected)
                {
                    continue;
                }

                try
                {
                    int padding = (4 - (data.Length % 4)) % 4;
                    data.Append('=', padding);
                    byte[] image = Convert.FromBase64String(data.ToString());
                    if (image.Length == 0 || (bounded && image.Length > PrinterFileEnrichmentLimits.MaximumImageBytes))
                    {
                        failure = "image-size";
                        continue;
                    }

                    metadata.ThumbnailData = image;
                    selectedPixels = pixels;
                    selectedQoi = isQoi;
                }
                catch (FormatException ex)
                {
                    _logger.LogDebug(ex, "Invalid base64 in G-code thumbnail");
                    failure = "invalid-thumbnail";
                }

                continue;
            }

            if (inBlock && !skipBlock)
            {
                if (bounded && comment.Length > encodedLimit - data.Length)
                {
                    skipBlock = true;
                    failure = "image-size";
                    data.Clear();
                }
                else
                {
                    data.Append(comment);
                }
            }
        }

        if (metadata.ThumbnailData is not null)
        {
            return (PrinterFileEnrichmentState.Ready, null);
        }

        failure = inBlock ? failure ?? "incomplete-thumbnail" : failure;
        if (failure is not null)
        {
            _logger.LogDebug("G-code thumbnail unavailable: {Reason}", failure);
            return (failure == "unsupported-thumbnail" ? PrinterFileEnrichmentState.Unsupported :
                PrinterFileEnrichmentState.Partial, failure);
        }

        return bounded ? (PrinterFileEnrichmentState.Partial, "not-in-bounded-source") :
            (PrinterFileEnrichmentState.Absent, null);
    }
}
