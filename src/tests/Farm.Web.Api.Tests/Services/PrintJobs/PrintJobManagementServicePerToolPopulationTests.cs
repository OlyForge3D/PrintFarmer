using System.Linq;
using System.Text.Json;
using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PrintJobs;

/// <summary>
/// Unit tests for
/// <c>PrintJobManagementService.PopulatePerToolRequirementsFromGcode</c>, which projects
/// slicer per-extruder metadata from <see cref="GcodeFile"/> onto a newly enqueued
/// <see cref="PrintJob"/> as part of GitHub issue OlyForge3D/PrintFarmer#710.
/// </summary>
public class PrintJobManagementServicePerToolPopulationTests
{
    [Fact]
    public void PopulatePerToolRequirementsFromGcode_SingleExtruder_PopulatesSingleRequirement()
    {
        var job = new PrintJob { RequiredMaterialType = "PLA" };
        var gcode = new GcodeFile
        {
            FileName = "cube.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 12.3 }),
            FilamentPerExtruderColorHex = JsonSerializer.Serialize(new[] { "#1A1A1A" }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        var reqs = job.RequiredMaterialsPerTool;
        Assert.NotNull(reqs);
        var single = Assert.Single(reqs!);
        Assert.Equal(0, single.Tool);
        Assert.Equal("PLA", single.MaterialType);
        Assert.Equal("#1A1A1A", single.ColorHint);
        Assert.Equal(12.3, single.EstimatedGrams);
        // Backward compatibility: legacy single-material field is untouched.
        Assert.Equal("PLA", job.RequiredMaterialType);
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_MultiTool_PopulatesAllRequirements()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "multi.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "PETG", "TPU" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 10.0, 5.5, 1.25 }),
            FilamentPerExtruderColorHex = JsonSerializer.Serialize(new[] { "#000000", "#FFFFFF", "#FF0000" }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        var reqs = job.RequiredMaterialsPerTool;
        Assert.NotNull(reqs);
        Assert.Equal(3, reqs!.Count);
        Assert.Equal(new[] { 0, 1, 2 }, reqs.Select(r => r.Tool).ToArray());
        Assert.Equal(new[] { "PLA", "PETG", "TPU" }, reqs.Select(r => r.MaterialType).ToArray());
        Assert.Equal(new[] { "#000000", "#FFFFFF", "#FF0000" }, reqs.Select(r => r.ColorHint).ToArray());
        Assert.Equal(new double?[] { 10.0, 5.5, 1.25 }, reqs.Select(r => r.EstimatedGrams).ToArray());
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_MissingMetadata_LeavesRequirementsNull()
    {
        var job = new PrintJob { RequiredMaterialType = "PETG" };
        var gcode = new GcodeFile { FileName = "no-metadata.gcode" };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        // No per-extruder metadata → per-tool requirement list is not populated,
        // and legacy single-material behaviour continues to drive validation.
        Assert.Null(job.RequiredMaterialsPerTool);
        Assert.Null(job.RequiredMaterialsPerToolJson);
        Assert.Equal("PETG", job.RequiredMaterialType);
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_SkipsBlankMaterialSlots()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "with-empty-slot.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "", "PETG" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 10.0, 0.0, 5.0 }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        var reqs = job.RequiredMaterialsPerTool;
        Assert.NotNull(reqs);
        // Slot 1 (empty) is skipped; slot 2 keeps its original tool index for downstream lookup.
        Assert.Equal(new[] { 0, 2 }, reqs!.Select(r => r.Tool).ToArray());
        Assert.Equal(new[] { "PLA", "PETG" }, reqs.Select(r => r.MaterialType).ToArray());
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_InvalidWeightJson_StillProducesTypeOnlyRequirements()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "corrupt-weights.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "PETG" }),
            FilamentPerExtruderWeightG = "not-json",
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        var reqs = job.RequiredMaterialsPerTool;
        Assert.NotNull(reqs);
        Assert.Equal(2, reqs!.Count);
        Assert.All(reqs, r => Assert.Null(r.EstimatedGrams));
    }
}
