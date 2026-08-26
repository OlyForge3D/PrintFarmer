using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.PrintJobs;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
    public void PopulatePerToolRequirementsFromGcode_UsedBlankMaterial_PersistsUnknownEntry()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "used-unknown.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 10.0, 4.0 }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        IReadOnlyList<PrintJobToolMaterialRequirement> reqs = job.RequiredMaterialsPerTool!;
        Assert.Equal(new[] { 0, 1 }, reqs.Select(r => r.Tool));
        Assert.Equal("PLA", reqs[0].MaterialType);
        Assert.Null(reqs[1].MaterialType);
        Assert.Contains("\"materialType\":null", job.RequiredMaterialsPerToolJson);
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_MissingMaterialArray_UsesWeightAsUsageSignal()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "missing-types.gcode",
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 0.0, 6.5 }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        PrintJobToolMaterialRequirement requirement = Assert.Single(job.RequiredMaterialsPerTool!);
        Assert.Equal(1, requirement.Tool);
        Assert.Null(requirement.MaterialType);
        Assert.Equal(6.5, requirement.EstimatedGrams);
    }

    [Fact]
    public void PopulatePerToolRequirementsFromGcode_ZeroWeightKnownSlot_IsUnused()
    {
        var job = new PrintJob();
        var gcode = new GcodeFile
        {
            FileName = "configured-unused.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "PETG" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 10.0, 0.0 }),
        };

        PrintJobManagementService.PopulatePerToolRequirementsFromGcode(job, gcode);

        PrintJobToolMaterialRequirement requirement = Assert.Single(job.RequiredMaterialsPerTool!);
        Assert.Equal(0, requirement.Tool);
        Assert.Equal("PLA", requirement.MaterialType);
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

    // ── Rerun production-entry regressions (issue #710) ──

    [Fact]
    public async Task RerunJobAsync_CopiesPerToolRequirementsFromOriginalJob()
    {
        // Reviewer requirement (#710): rerun must copy the source job's per-tool
        // requirement list verbatim so multi-material validation still works after
        // a rerun without re-reading the G-code file.
        var originalId = Guid.NewGuid();
        var original = new PrintJob
        {
            Id = originalId,
            Name = "orig",
            GcodeFileId = null,
            RequiredMaterialType = "PLA",
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(0, "PLA", "#111111", 12.0),
                new(1, "PETG", "#222222", 4.5),
            },
        };

        var repo = new Mock<IPrintJobManagementRepository>();
        repo.Setup(r => r.GetByIdAsync(originalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        repo.Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        PrintJob? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .Callback<PrintJob, CancellationToken>((j, _) => added = j)
            .ReturnsAsync((PrintJob j, CancellationToken _) => j);

        PrintJobManagementService service = CreateService(repo);
        _ = await service.RerunJobAsync(originalId.ToString(), "user-42");

        Assert.NotNull(added);
        Assert.NotSame(original, added);
        Assert.Equal("PLA", added!.RequiredMaterialType);
        Assert.NotNull(added.RequiredMaterialsPerTool);
        Assert.Equal(2, added.RequiredMaterialsPerTool!.Count);
        Assert.Equal(new[] { 0, 1 }, added.RequiredMaterialsPerTool.Select(r => r.Tool));
        Assert.Equal(new[] { "PLA", "PETG" }, added.RequiredMaterialsPerTool.Select(r => r.MaterialType));
        Assert.Equal(new double?[] { 12.0, 4.5 }, added.RequiredMaterialsPerTool.Select(r => r.EstimatedGrams));
    }

    [Fact]
    public async Task RerunJobAsync_RederivesPerToolRequirementsFromGcode_WhenOriginalMissingProjection()
    {
        // Rerunning a pre-#710 job (no per-tool projection stored) must fall back
        // to the linked G-code file so validation isn't silently downgraded on rerun.
        var originalId = Guid.NewGuid();
        var gcodeId = Guid.NewGuid();
        var original = new PrintJob
        {
            Id = originalId,
            Name = "legacy",
            GcodeFileId = gcodeId,
            RequiredMaterialType = "PLA",
            RequiredMaterialsPerTool = null,
        };
        var gcode = new GcodeFile
        {
            Id = gcodeId,
            Name = "legacy.gcode",
            FileName = "legacy.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "PETG" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 8.0, 3.2 }),
        };

        var repo = new Mock<IPrintJobManagementRepository>();
        repo.Setup(r => r.GetByIdAsync(originalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        repo.Setup(r => r.GetGcodeFileAsync(gcodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);
        repo.Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        PrintJob? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .Callback<PrintJob, CancellationToken>((j, _) => added = j)
            .ReturnsAsync((PrintJob j, CancellationToken _) => j);

        PrintJobManagementService service = CreateService(repo);
        _ = await service.RerunJobAsync(originalId.ToString(), "user-42");

        Assert.NotNull(added);
        Assert.NotNull(added!.RequiredMaterialsPerTool);
        Assert.Equal(new[] { 0, 1 }, added.RequiredMaterialsPerTool!.Select(r => r.Tool));
        Assert.Equal(new[] { "PLA", "PETG" }, added.RequiredMaterialsPerTool.Select(r => r.MaterialType));
    }

    [Fact]
    public async Task EnqueueJobAsync_OmittedRequestMaterial_FallsBackToGcodeRequiredMaterial()
    {
        // B4: the production enqueue entry point (NOT a helper) must resolve the effective
        // material from the linked G-code file when the request omits RequiredMaterialType,
        // exactly like JobQueueService.AddJobToQueueAsync does.
        var gcodeId = Guid.NewGuid();
        var gcode = new GcodeFile
        {
            Id = gcodeId,
            Name = "single.gcode",
            FileName = "single.gcode",
            RequiredMaterial = "PETG", // authoritative fallback for a single-tool file
        };

        var repo = new Mock<IPrintJobManagementRepository>();
        repo.Setup(r => r.GetGcodeFileAsync(gcodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);
        repo.Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        PrintJob? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .Callback<PrintJob, CancellationToken>((j, _) => added = j)
            .ReturnsAsync((PrintJob j, CancellationToken _) => j);

        PrintJobManagementService service = CreateService(repo);

        var request = new EnqueueQueueJobRequest
        {
            GcodeFileId = gcodeId.ToString(),
            RequiredMaterialType = null, // omitted → must fall back to gcode
        };

        QueuedPrintJobDto dto = await service.EnqueueJobAsync(request, "user-1");

        Assert.NotNull(added);
        Assert.Equal("PETG", added!.RequiredMaterialType);
        Assert.Equal("PETG", dto.RequiredMaterialType);
    }

    [Fact]
    public async Task EnqueueJobAsync_ProjectsPerToolRequirements_OntoWireContract()
    {
        // B5: the QueuedPrintJobDto returned by the production enqueue path must carry the
        // public toolRequirements[] wire array projected from the authoritative per-tool
        // requirements, while preserving the legacy RequiredMaterialType.
        var gcodeId = Guid.NewGuid();
        var gcode = new GcodeFile
        {
            Id = gcodeId,
            Name = "multi.gcode",
            FileName = "multi.gcode",
            FilamentPerExtruderType = JsonSerializer.Serialize(new[] { "PLA", "PETG" }),
            FilamentPerExtruderWeightG = JsonSerializer.Serialize(new[] { 12.0, 4.5 }),
        };

        var repo = new Mock<IPrintJobManagementRepository>();
        repo.Setup(r => r.GetGcodeFileAsync(gcodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);
        repo.Setup(r => r.GetMaxQueuePositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        repo.Setup(r => r.AddAsync(It.IsAny<PrintJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJob j, CancellationToken _) => j);

        PrintJobManagementService service = CreateService(repo);

        QueuedPrintJobDto dto = await service.EnqueueJobAsync(
            new EnqueueQueueJobRequest { GcodeFileId = gcodeId.ToString() },
            "user-1");

        Assert.Equal(2, dto.ToolRequirements.Count);
        Assert.Equal(new[] { 0, 1 }, dto.ToolRequirements.Select(r => r.ToolIndex));
        Assert.Equal(new[] { "PLA", "PETG" }, dto.ToolRequirements.Select(r => r.MaterialType));
        Assert.Equal(12.0, dto.ToolRequirements[0].EstimatedGrams);
    }

    [Fact]
    public void ToWireRequirements_OmitsUnknownInternalEntries_AndDoesNotSynthesizeScalarT0()
    {
        var job = new PrintJob
        {
            RequiredMaterialType = "PLA",
            RequiredMaterialsPerTool = new List<PrintJobToolMaterialRequirement>
            {
                new(1, MaterialType: null, ColorHint: null, EstimatedGrams: 4.0),
            },
        };

        List<PrintJobToolRequirementDto> wire = PrintJobRequirementsMapper.ToWireRequirements(job);

        Assert.Empty(wire);

        var scalarOnly = new PrintJob { RequiredMaterialType = "PETG" };
        Assert.Empty(PrintJobRequirementsMapper.ToWireRequirements(scalarOnly));
    }

    [Fact]
    public void QueuedPrintJobDto_SerializesToolRequirements_WithExactWireFieldNames()
    {
        // B5: the public toolRequirements[] contract must serialize with the exact Dallas
        // field names/types (toolIndex, materialType, colorHint, estimatedGrams) in camelCase,
        // while preserving the legacy requiredMaterialType scalar.
        var dto = new QueuedPrintJobDto
        {
            RequiredMaterialType = "PLA",
            ToolRequirements =
            [
                new PrintJobToolRequirementDto(ToolIndex: 0, MaterialType: "PLA", ColorHint: "#FF0000", EstimatedGrams: 12.5),
                new PrintJobToolRequirementDto(ToolIndex: 1, MaterialType: "PETG", ColorHint: null, EstimatedGrams: null),
            ],
        };

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"requiredMaterialType\":\"PLA\"", json);
        Assert.Contains("\"toolRequirements\":[", json);
        Assert.Contains("\"toolIndex\":0", json);
        Assert.Contains("\"materialType\":\"PLA\"", json);
        Assert.Contains("\"colorHint\":\"#FF0000\"", json);
        Assert.Contains("\"estimatedGrams\":12.5", json);
        Assert.Contains("\"toolIndex\":1", json);
        Assert.Contains("\"materialType\":\"PETG\"", json);
    }

    private static PrintJobManagementService CreateService(Mock<IPrintJobManagementRepository> repository)
    {
        return new PrintJobManagementService(
            repository.Object,
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: Mock.Of<IRetryService>(),
            printerStatusRefreshService: Mock.Of<IPrinterStatusRefreshService>(),
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            settingsService: null);
    }
}
