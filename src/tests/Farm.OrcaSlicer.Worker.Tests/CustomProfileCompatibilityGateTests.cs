using Farm.OrcaSlicer.Worker.Controllers;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Verifies that rendered custom names satisfy both profile discovery and execution gates.
/// </summary>
public sealed class CustomProfileCompatibilityGateTests
{
    [Fact]
    public async Task ExactCustomMachineName_SatisfiesDiscoveryAndExecutionGates()
    {
        const string machineName = "Farm Test 0.6 nozzle";
        var process = new ProcessProfileDto
        {
            Name = "0.30mm Draft @Farm Test",
            CompatiblePrinters = [machineName],
            CompatiblePrintersCondition = string.Empty,
            Settings = new Dictionary<string, object>
            {
                ["name"] = "0.30mm Draft @Farm Test",
                ["compatible_printers"] = new List<string> { machineName },
                ["compatible_printers_condition"] = string.Empty
            }
        };
        var service = new StubProfilesService(process);
        var controller = new ProfilesController(
            service,
            NullLogger<ProfilesController>.Instance);

        ActionResult<List<ProcessProfileDto>> discovery =
            await controller.GetProcessProfilesForMachinesAsync(
                new MachineNamesRequest { MachineNames = [machineName] },
                CancellationToken.None);

        OkObjectResult ok = discovery.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should()
            .BeAssignableTo<IReadOnlyList<ProcessProfileDto>>()
            .Which.Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(process);

        var machine = new MachineProfileDto
        {
            Name = machineName,
            Settings = new Dictionary<string, object>
            {
                ["name"] = machineName,
                ["from"] = "system"
            }
        };
        OrcaSlicingPipelineService.ProcessCompatibilityResolution execution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(process, machine);

        execution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.AlreadyDeclared);
        execution.Settings["compatible_printers"]
            .Should()
            .BeOfType<List<string>>()
            .Which.Should()
            .Equal(machineName);
    }

    private sealed class StubProfilesService(ProcessProfileDto process) : ISlicerProfilesService
    {
        public Task<IList<MachineModelProfileDto>> ListAvailableMachineModelProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineModelProfileDto>>([]);

        public Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<MachineProfileDto>>([]);

        public Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<FilamentProfileDto>>([]);

        public Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IList<ProcessProfileDto>>([process]);
    }
}
