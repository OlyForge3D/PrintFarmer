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
    /// Reconciliation must never take the host down. .NET's default
    /// <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, so an escaping exception here
    /// would turn an incomplete profile catalog — a cosmetic gap — into an outage. A seed that keeps
    /// failing is retried to the deadline and then logged, not rethrown.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SeedAlwaysThrows_IsRetriedThenSwallowed()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("worker unreachable"));

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _);

        // Gives up at the deadline rather than surfacing the failure.
        await svc.ReconcileAsync(CancellationToken.None);

        await svc.StartAsync(CancellationToken.None);
#pragma warning disable VSTHRD003 // ExecuteTask is BackgroundService's own framework-owned task started by StartAsync; awaiting it here is the standard test pattern for driving a hosted service to completion.
        await svc.ExecuteTask!;
#pragma warning restore VSTHRD003
        await svc.StopAsync(CancellationToken.None);

        Assert.True(svc.ExecuteTask.IsCompletedSuccessfully);
        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Startup must not wait on reconciliation. <see cref="Microsoft.Extensions.Hosting.BackgroundService.StartAsync"/> returns
    /// as soon as the implementation hits its first await, so readiness is never gated on a
    /// catalog-wide seed that talks to a worker over the network.
    /// </summary>
    [Fact]
    public async Task StartAsync_DoesNotBlockOnReconciliation()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
#pragma warning disable VSTHRD003 // gate is a TaskCompletionSource this test controls to hold the mocked reconciliation open; not a foreign/UI-thread task.
                await gate.Task;
#pragma warning restore VSTHRD003
                return new { imported = 0 };
            });

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _);

        await svc.StartAsync(CancellationToken.None);

        // Host startup completed while the seed is still parked inside the gate.
        Assert.False(svc.ExecuteTask!.IsCompleted);

        gate.SetResult();
        await svc.ExecuteTask;
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

    /// <summary>
    /// A registration left behind by a removed worker must not satisfy the wait. Accepting it would
    /// burn the single attempt against a worker that is not there and leave the deployment
    /// unreconciled until the next restart — the failure mode this service exists to remove.
    /// </summary>
    [Theory]
    [InlineData("stale-heartbeat")]
    [InlineData("unsupported-version")]
    [InlineData("no-attestation")]
    [InlineData("offline")]
    public async Task ReconcileAsync_OnlyIneligibleWorkerRegistered_DoesNotSeed(string defect)
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        SystemProfileReconciliationService svc = CreateService(profiles, new List<SlicerService> { Ineligible(defect) }, out _);

        await svc.ReconcileAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The realistic rolling-deploy shape: a stale row is present at first and the real worker
    /// registers moments later. Reconciliation must keep polling and then seed.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_StaleWorkerThenHealthyWorker_EventuallySeeds()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { imported = 8, errors = 0 });

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicers = new(MockBehavior.Loose);
        int call = 0;
        _ = slicers.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++call == 1
                ? new List<SlicerService> { Ineligible("stale-heartbeat") }
                : WorkerOnline());

        SystemProfileReconciliationService svc = CreateService(profiles, slicers, out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A worker that is up but still preloading its catalog fails the first fetch. Reconciliation
    /// must retry within its window rather than giving up until the next restart.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_FirstSeedAttemptThrows_RetriesUntilItSucceeds()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        int attempts = 0;
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempts == 1
                ? throw new HttpRequestException("worker still preloading profiles")
                : Task.FromResult<object>(new { imported = 8, errors = 0 }));

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// A seed that reports failed rows has NOT produced a complete catalog, even if the row count
    /// happens not to move. It must be retried rather than logged as complete.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_SeedReportsErrors_RetriesRatherThanReportingComplete()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        int attempts = 0;
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 1
                ? new { imported = 0, errors = 3 }
                : new { imported = 8, errors = 0 });

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// A transient registry/database failure during worker discovery must be retried like any other
    /// failure. If it escaped the retry loop, reconciliation would be deferred to the next restart —
    /// the exact failure mode this service exists to remove.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_WorkerDiscoveryThrowsTransiently_IsRetriedNotAbandoned()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new { imported = 8, errors = 0 });

        Mock<Farm.Slicer.Module.Services.ISlicersService> slicers = new(MockBehavior.Loose);
        int call = 0;
        _ = slicers.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ++call == 1
                ? throw new InvalidOperationException("registry temporarily unavailable")
                : Task.FromResult<IReadOnlyList<SlicerService>>(WorkerOnline()));

        SystemProfileReconciliationService svc = CreateService(profiles, slicers, out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        profiles.Verify(
            p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A worker that answers 200 with an empty hierarchy (up, but not finished loading its bundles)
    /// must be retried, not accepted as a complete catalog — this is Hicks's and Vasquez's
    /// empty-then-populated case end to end.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_WorkerReturnsEmptyCatalogThenPopulated_RetriesUntilPopulated()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        int attempts = 0;
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 1
                ? new { imported = 0, skipped = 0, errors = 1, message = "No profiles available from worker or invalid hierarchy structure" }
                : new { imported = 8, skipped = 0, errors = 0, message = "ok" });

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// An unrecognised result shape is not evidence of success. Reconciliation must fail closed and
    /// keep retrying rather than infer "zero errors" from a missing member.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_SeedResultHasNoErrorCount_FailsClosedAndRetries()
    {
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles = new(MockBehavior.Loose);
        int attempts = 0;
        _ = profiles.Setup(p => p.SeedSystemProfilesFromWorkerAsync(It.IsAny<HttpClient>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 1
                ? new { imported = 0 }
                : (object)new { imported = 8, errors = 0 });

        SystemProfileReconciliationService svc = CreateService(profiles, WorkerOnline(), out _, LongWindow());

        await svc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    private static Dictionary<string, string?> LongWindow() => new()
    {
        ["SystemProfileReconciliation:StartupDelaySeconds"] = "0",
        ["SystemProfileReconciliation:WorkerWaitMinutes"] = "5",
        ["SystemProfileReconciliation:WorkerPollSeconds"] = "0"
    };

    private static SlicerService Ineligible(string defect) => new()
    {
        Name = "orca",
        SlicerType = 1,
        Host = "http://worker",
        Status = defect == "offline" ? "Offline" : "Online",
        LastSeen = defect == "stale-heartbeat" ? DateTime.UtcNow.AddHours(-4) : DateTime.UtcNow,
        Version = defect == "unsupported-version" ? "1.0.0" : "2.4.2",
        CapabilitiesJson = defect == "no-attestation"
            ? "[\"gcode-gen\"]"
            : $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
    };

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
        return CreateService(profiles, slicers, out provider, config);
    }

    private static SystemProfileReconciliationService CreateService(
        Mock<Farm.Slicer.Module.Services.IProfilesService> profiles,
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicers,
        out ServiceProvider provider,
        Dictionary<string, string?>? config = null)
    {
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
