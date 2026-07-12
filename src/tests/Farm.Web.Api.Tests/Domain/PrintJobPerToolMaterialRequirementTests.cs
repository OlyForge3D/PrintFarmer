using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Xunit;

namespace Farm.Web.Api.Tests.Domain;

/// <summary>
/// Unit tests for the per-tool material requirement extensions added to <see cref="PrintJob"/>
/// as part of GitHub issue OlyForge3D/PrintFarmer#710 (guided filament swap flow).
/// </summary>
public class PrintJobPerToolMaterialRequirementTests
{
    [Fact]
    public void RequiredMaterialsPerTool_WhenNull_LeavesJsonNullAndPreservesLegacyMaterial()
    {
        var job = new PrintJob { RequiredMaterialType = "PLA" };

        job.RequiredMaterialsPerTool = null;

        Assert.Null(job.RequiredMaterialsPerToolJson);
        Assert.Null(job.RequiredMaterialsPerTool);
        // Backward compatibility: legacy single-material field is preserved untouched.
        Assert.Equal("PLA", job.RequiredMaterialType);
    }

    [Fact]
    public void RequiredMaterialsPerTool_SingleTool_RoundTripsThroughJson()
    {
        var job = new PrintJob
        {
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", "#FF0000", 25.5)
            }
        };

        Assert.NotNull(job.RequiredMaterialsPerToolJson);
        Assert.Contains("\"tool\":0", job.RequiredMaterialsPerToolJson);
        Assert.Contains("\"materialType\":\"PLA\"", job.RequiredMaterialsPerToolJson);
        Assert.Contains("\"colorHint\":\"#FF0000\"", job.RequiredMaterialsPerToolJson);

        IReadOnlyList<PrintJobToolMaterialRequirement>? readBack = job.RequiredMaterialsPerTool;
        Assert.NotNull(readBack);
        var single = Assert.Single(readBack!);
        Assert.Equal(0, single.Tool);
        Assert.Equal("PLA", single.MaterialType);
        Assert.Equal("#FF0000", single.ColorHint);
        Assert.Equal(25.5, single.EstimatedGrams);
    }

    [Fact]
    public void RequiredMaterialsPerTool_MultiTool_PreservesOrderAndOptionalFields()
    {
        var job = new PrintJob
        {
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", "#1A1A1A", 30),
                new(1, "PETG", ColorHint: null, EstimatedGrams: null),
                new(2, "TPU", "#00FF00", 5.25)
            }
        };

        IReadOnlyList<PrintJobToolMaterialRequirement>? readBack = job.RequiredMaterialsPerTool;
        Assert.NotNull(readBack);
        Assert.Equal(3, readBack!.Count);
        Assert.Equal(new[] { 0, 1, 2 }, System.Linq.Enumerable.Select(readBack, r => r.Tool));
        Assert.Equal("PETG", readBack[1].MaterialType);
        Assert.Null(readBack[1].ColorHint);
        Assert.Null(readBack[1].EstimatedGrams);
    }

    [Fact]
    public void RequiredMaterialsPerTool_UsedToolWithUnknownMaterial_RoundTripsThroughJson()
    {
        var job = new PrintJob
        {
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(1, MaterialType: null, ColorHint: null, EstimatedGrams: 4.25),
            },
        };

        Assert.Contains("\"tool\":1", job.RequiredMaterialsPerToolJson);
        Assert.Contains("\"materialType\":null", job.RequiredMaterialsPerToolJson);

        PrintJobToolMaterialRequirement requirement = Assert.Single(job.RequiredMaterialsPerTool!);
        Assert.Equal(1, requirement.Tool);
        Assert.Null(requirement.MaterialType);
        Assert.Equal(4.25, requirement.EstimatedGrams);
    }

    [Fact]
    public void RequiredMaterialsPerTool_InvalidJson_ReturnsNull()
    {
        var job = new PrintJob { RequiredMaterialsPerToolJson = "not-json" };

        Assert.Null(job.RequiredMaterialsPerTool);
    }

    [Fact]
    public void RequiredMaterialsPerTool_EmptyList_SerializesToEmptyArray()
    {
        var job = new PrintJob
        {
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>()
        };

        Assert.Equal("[]", job.RequiredMaterialsPerToolJson);
        IReadOnlyList<PrintJobToolMaterialRequirement>? readBack = job.RequiredMaterialsPerTool;
        Assert.NotNull(readBack);
        Assert.Empty(readBack!);
    }

    [Fact]
    public void PrintJobToolMaterialRequirement_SerializesUsingCamelCaseNames()
    {
        var req = new PrintJobToolMaterialRequirement(1, "PETG", "#0055FF", 12.4);

        string json = JsonSerializer.Serialize(req);

        Assert.Contains("\"tool\":1", json);
        Assert.Contains("\"materialType\":\"PETG\"", json);
        Assert.Contains("\"colorHint\":\"#0055FF\"", json);
        Assert.Contains("\"estimatedGrams\":12.4", json);
        Assert.DoesNotContain("\"Tool\"", json);
        Assert.DoesNotContain("\"MaterialType\"", json);
    }
}
