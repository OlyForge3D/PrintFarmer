using System.Text.RegularExpressions;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Slicer.Module.Services;
using OrcaBundlePreviewDto = Farm.Slicer.Module.Models.OrcaBundlePreviewDto;
using OrcaFilamentPresetDto = Farm.Slicer.Module.Models.OrcaFilamentPresetDto;
using OrcaPrinterPresetDto = Farm.Slicer.Module.Models.OrcaPrinterPresetDto;
using OrcaProcessPresetDto = Farm.Slicer.Module.Models.OrcaProcessPresetDto;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Maps OrcaSlicer bundle presets to PrintFarmer catalog entities using fuzzy matching and confidence scoring.
/// </summary>
public sealed partial class OrcaPresetMappingService(ICatalogRepository catalogRepo) : IOrcaPresetMappingService
{
    private readonly ICatalogRepository _catalogRepo = catalogRepo ?? throw new ArgumentNullException(nameof(catalogRepo));

    public async Task<OrcaBundleMappingResult> MapBundlePresetsAsync(OrcaBundlePreviewDto preview, CancellationToken ct = default)
    {
        OrcaBundleMappingResult result = new OrcaBundleMappingResult();

        // Load catalog data for matching
        IReadOnlyList<PrinterModelDto> printerModels = await _catalogRepo.GetModelsCachedAsync(null, ct);

        IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)> manufacturerTuples = await _catalogRepo.GetManufacturersAsync(ct);
        Dictionary<Guid, string> manufacturerLookup = manufacturerTuples
            .ToDictionary(m => m.Id, m => m.Name);

        // Map printer presets
        foreach (OrcaPrinterPresetDto printer in preview.Printers)
        {
            PrinterPresetMatch match = MapPrinterPreset(printer, printerModels, manufacturerLookup);
            result.PrinterMatches.Add(match);
        }

        // Map filament presets
        foreach (OrcaFilamentPresetDto filament in preview.Filaments)
        {
            FilamentPresetMatch match = MapFilamentPreset(filament);
            result.FilamentMatches.Add(match);
        }

        // Map process presets
        foreach (OrcaProcessPresetDto process in preview.Processes)
        {
            ProcessPresetMatch match = MapProcessPreset(process);
            result.ProcessMatches.Add(match);
        }

        // Calculate statistics
        List<double> allMatches = result.PrinterMatches.Select(m => m.ConfidenceScore)
            .Concat(result.FilamentMatches.Select(m => m.ConfidenceScore))
            .Concat(result.ProcessMatches.Select(m => m.ConfidenceScore))
            .ToList();

        result.TotalPresets = allMatches.Count;
        result.HighConfidenceMatches = allMatches.Count(s => s >= 0.7);
        result.MediumConfidenceMatches = allMatches.Count(s => s >= 0.4 && s < 0.7);
        result.LowConfidenceMatches = allMatches.Count(s => s < 0.4);

        return result;
    }

    private PrinterPresetMatch MapPrinterPreset(
        OrcaPrinterPresetDto preset,
        IReadOnlyList<PrinterModelDto> catalogModels,
        Dictionary<Guid, string> manufacturerLookup)
    {
        PrinterPresetMatch match = new PrinterPresetMatch
        {
            Preset = preset,
            ConfidenceScore = 0.0
        };

        PrinterModelDto? bestMatch = null;
        double bestScore = 0.0;
        List<string> reasons = [];

        foreach (PrinterModelDto model in catalogModels)
        {
            double score = 0.0;
            List<string> matchReasons = [];

            // Model name similarity (40% weight)
            double nameScore = CalculateStringSimilarity(preset.PrinterModel ?? preset.Name, model.Name);
            score += nameScore * 0.4;
            if (nameScore > 0.5)
            {
                matchReasons.Add($"Model name similarity: {nameScore:P0}");
            }

            // Manufacturer match (30% weight)
            string? manufacturerName = manufacturerLookup.TryGetValue(model.ManufacturerId, out string? mfgName) ? mfgName : null;
            if (manufacturerName != null && !string.IsNullOrWhiteSpace(preset.Manufacturer))
            {
                double mfgScore = CalculateStringSimilarity(preset.Manufacturer, manufacturerName);
                score += mfgScore * 0.3;
                if (mfgScore > 0.7)
                {
                    matchReasons.Add($"Manufacturer match: {manufacturerName}");
                }
            }

            // Build volume match (20% weight) - within 10% tolerance
            if (model.MaxX > 0 && model.MaxY > 0 && model.MaxZ > 0)
            {
                double volumeScore = CalculateVolumeSimilarity(
                    preset.BedWidth, preset.BedDepth, preset.MaxZHeight,
                    (double)model.MaxX!, (double)model.MaxY!, (double)model.MaxZ!);
                score += volumeScore * 0.2;
                if (volumeScore > 0.8)
                {
                    matchReasons.Add($"Build volume match: {model.MaxX}x{model.MaxY}x{model.MaxZ}mm");
                }
            }

            // Nozzle diameter match (10% weight) - get from primary toolhead
            PrinterModelToolheadDto? primaryToolhead = model.Toolheads?.FirstOrDefault(t => t.IsPrimary) ?? model.Toolheads?.FirstOrDefault();
            double? modelNozzleDiameter = primaryToolhead?.NozzleDiameter;
            if (modelNozzleDiameter > 0 && Math.Abs(preset.NozzleDiameter - (double)modelNozzleDiameter) < 0.05)
            {
                score += 0.1;
                matchReasons.Add($"Nozzle diameter: {modelNozzleDiameter}mm");
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = model;
                reasons = matchReasons;
            }
        }

        if (bestMatch != null)
        {
            match.MatchedPrinterModelId = bestMatch.Id;
            match.MatchedPrinterModelName = bestMatch.Name;
            match.MatchedManufacturerName = manufacturerLookup.TryGetValue(bestMatch.ManufacturerId, out string? matchedMfgName) ? matchedMfgName : null;
            match.ConfidenceScore = bestScore;
            match.MatchReasons = reasons;
        }

        return match;
    }

    private FilamentPresetMatch MapFilamentPreset(OrcaFilamentPresetDto preset)
    {
        FilamentPresetMatch match = new FilamentPresetMatch
        {
            Preset = preset,
            ConfidenceScore = 0.0
        };

        // Known material types mapping
        Dictionary<string, string> knownMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "PLA", "PLA" },
            { "PETG", "PETG" },
            { "ABS", "ABS" },
            { "TPU", "TPU" },
            { "Nylon", "Nylon" },
            { "ASA", "ASA" },
            { "PC", "Polycarbonate" },
            { "PVA", "PVA" },
            { "HIPS", "HIPS" }
        };

        if (!string.IsNullOrWhiteSpace(preset.FilamentType))
        {
            // Direct match
            if (knownMaterials.TryGetValue(preset.FilamentType, out string? mapped))
            {
                match.MatchedMaterialType = mapped;
                match.ConfidenceScore = 1.0;
                match.MatchReasons.Add($"Direct material type match: {mapped}");
            }
            else
            {
                // Fuzzy match
                var bestMatch = knownMaterials.Keys
                    .Select(k => new { Key = k, Score = CalculateStringSimilarity(preset.FilamentType, k) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (bestMatch != null && bestMatch.Score > 0.6)
                {
                    match.MatchedMaterialType = knownMaterials[bestMatch.Key];
                    match.ConfidenceScore = bestMatch.Score;
                    match.MatchReasons.Add($"Fuzzy material match: {bestMatch.Key} ({bestMatch.Score:P0})");
                }
                else
                {
                    // Fallback to preset value
                    match.MatchedMaterialType = preset.FilamentType;
                    match.ConfidenceScore = 0.3;
                    match.MatchReasons.Add("Unknown material type, using preset value");
                }
            }
        }
        else
        {
            match.MatchedMaterialType = "PLA"; // Default
            match.ConfidenceScore = 0.1;
            match.MatchReasons.Add("No material type specified, defaulting to PLA");
        }

        return match;
    }

    private ProcessPresetMatch MapProcessPreset(OrcaProcessPresetDto preset)
    {
        ProcessPresetMatch match = new ProcessPresetMatch
        {
            Preset = preset,
            ConfidenceScore = 1.0 // Process presets are always high confidence as they map to internal quality levels
        };

        // Derive quality from layer height or explicit quality field
        if (!string.IsNullOrWhiteSpace(preset.Quality))
        {
            match.DerivedQuality = NormalizeQuality(preset.Quality);
            match.MatchReasons.Add($"Explicit quality: {preset.Quality}");
        }
        else
        {
            // Heuristic based on layer height
            match.DerivedQuality = preset.LayerHeight switch
            {
                <= 0.12 => "Fine",
                <= 0.2 => "Standard",
                _ => "Draft"
            };
            match.MatchReasons.Add($"Quality derived from layer height: {preset.LayerHeight}mm → {match.DerivedQuality}");
        }

        return match;
    }

    /// <summary>
    /// Calculates string similarity using Levenshtein distance ratio (0.0 to 1.0).
    /// </summary>
    private double CalculateStringSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.0;
        }

        // Normalize: lowercase, remove special chars, collapse whitespace
        a = NormalizeString(a);
        b = NormalizeString(b);

        if (a == b)
        {
            return 1.0;
        }

        int distance = LevenshteinDistance(a, b);
        int maxLength = Math.Max(a.Length, b.Length);

        return maxLength == 0 ? 1.0 : 1.0 - ((double)distance / maxLength);
    }

    private static string NormalizeString(string input)
    {
        // Remove special characters, convert to lowercase, collapse whitespace
        string normalized = SpecialCharsRegex().Replace(input, " ");
        normalized = normalized.ToLowerInvariant().Trim();
        normalized = WhitespaceRegex().Replace(normalized, " ");
        return normalized;
    }

    [GeneratedRegex(@"[^a-z0-9\s]", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Computes Levenshtein distance between two strings.
    /// </summary>
    private int LevenshteinDistance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int j = 1; j <= b.Length; j++)
        {
            for (int i = 1; i <= a.Length; i++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>
    /// Calculates build volume similarity (0.0 to 1.0) with 10% tolerance.
    /// </summary>
    private double CalculateVolumeSimilarity(
        double x1, double y1, double z1,
        double x2, double y2, double z2)
    {
        double xSim = 1.0 - Math.Min(Math.Abs(x1 - x2) / Math.Max(x1, x2), 1.0);
        double ySim = 1.0 - Math.Min(Math.Abs(y1 - y2) / Math.Max(y1, y2), 1.0);
        double zSim = 1.0 - Math.Min(Math.Abs(z1 - z2) / Math.Max(z1, z2), 1.0);

        // Average similarity across all dimensions
        return (xSim + ySim + zSim) / 3.0;
    }

    private string NormalizeQuality(string quality)
    {
        string normalized = quality.ToLowerInvariant().Trim();

        return normalized switch
        {
            "draft" or "fast" or "low" => "Draft",
            "standard" or "normal" or "medium" => "Standard",
            "fine" or "high" or "quality" => "Fine",
            _ => "Standard"
        };
    }
}
