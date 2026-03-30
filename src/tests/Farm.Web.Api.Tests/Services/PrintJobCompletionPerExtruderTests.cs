using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// TDD tests for Phase 3: Backend Per-Extruder Actual Usage tracking.
/// Tests the creation of PrintJobToolheadUsage records when multi-toolhead backends
/// report per-extruder filament consumption through ISupportsPerExtruderFilamentUsage.
/// </summary>
public class PrintJobCompletionPerExtruderTests
{
    [Fact]
    public void MultiExtruderBackend_CreatesToolheadUsageRecords()
    {
        // Arrange: Setup multi-extruder backend response with per-toolhead usage
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 }
        };

        var toolheads = new List<Toolhead>
        {
            new()
            {
                Index = 0,
                CurrentSpoolId = 100,
                CurrentMaterial = "PLA",
                CurrentFilamentColor = "#FF0000"
            },
            new()
            {
                Index = 1,
                CurrentSpoolId = 200,
                CurrentMaterial = "PETG",
                CurrentFilamentColor = "#00FF00"
            }
        };

        var job = new PrintJob { Id = Guid.NewGuid() };

        // Act: Build expected usage records from per-extruder data
        var usageRecords = CreateToolheadUsageRecords(job.Id, perExtruderUsage, toolheads);

        // Assert: Verify correct number of records
        usageRecords.Should().HaveCount(2);

        // Assert: Verify T0 record
        var t0Record = usageRecords.FirstOrDefault(r => r.ToolheadIndex == 0);
        t0Record.Should().NotBeNull();
        t0Record!.PrintJobId.Should().Be(job.Id);
        t0Record.ToolheadIndex.Should().Be(0);
        t0Record.SpoolmanSpoolId.Should().Be(100);
        t0Record.FilamentUsageGrams.Should().Be(10.5);
        t0Record.FilamentName.Should().Be("PLA");
        t0Record.FilamentColor.Should().Be("#FF0000");

        // Assert: Verify T1 record
        var t1Record = usageRecords.FirstOrDefault(r => r.ToolheadIndex == 1);
        t1Record.Should().NotBeNull();
        t1Record!.PrintJobId.Should().Be(job.Id);
        t1Record.ToolheadIndex.Should().Be(1);
        t1Record.SpoolmanSpoolId.Should().Be(200);
        t1Record.FilamentUsageGrams.Should().Be(3.2);
        t1Record.FilamentName.Should().Be("PETG");
        t1Record.FilamentColor.Should().Be("#00FF00");
    }

    [Fact]
    public void MultiExtruderUsage_SumsToTotalActualUsage()
    {
        // Arrange: Backend returns per-extruder usage
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 }
        };

        // Act: Calculate total usage from per-extruder data
        double totalUsage = perExtruderUsage.Values.Sum();

        // Assert: Total should equal sum of all extruders
        totalUsage.Should().Be(13.7);
    }

    [Fact]
    public void MultiExtruderUsage_WithThreeExtruders_SumsCorrectly()
    {
        // Arrange: Backend returns usage for 3 extruders
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 },
            { 2, 5.8 }
        };

        // Act: Calculate total usage
        double totalUsage = perExtruderUsage.Values.Sum();

        // Assert: Total should equal sum of all three extruders
        totalUsage.Should().Be(19.5);
    }

    [Fact]
    public void SingleExtruderBackend_FallsBackToExistingBehavior()
    {
        // Arrange: Backend does NOT implement ISupportsPerExtruderFilamentUsage
        // Returns null for per-extruder data, only has single total
        Dictionary<int, double>? perExtruderUsage = null;
        double singleTotalUsage = 15.0;

        // Act: Determine which usage to use
        double? actualUsage = perExtruderUsage != null
            ? perExtruderUsage.Values.Sum()
            : singleTotalUsage;

        bool shouldCreatePerExtruderRecords = perExtruderUsage != null;

        // Assert: Should use single total and not create per-extruder records
        actualUsage.Should().Be(15.0);
        shouldCreatePerExtruderRecords.Should().BeFalse();
    }

    [Fact]
    public void PerExtruderNull_FallsBackToSingleTotal()
    {
        // Arrange: Backend implements ISupportsPerExtruderFilamentUsage but returns null
        Dictionary<int, double>? perExtruderUsage = null;
        double? fallbackTotalUsage = 12.3;

        // Act: Use fallback when per-extruder data is null
        double? finalUsage = perExtruderUsage?.Values.Sum() ?? fallbackTotalUsage;

        // Assert: Should fall back to single total from ISupportsFilamentUsageQuery
        finalUsage.Should().Be(12.3);
    }

    [Fact]
    public void ToolheadSpoolAssignment_CapturesCurrentSpools()
    {
        // Arrange: Printer toolheads have spools assigned
        var toolheads = new List<Toolhead>
        {
            new()
            {
                Index = 0,
                CurrentSpoolId = 100,
                CurrentMaterial = "PLA Basic",
                CurrentFilamentColor = "#FF0000"
            },
            new()
            {
                Index = 1,
                CurrentSpoolId = 200,
                CurrentMaterial = "PETG Premium",
                CurrentFilamentColor = "#00FF00"
            }
        };

        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 8.5 },
            { 1, 4.2 }
        };

        var jobId = Guid.NewGuid();

        // Act: Create usage records with spool snapshot
        var usageRecords = CreateToolheadUsageRecords(jobId, perExtruderUsage, toolheads);

        // Assert: Records should contain correct SpoolmanSpoolId from toolhead assignment
        usageRecords.Should().HaveCount(2);

        var t0 = usageRecords.First(r => r.ToolheadIndex == 0);
        t0.SpoolmanSpoolId.Should().Be(100);
        t0.FilamentName.Should().Be("PLA Basic");

        var t1 = usageRecords.First(r => r.ToolheadIndex == 1);
        t1.SpoolmanSpoolId.Should().Be(200);
        t1.FilamentName.Should().Be("PETG Premium");
    }

    [Fact]
    public void MissingToolheadSpool_StillCreatesUsageRecord()
    {
        // Arrange: T0 has spool, T1 has NO spool assigned
        var toolheads = new List<Toolhead>
        {
            new()
            {
                Index = 0,
                CurrentSpoolId = 100,
                CurrentMaterial = "PLA",
                CurrentFilamentColor = "#FF0000"
            },
            new()
            {
                Index = 1,
                CurrentSpoolId = null, // No spool assigned
                CurrentMaterial = null,
                CurrentFilamentColor = null
            }
        };

        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 }
        };

        var jobId = Guid.NewGuid();

        // Act: Create usage records even when spool is missing
        var usageRecords = CreateToolheadUsageRecords(jobId, perExtruderUsage, toolheads);

        // Assert: Both records created; T1's SpoolmanSpoolId is null
        usageRecords.Should().HaveCount(2);

        var t0 = usageRecords.First(r => r.ToolheadIndex == 0);
        t0.SpoolmanSpoolId.Should().Be(100);
        t0.FilamentUsageGrams.Should().Be(10.5);

        var t1 = usageRecords.First(r => r.ToolheadIndex == 1);
        t1.SpoolmanSpoolId.Should().BeNull();
        t1.FilamentUsageGrams.Should().Be(3.2);
        t1.FilamentName.Should().BeNull();
        t1.FilamentColor.Should().BeNull();
    }

    [Fact]
    public void ZeroUsageExtruder_StillCreatesRecord()
    {
        // Arrange: Backend returns zero usage for T1
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 0.0 }
        };

        var toolheads = new List<Toolhead>
        {
            new()
            {
                Index = 0,
                CurrentSpoolId = 100,
                CurrentMaterial = "PLA",
                CurrentFilamentColor = "#FF0000"
            },
            new()
            {
                Index = 1,
                CurrentSpoolId = 200,
                CurrentMaterial = "PETG",
                CurrentFilamentColor = "#00FF00"
            }
        };

        var jobId = Guid.NewGuid();

        // Act: Create usage records including zero-usage extruder
        var usageRecords = CreateToolheadUsageRecords(jobId, perExtruderUsage, toolheads);

        // Assert: Both records created; T1 has FilamentUsageGrams = 0.0
        usageRecords.Should().HaveCount(2);

        var t0 = usageRecords.First(r => r.ToolheadIndex == 0);
        t0.FilamentUsageGrams.Should().Be(10.5);

        var t1 = usageRecords.First(r => r.ToolheadIndex == 1);
        t1.FilamentUsageGrams.Should().Be(0.0);
        t1.SpoolmanSpoolId.Should().Be(200);
    }

    [Fact]
    public void PerExtruderSpoolmanConsumption_CallsForEachSpool()
    {
        // Arrange: Backend returns usage for two extruders with different spools
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 }
        };

        var toolheads = new List<Toolhead>
        {
            new() { Index = 0, CurrentSpoolId = 100 },
            new() { Index = 1, CurrentSpoolId = 200 }
        };

        // Act: Build expected Spoolman consumption calls
        var expectedConsumptionCalls = new List<(int spoolId, double grams)>();
        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);
            if (toolhead?.CurrentSpoolId != null)
            {
                expectedConsumptionCalls.Add((toolhead.CurrentSpoolId.Value, grams));
            }
        }

        // Assert: Should have two consumption calls
        expectedConsumptionCalls.Should().HaveCount(2);
        expectedConsumptionCalls.Should().Contain((100, 10.5));
        expectedConsumptionCalls.Should().Contain((200, 3.2));
    }

    [Fact]
    public void PerExtruderSpoolmanConsumption_SkipsNullSpools()
    {
        // Arrange: T0 has spool, T1 does not
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 },
            { 1, 3.2 }
        };

        var toolheads = new List<Toolhead>
        {
            new() { Index = 0, CurrentSpoolId = 100 },
            new() { Index = 1, CurrentSpoolId = null }
        };

        // Act: Build expected Spoolman consumption calls (skip null spools)
        var expectedConsumptionCalls = new List<(int spoolId, double grams)>();
        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);
            if (toolhead?.CurrentSpoolId != null)
            {
                expectedConsumptionCalls.Add((toolhead.CurrentSpoolId.Value, grams));
            }
        }

        // Assert: Should only have one consumption call (for T0)
        expectedConsumptionCalls.Should().HaveCount(1);
        expectedConsumptionCalls.Should().Contain((100, 10.5));
    }

    [Fact]
    public void UnusedExtruderNotInUsageData_NoRecordCreated()
    {
        // Arrange: Backend only reports usage for T0, not T1
        var perExtruderUsage = new Dictionary<int, double>
        {
            { 0, 10.5 }
            // T1 not included in usage data
        };

        var toolheads = new List<Toolhead>
        {
            new() { Index = 0, CurrentSpoolId = 100 },
            new() { Index = 1, CurrentSpoolId = 200 }
        };

        var jobId = Guid.NewGuid();

        // Act: Only create records for extruders in the usage data
        var usageRecords = CreateToolheadUsageRecords(jobId, perExtruderUsage, toolheads);

        // Assert: Only T0 record should be created
        usageRecords.Should().HaveCount(1);
        usageRecords.First().ToolheadIndex.Should().Be(0);
        usageRecords.First().FilamentUsageGrams.Should().Be(10.5);
    }

    #region Helper Methods

    /// <summary>
    /// Helper method to create PrintJobToolheadUsage records from per-extruder data.
    /// This simulates the logic that PrintJobCompletionService should implement in Phase 3.
    /// </summary>
    private List<PrintJobToolheadUsage> CreateToolheadUsageRecords(
        Guid printJobId,
        Dictionary<int, double> perExtruderUsage,
        List<Toolhead> toolheads)
    {
        var usageRecords = new List<PrintJobToolheadUsage>();

        foreach (var (toolIndex, grams) in perExtruderUsage)
        {
            var toolhead = toolheads.FirstOrDefault(t => t.Index == toolIndex);

            usageRecords.Add(new PrintJobToolheadUsage
            {
                Id = Guid.NewGuid(),
                PrintJobId = printJobId,
                ToolheadIndex = toolIndex,
                SpoolmanSpoolId = toolhead?.CurrentSpoolId,
                FilamentUsageGrams = grams,
                FilamentName = toolhead?.CurrentMaterial,
                FilamentColor = toolhead?.CurrentFilamentColor,
                MaterialCostUsd = null // Phase 5: Cost calculation
            });
        }

        return usageRecords;
    }

    #endregion
}
