using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.PrintJobs;

/// <summary>
/// Shared, dependency-free helpers that project slicer G-code metadata onto a
/// <see cref="PrintJob"/> at enqueue / rerun time.
/// <para>
/// Owned by the Infrastructure layer so every production entry point that creates a
/// <see cref="PrintJob"/> (queue, SlicePrintBridge, OctoPrint, projects, approvals,
/// analytics enqueue, rerun) can call the exact same routine. This is the single
/// source of truth for populating <see cref="PrintJob.RequiredMaterialsPerTool"/>
/// and for resolving the effective scalar <see cref="PrintJob.RequiredMaterialType"/>
/// (request value falling back to G-code metadata).
/// </para>
/// </summary>
/// <remarks>
/// Introduced as part of GitHub issue OlyForge3D/PrintFarmer#710 (guided filament
/// swap flow) to close the "only wired to analytics enqueue" gap identified in the
/// pre-PR consensus review.
/// </remarks>
public static class PrintJobRequirementsMapper
{
    /// <summary>
    /// Resolves the effective single-material scalar to persist on
    /// <see cref="PrintJob.RequiredMaterialType"/>. Request-supplied value wins; falls
    /// back to <see cref="GcodeFile.RequiredMaterial"/> when the request left the value
    /// blank. Returns <c>null</c> if neither has a value.
    /// </summary>
    public static string? ResolveEffectiveMaterial(string? requestMaterial, GcodeFile? gcodeFile)
    {
        if (!string.IsNullOrWhiteSpace(requestMaterial))
        {
            return requestMaterial;
        }

        string? fromGcode = gcodeFile?.RequiredMaterial;
        return string.IsNullOrWhiteSpace(fromGcode) ? null : fromGcode;
    }

    /// <summary>
    /// Populates <see cref="PrintJob.RequiredMaterialsPerTool"/> from the slicer
    /// per-extruder metadata carried on <paramref name="gcodeFile"/>. Entry presence records
    /// authoritative tool usage even when the material label is blank or missing. No-op when
    /// the source lacks both material and usage data — the legacy single-material scalar
    /// continues to drive validation in that case.
    /// </summary>
    /// <param name="job">The newly constructed print job to mutate.</param>
    /// <param name="gcodeFile">The G-code file supplying slicer metadata. May be null.</param>
    public static void PopulateFromGcode(PrintJob job, GcodeFile? gcodeFile)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (gcodeFile is null)
        {
            return;
        }

        string[]? materials = TryParseJsonArray<string>(gcodeFile.FilamentPerExtruderType);
        double[]? weights = TryParseJsonArray<double>(gcodeFile.FilamentPerExtruderWeightG);
        int toolCount = Math.Max(materials?.Length ?? 0, weights?.Length ?? 0);
        if (toolCount == 0)
        {
            return;
        }

        string[]? colors = TryParseJsonArray<string>(gcodeFile.FilamentPerExtruderColorHex);

        var reqs = new List<PrintJobToolMaterialRequirement>(toolCount);
        for (int tool = 0; tool < toolCount; tool++)
        {
            string? material = materials is not null && tool < materials.Length
                ? materials[tool]
                : null;
            material = string.IsNullOrWhiteSpace(material) ? null : material.Trim();

            bool hasWeightSignal = weights is not null && tool < weights.Length;
            double? grams = hasWeightSignal ? weights![tool] : null;
            bool isUsed = hasWeightSignal ? grams > 0 : material is not null;
            if (!isUsed)
            {
                continue;
            }

            string? color = colors is not null && tool < colors.Length ? colors[tool] : null;
            if (string.IsNullOrWhiteSpace(color))
            {
                color = null;
            }

            reqs.Add(new PrintJobToolMaterialRequirement(tool, material, color, grams));
        }

        if (reqs.Count > 0)
        {
            job.RequiredMaterialsPerTool = reqs;
        }
    }

    /// <summary>
    /// Copies per-tool material requirements from <paramref name="sourceJob"/> to
    /// <paramref name="newJob"/> for rerun/copy paths. Prefers the already-computed
    /// JSON verbatim (cheap and lossless). If the source is missing per-tool data,
    /// attempts to re-derive from <paramref name="gcodeFile"/>.
    /// </summary>
    public static void CopyFrom(PrintJob newJob, PrintJob sourceJob, GcodeFile? gcodeFile = null)
    {
        ArgumentNullException.ThrowIfNull(newJob);
        ArgumentNullException.ThrowIfNull(sourceJob);

        if (!string.IsNullOrWhiteSpace(sourceJob.RequiredMaterialsPerToolJson))
        {
            newJob.RequiredMaterialsPerToolJson = sourceJob.RequiredMaterialsPerToolJson;
            return;
        }

        PopulateFromGcode(newJob, gcodeFile);
    }

    /// <summary>
    /// Projects a print job's authoritative <see cref="PrintJob.RequiredMaterialsPerTool"/>
    /// onto the public <c>toolRequirements[]</c> wire shape without mutating the source. Returns
    /// an empty list (never null) when the job has no per-tool requirements, so response DTOs
    /// always carry a stable array (GitHub issue OlyForge3D/PrintFarmer#710, B5).
    /// </summary>
    public static List<PrintJobToolRequirementDto> ToWireRequirements(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        IReadOnlyList<PrintJobToolMaterialRequirement>? perTool = job.RequiredMaterialsPerTool;
        if (perTool is null || perTool.Count == 0)
        {
            return new List<PrintJobToolRequirementDto>();
        }

        var wire = new List<PrintJobToolRequirementDto>(perTool.Count);
        foreach (PrintJobToolMaterialRequirement req in perTool)
        {
            if (string.IsNullOrWhiteSpace(req.MaterialType))
            {
                continue;
            }

            wire.Add(new PrintJobToolRequirementDto(
                ToolIndex: req.Tool,
                MaterialType: req.MaterialType,
                ColorHint: req.ColorHint,
                EstimatedGrams: req.EstimatedGrams));
        }

        return wire;
    }

    private static T[]? TryParseJsonArray<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
