using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Gcode;

public class GcodeMetadataExtractorService(ILogger<GcodeMetadataExtractorService> logger) : IGcodeMetadataExtractorService
{
    private readonly ILogger<GcodeMetadataExtractorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
    private static readonly Regex SlicerGeneratedByPattern = new(@"(?:PrusaSlicer|OrcaSlicer|Cura|SuperSlicer)\s+([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CuraVersionPattern = new(@"Cura_Version:(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaterialPattern = new(@";\s*(?:filament_type|MATERIAL|material)\s*[:=]\s*([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NozzleDiameterPattern = new(@";\s*nozzle_diameter\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LayerHeightPattern = new(@";\s*layer_height\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrintTimeHoursPattern = new(@"(\d+)h\s+(\d+)m\s+(\d+)s", RegexOptions.Compiled);
    private static readonly Regex PrintTimeMinutesPattern = new(@"(\d+)m\s+(\d+)s", RegexOptions.Compiled);
    private static readonly Regex PrintTimeSecondsPattern = new(@"(?:TIME|Time):\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentLengthPattern = new(@";\s*filament_length\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentLengthConfigPattern = new(@";\s*filament used \[mm\]\s*[:=]\s*([\d.,\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentWeightPattern = new(@";\s*(?:filament_g|filament_weight)\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilamentWeightConfigPattern = new(@";\s*filament used \[g\]\s*[:=]\s*([\d.,\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BedTempPattern = new(@";\s*first_layer_bed_temperature\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NozzleTempPattern = new(@";\s*first_layer_temperature\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrinterModelPattern = new(@"; ?(?:printer_model|machine_name)\s*[:=]\s*(?!~\/)([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PrintSettingsPattern = new(@";\s*print_settings_id\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SparseInfillPattern = new(@";\s*sparse_infill_density\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FillDensityPattern = new(@";\s*fill_density\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WallLoopsPattern = new(@";\s*wall_loops\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerimetersPattern = new(@";\s*perimeters\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingEscapePattern = new(@"(\\[nrt])+$", RegexOptions.Compiled);

    // --- New patterns for expanded metadata ---
    private static readonly Regex TotalLayersPattern = new(@";\s*(?:total layers count|LAYER_COUNT|total_layer_count)\s*[:=]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FirstLayerHeightPattern = new(@";\s*first_layer_height\s*[:=]\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportEnabledPattern = new(@";\s*(?:support_material|enable_support)\s*[:=]\s*(\d+|true|false)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SupportTypePattern = new(@";\s*support_type\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    private void ExtractAllProperties(IReadOnlyList<string> lines, GcodeMetadataExtracted metadata)
    {
        // Print time uses priority: normal mode (3) > TIME: seconds (2) > generic estimated (1)
        int printTimePriority = 0;

        // Tracking for counted fields
        HashSet<string> uniqueObjects = new(StringComparer.OrdinalIgnoreCase);
        int toolChangeCount = 0;
        int? lastTool = null;

        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Track tool changes from non-comment G-code commands (e.g., "T0", "T1")
            if (!line.StartsWith(';'))
            {
                if (line.Length >= 2 && line[0] == 'T' && char.IsDigit(line[1])
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
            if (line.StartsWith("; printing object", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(";MESH:", StringComparison.OrdinalIgnoreCase))
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
                        metadata.SlicerName = match.Groups[0].Value.Split()[0];
                        metadata.SlicerVersion = match.Groups[1].Value;
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
                     !line.Contains("first layer", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParsePrintTime(line, out double minutes))
                {
                    metadata.EstimatedPrintTimeMinutes = minutes;
                    printTimePriority = 1;
                }
            }

            // --- Filament Length ---
            if (metadata.FilamentLengthMm == null)
            {
                Match match = FilamentLengthPattern.Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double length))
                {
                    metadata.FilamentLengthMm = length;
                    metadata.FilamentPerExtruderLengthMm = [length];
                }
                else
                {
                    match = FilamentLengthConfigPattern.Match(line);
                    if (match.Success)
                    {
                        double[]? perExtruder = ParseCommaDelimitedDoubles(match.Groups[1].Value);
                        if (perExtruder is { Length: > 0 })
                        {
                            metadata.FilamentPerExtruderLengthMm = perExtruder;
                            metadata.FilamentLengthMm = perExtruder.Sum();
                        }
                    }
                }
            }

            // --- Filament Weight ---
            if (metadata.FilamentWeightGrams == null &&
                !line.Contains("mm3", StringComparison.OrdinalIgnoreCase))
            {
                Match match = FilamentWeightPattern.Match(line);
                if (match.Success && !line.Contains("[", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(match.Groups[1].Value, out double weight))
                {
                    metadata.FilamentWeightGrams = weight;
                    metadata.FilamentPerExtruderWeightG = [weight];
                }
                else
                {
                    match = FilamentWeightConfigPattern.Match(line);
                    if (match.Success)
                    {
                        double[]? perExtruder = ParseCommaDelimitedDoubles(match.Groups[1].Value);
                        if (perExtruder is { Length: > 0 })
                        {
                            metadata.FilamentPerExtruderWeightG = perExtruder;
                            metadata.FilamentWeightGrams = perExtruder.Sum();
                        }
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
                else
                {
                    match = SupportTypePattern.Match(line);
                    if (match.Success)
                    {
                        string val = match.Groups[1].Value.Trim();
                        metadata.SupportEnabled = !string.Equals(val, "none", StringComparison.OrdinalIgnoreCase)
                                               && !string.Equals(val, "0", StringComparison.OrdinalIgnoreCase);
                    }
                }
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
        int weightCount = metadata.FilamentPerExtruderWeightG?.Length ?? 0;
        int lengthCount = metadata.FilamentPerExtruderLengthMm?.Length ?? 0;
        int maxExtruders = Math.Max(weightCount, lengthCount);
        if (maxExtruders > 1)
        {
            metadata.ExtruderCount = maxExtruders;
        }
    }

    /// <summary>
    /// Parses a comma-delimited string of doubles (e.g., "10.30,3.80" or "10.30, 3.80,").
    /// Trims whitespace, ignores empty segments from trailing commas.
    /// Uses invariant culture to ensure '.' is always the decimal separator.
    /// </summary>
    private static double[]? ParseCommaDelimitedDoubles(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<double> values = new(parts.Length);
        foreach (string part in parts)
        {
            if (double.TryParse(part, CultureInfo.InvariantCulture, out double d))
            {
                values.Add(d);
            }
        }

        return values.Count > 0 ? values.ToArray() : null;
    }

    /// <summary>
    /// Parse print time from "Xh Ym Zs" or "Ym Zs" format.
    /// </summary>
    private static bool TryParsePrintTime(string line, out double minutes)
    {
        minutes = 0;
        Match hoursMatch = PrintTimeHoursPattern.Match(line);
        if (hoursMatch.Success)
        {
            int h = int.Parse(hoursMatch.Groups[1].Value);
            int m = int.Parse(hoursMatch.Groups[2].Value);
            int s = int.Parse(hoursMatch.Groups[3].Value);
            minutes = (h * 60) + m + (s / 60.0);
            return true;
        }

        Match minMatch = PrintTimeMinutesPattern.Match(line);
        if (minMatch.Success)
        {
            int m = int.Parse(minMatch.Groups[1].Value);
            int s = int.Parse(minMatch.Groups[2].Value);
            minutes = m + (s / 60.0);
            return true;
        }

        return false;
    }

    private void ExtractThumbnail(string[] allLines, GcodeMetadataExtracted metadata)
    {
        // PrusaSlicer/OrcaSlicer format: `;thumbnail` comment blocks with base64-encoded image data
        //
        // PrusaSlicer embeds multiple thumbnails at different sizes in this order:
        // 1. Three QOI-format thumbnails (thumbnail_QOI begin/end blocks) at different resolutions
        // 2. Multiple PNG-format thumbnails (thumbnail begin/end block) at various resolutions
        //
        // OrcaSlicer embeds QOI format thumbnails early in the file
        //
        // Examples:
        // ;thumbnail_QOI begin 200x200 Q0/10
        // ;iVBORw0KGgoAAAANSUhEUgAAAMgAAADICAYAAACtWK6eAAA...
        // ;thumbnail_QOI end
        //
        // ;thumbnail begin 380x285
        // ;base64 PNG data...
        // ;thumbnail end
        //
        // We select the LARGEST PNG thumbnail for best quality previews.
        try
        {
            _logger.LogInformation("ExtractThumbnail: Starting thumbnail extraction from {LineCount} lines", allLines.Length.ToString());

            // Convert to list for easier processing
            var lines = new List<string>(allLines);

            // Store all found thumbnails with their format, dimensions, and data
            List<(string Format, int Width, int Height, List<string> Data)> thumbnailBlocks = new();
            List<string> currentThumbnailLines = new List<string>();
            string? currentThumbnailFormat = null;
            int currentWidth = 0;
            int currentHeight = 0;
            bool inThumbnail = false;

            foreach (string line in lines)
            {
                // Check if line contains thumbnail marker (handle both ";thumbnail" and "; thumbnail" formats)
                if (line.StartsWith(';'))
                {
                    string trimmedAfterSemicolon = line.Substring(1).TrimStart();
                    if (trimmedAfterSemicolon.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("ExtractThumbnail: Found thumbnail line: {Line}", line.Substring(0, Math.Min(80, line.Length)));

                        if (line.Contains("begin", StringComparison.OrdinalIgnoreCase))
                        {
                            // Detect format: QOI or PNG
                            currentThumbnailFormat = line.Contains("_QOI", StringComparison.OrdinalIgnoreCase) ? "QOI" : "PNG";

                            // Parse dimensions from line like ";thumbnail begin 380x285" or ";thumbnail_QOI begin 200x200 Q0/10"
                            currentWidth = 0;
                            currentHeight = 0;
                            Match dimensionMatch = Regex.Match(line, @"(\d+)x(\d+)", RegexOptions.IgnoreCase);
                            if (dimensionMatch.Success)
                            {
                                _ = int.TryParse(dimensionMatch.Groups[1].Value, out currentWidth);
                                _ = int.TryParse(dimensionMatch.Groups[2].Value, out currentHeight);
                            }

                            inThumbnail = true;
                            currentThumbnailLines = new List<string>();
                            _logger.LogDebug("ExtractThumbnail: {CurrentThumbnailFormat} thumbnail block started ({CurrentWidth}x{CurrentHeight})", currentThumbnailFormat, currentWidth, currentHeight);
                            continue;
                        }

                        if (line.Contains("end", StringComparison.OrdinalIgnoreCase))
                        {
                            if (inThumbnail)
                            {
                                // Store this thumbnail block with all its metadata
                                thumbnailBlocks.Add((currentThumbnailFormat!, currentWidth, currentHeight, new List<string>(currentThumbnailLines)));
                                _logger.LogDebug("ExtractThumbnail: {CurrentThumbnailFormat} thumbnail block ended ({CurrentWidth}x{CurrentHeight}), collected {CurrentThumbnailLinesCount} lines", currentThumbnailFormat, currentWidth, currentHeight, currentThumbnailLines.Count);
                                inThumbnail = false;
                                currentThumbnailFormat = null;
                                currentWidth = 0;
                                currentHeight = 0;
                            }
                        }
                    }
                }

                if (inThumbnail && line.StartsWith(';'))
                {
                    // Extract base64/data from comment line (remove leading ";")
                    string data = line.TrimStart(';').Trim();
                    if (!string.IsNullOrEmpty(data) && !data.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) && !data.StartsWith("THUMBNAIL", StringComparison.OrdinalIgnoreCase))
                    {
                        currentThumbnailLines.Add(data);
                    }
                }
            }

            _logger.LogInformation("ExtractThumbnail: Found {Count} thumbnail blocks total", thumbnailBlocks.Count.ToString());

            // Select the LARGEST PNG thumbnail (by pixel count = width * height)
            // If no PNG found, fall back to largest QOI
            (string? Format, int Width, int Height, List<string>? Lines) selectedThumbnail = default;

            // Get all PNG thumbnails and select the largest
            var pngThumbnails = thumbnailBlocks.Where(t => t.Format == "PNG").ToList();
            if (pngThumbnails.Count > 0)
            {
                var largest = pngThumbnails.MaxBy(t => t.Width * t.Height);
                selectedThumbnail = largest;
                _logger.LogInformation("ExtractThumbnail: Selected largest PNG thumbnail ({LargestWidth}x{LargestHeight}) from {PngThumbnailsCount} PNG options", largest.Width, largest.Height, pngThumbnails.Count);
            }
            else
            {
                // Fall back to largest QOI if no PNG available
                var qoiThumbnails = thumbnailBlocks.Where(t => t.Format == "QOI").ToList();
                if (qoiThumbnails.Count > 0)
                {
                    var largest = qoiThumbnails.MaxBy(t => t.Width * t.Height);
                    selectedThumbnail = largest;
                    _logger.LogInformation("ExtractThumbnail: Selected largest QOI thumbnail ({LargestWidth}x{LargestHeight}) from {QoiThumbnailsCount} QOI options (no PNG found)", largest.Width, largest.Height, qoiThumbnails.Count);
                }
                else if (thumbnailBlocks.Count > 0)
                {
                    // Fallback: use largest available thumbnail of any format
                    var largest = thumbnailBlocks.MaxBy(t => t.Width * t.Height);
                    selectedThumbnail = largest;
                    _logger.LogInformation("ExtractThumbnail: Selected largest {LargestFormat} thumbnail ({LargestWidth}x{LargestHeight}) as fallback", largest.Format, largest.Width, largest.Height);
                }
            }

            if (selectedThumbnail.Lines != null && selectedThumbnail.Lines.Count > 0)
            {
                string base64Data = string.Concat(selectedThumbnail.Lines);
                _logger.LogInformation("ExtractThumbnail: Attempting to decode {SelectedThumbnailFormat} thumbnail ({SelectedThumbnailWidth}x{SelectedThumbnailHeight}) with {Base64DataLength} base64 chars", selectedThumbnail.Format ?? "Unknown", selectedThumbnail.Width, selectedThumbnail.Height, base64Data.Length);

                // Log first and last lines for debugging
                if (selectedThumbnail.Lines.Count > 0)
                {
                    _logger.LogDebug("ExtractThumbnail: First base64 line (len={Len}): {Data}", selectedThumbnail.Lines[0].Length.ToString(), selectedThumbnail.Lines[0].Substring(0, Math.Min(50, selectedThumbnail.Lines[0].Length)));
                    if (selectedThumbnail.Lines.Count > 1)
                    {
                        _logger.LogDebug("ExtractThumbnail: Last base64 line (len={Len}): {Data}", selectedThumbnail.Lines[selectedThumbnail.Lines.Count - 1].Length.ToString(), selectedThumbnail.Lines[selectedThumbnail.Lines.Count - 1].Substring(0, Math.Min(50, selectedThumbnail.Lines[selectedThumbnail.Lines.Count - 1].Length)));
                    }
                }

                try
                {
                    // Pad base64 data to valid length if needed
                    int paddingNeeded = (4 - (base64Data.Length % 4)) % 4;
                    if (paddingNeeded > 0)
                    {
                        base64Data = base64Data + new string('=', paddingNeeded);
                    }

                    // Validate it's actual base64
                    if (IsValidBase64(base64Data))
                    {
                        metadata.ThumbnailData = Convert.FromBase64String(base64Data);
                        _logger.LogInformation("ExtractThumbnail: Successfully decoded {Length} bytes of {SelectedThumbnailFormat} thumbnail data ({SelectedThumbnailWidth}x{SelectedThumbnailHeight})", metadata.ThumbnailData.Length, selectedThumbnail.Format ?? "Unknown", selectedThumbnail.Width, selectedThumbnail.Height);
                    }
                    else
                    {
                        _logger.LogWarning("ExtractThumbnail: Base64 data validation failed (length={Length}, could not decode)", base64Data.Length.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode base64 thumbnail data from gcode");
                }
            }
            else
            {
                _logger.LogWarning("ExtractThumbnail: No thumbnail data lines found in GCODE");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting thumbnail from G-code");
        }
    }

    private static bool IsValidBase64(string input)
    {
        // Base64 should only contain valid characters: A-Z, a-z, 0-9, +, /, =
        try
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            // Try to decode - this is the safest check
            _ = Convert.FromBase64String(input);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
