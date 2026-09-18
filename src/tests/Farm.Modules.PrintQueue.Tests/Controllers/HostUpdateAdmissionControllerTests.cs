// <copyright file="HostUpdateAdmissionControllerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.PrintQueue.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Controllers;

/// <summary>
/// Verifies host-update admission gates at physical queue/dispatch entry points.
/// </summary>
public sealed class HostUpdateAdmissionControllerTests
{
    private readonly Mock<IJobQueueService> _queueService = new();
    private readonly Mock<IPrintJobManagementService> _printJobs = new();
    private readonly Mock<IPrintJobCompletionService> _completion = new();
    private readonly Mock<IJobDispatchService> _dispatch = new();
    private readonly Mock<IBatchDispatchService> _batchDispatch = new();
    private readonly Mock<IBedClearAcknowledgementService> _bedClear = new();
    private readonly Mock<IPrinterStatusCacheReader> _printerStatus = new();
    private readonly Mock<IPrintFarmerTelemetryService> _telemetry = new();
    private readonly Mock<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService> _partHarvest = new();
    private readonly Mock<IOperatorFeatureGate> _featureGate = new();

    [Fact]
    public async Task QueueJobAsync_AdmissionClosed_ReturnsConflictWithoutEnqueueing()
    {
        JobQueueController controller = CreateJobQueueController();

        ActionResult<JobQueuePrintJobDto> result = await controller.QueueJobAsync(new QueuePrintJobDto { GcodeFileId = Guid.NewGuid() });

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        AssertAdmissionClosed(conflict);
        _printJobs.Verify(
            service => service.EnqueueJobAsync(
                It.IsAny<EnqueueQueueJobRequest>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchJobAsync_AdmissionClosed_ReturnsConflictWithoutDispatching()
    {
        JobQueueController controller = CreateJobQueueController();

        IActionResult result = await controller.DispatchJobAsync(Guid.NewGuid());

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        AssertAdmissionClosed(conflict);
        _printJobs.Verify(
            service => service.DispatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchToAsync_AdmissionClosed_ReturnsConflictWithoutDispatching()
    {
        JobQueueController controller = CreateJobQueueController();

        IActionResult result = await controller.DispatchToAsync(
            Guid.NewGuid(),
            new DispatchJobDto { PrinterId = Guid.NewGuid() });

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        AssertAdmissionClosed(conflict);
        _dispatch.Verify(
            service => service.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BatchDispatchAsync_AdmissionClosed_ReturnsConflictWithoutDispatching()
    {
        JobQueueController controller = CreateJobQueueController();

        IActionResult result = await controller.BatchDispatchAsync(
            new BatchDispatchRequest { DispatchAll = true },
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        AssertAdmissionClosed(conflict);
        _batchDispatch.Verify(
            service => service.BatchDispatchAsync(
                It.IsAny<BatchDispatchRequest>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkReadyAsync_AdmissionClosed_ReturnsConflictWithoutDispatching()
    {
        var autoDispatch = new Mock<IAutoDispatchService>();
        var controller = new AutoDispatchController(
            autoDispatch.Object,
            Mock.Of<ILogger<AutoDispatchController>>(),
            hostUpdateAdmissionGate: new ClosedAdmissionGate())
        {
            ControllerContext = CreateControllerContext(),
        };

        ActionResult<AutoDispatchReadyResult> result = await controller.MarkReadyAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        AssertAdmissionClosed(conflict);
        autoDispatch.Verify(
            service => service.MarkReadyAsync(
                It.IsAny<Guid>(),
                It.IsAny<byte[]>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<byte[]?>(),
                It.IsAny<byte[]?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private JobQueueController CreateJobQueueController() =>
        new(
            _queueService.Object,
            _printJobs.Object,
            _completion.Object,
            _dispatch.Object,
            _batchDispatch.Object,
            _bedClear.Object,
            _printerStatus.Object,
            _telemetry.Object,
            _partHarvest.Object,
            _featureGate.Object,
            Mock.Of<ILogger<JobQueueController>>(),
            hostUpdateAdmissionGate: new ClosedAdmissionGate())
        {
            ControllerContext = CreateControllerContext(),
        };

    private static ControllerContext CreateControllerContext()
    {
        var userId = Guid.NewGuid().ToString();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "TestAuth");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private static void AssertAdmissionClosed(ConflictObjectResult conflict)
    {
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("host_update_admission_closed", conflict.Value?.ToString(), StringComparison.Ordinal);
    }

    private sealed class ClosedAdmissionGate : IHostUpdateAdmissionGate
    {
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> IsClosedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
