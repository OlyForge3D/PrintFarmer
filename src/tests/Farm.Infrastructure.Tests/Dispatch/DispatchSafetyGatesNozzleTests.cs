// <copyright file="DispatchSafetyGatesNozzleTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// Stored per-printer nozzle diameter is the only evidence accepted by <see cref="DispatchSafetyGates.EvaluateHardware"/>.
/// </summary>
public sealed class DispatchSafetyGatesNozzleTests
{
    [Fact]
    public void StandardJob_AcceptsStoredToolheadDiameter()
    {
        PrintJob job = CreateJob(JobKind.Standard, 0.4m);
        Printer printer = CreatePrinter(explicitDiameter: 0.4, modelDiameter: null);

        DispatchSafetyGates.EvaluateHardware(job, printer).Should().BeNull();
    }

    [Fact]
    public void StandardJob_IgnoresNozzleModelDiameter_WhenStoredDiameterMissing()
    {
        PrintJob job = CreateJob(JobKind.Standard, 0.4m);
        Printer printer = CreatePrinter(explicitDiameter: null, modelDiameter: 0.4);

        DispatchSafetyGates.EvaluateHardware(job, printer)!.ErrorCode.Should().Be("nozzle_unknown");
    }

    [Fact]
    public void StandardJob_StoredDiameterWinsOverNozzleModel()
    {
        PrintJob job = CreateJob(JobKind.Standard, 0.4m);
        Printer printer = CreatePrinter(explicitDiameter: 0.6, modelDiameter: 0.4);

        DispatchSafetyGates.EvaluateHardware(job, printer)!.ErrorCode.Should().Be("nozzle_mismatch");
    }

    [Fact]
    public void CalibrationJob_IgnoresNozzleModelDiameter()
    {
        PrintJob job = CreateJob(JobKind.FilamentCalibration, 0.4m);
        Printer printer = CreatePrinter(explicitDiameter: null, modelDiameter: 0.4);

        DispatchSafetyGates.EvaluateHardware(job, printer)!.ErrorCode.Should().Be("nozzle_unknown");
    }

    private static PrintJob CreateJob(JobKind kind, decimal requiredNozzle) => new()
    {
        Id = Guid.NewGuid(),
        JobKind = kind,
        RequiredNozzleDiameter = requiredNozzle,
    };

    private static Printer CreatePrinter(double? explicitDiameter, double? modelDiameter)
    {
        Printer printer = new() { Id = Guid.NewGuid() };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = explicitDiameter,
            NozzleModel = modelDiameter is { } diameter
                ? new NozzleModelDefinition { Id = Guid.NewGuid(), Diameter = diameter }
                : null,
        });
        return printer;
    }
}
