using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.HostedServices;

/// <summary>
/// Tests for the reconciliation that makes a corrected profile seed actually reach an existing
/// deployment (#1779).
/// </summary>
/// <remarks>
/// This is the piece whose absence caused the issue to be closed twice while still reproducing:
/// the seed fix was correct and deployed both times, but nothing ever ran it, so the database kept
/// its incomplete profile set. These tests pin the behaviour that closes that gap — the seed runs
/// automatically once a worker is available, and is skipped rather than erroring when it cannot.
/// </remarks>
public class SystemProfileReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileAsync_WorkerAvailable_InvokesSeedSoDeploymentConvergesWithoutAdminAction()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { imported = 8 });

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _);

        await svc.ReconcileAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_NoWorkerRegistered_SkipsSeedingWithoutThrowing()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);

        SystemProfileReconciliationService svc = CreateService(profiles, new List<SlicerService>(), out _);

        await svc.ReconcileAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Reconciliation must never take the host down; a seeding failure leaves the admin endpoint as
    /// a manual fallback and the next start retries.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SeedThrows_DoesNotPropagate()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("worker unreachable"));

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _);

        // ReconcileAsync surfaces the failure; StartAsync must swallow it.
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => svc.ReconcileAsync(CancellationToken.None));

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReconcileAsync_DisabledByConfiguration_DoesNotRunOnStart()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        SystemProfileReconciliationService svc = CreateService(
            profiles,
            WorkerOnline(),
            out _,
            new Dictionary<string, string?> { ["SystemProfileReconciliation:Enabled"] = "false" });

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static List<SlicerService> WorkerOnline() =>
        new()
        {
            new()
            {
                Name = "orca",
                SlicerType = 1,
                Host = "http://worker",
                Status = "Online",
                LastSeen = DateTime.UtcNow,
                Version = "2.4.2",
                CapabilitiesJson = $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
            }
        };

    private static SystemProfileReconciliationService CreateService(
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles,
        List<SlicerService> workers,
        out ServiceProvider provider,
        Dictionary<string, string?>? config = null)
    {
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicers = new(MockBehavior.Loose);
        _ = slicers.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(workers);

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Loose);
        _ = machineRepo.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MachineProfile>());

        ServiceCollection services = new();
        _ = services.AddSingleton(profiles.Object);
        _ = services.AddSingleton(slicers.Object);
        _ = services.AddSingleton(machineRepo.Object);
        provider = services.BuildServiceProvider();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>
            {
                // Keep the test fast: no startup delay, no worker polling window.
                ["SystemProfileReconciliation:StartupDelaySeconds"] = "0",
                ["SystemProfileReconciliation:WorkerWaitMinutes"] = "0",
                ["SystemProfileReconciliation:WorkerPollSeconds"] = "0"
            })
            .Build();

        return new SystemProfileReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemProfileReconciliationService>.Instance,
            configuration);
    }
}
