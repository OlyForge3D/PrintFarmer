using System;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests that the completion endpoint refuses an out-of-domain <c>layoutDegradation</c> value
/// instead of persisting/round-tripping it (issue #1800 review finding). Because
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> accepts raw numeric tokens
/// on input by default, a malformed or malicious worker payload could otherwise bind
/// <c>LayoutDegradation</c> to an integer that names no real
/// <see cref="LayoutDegradationReason"/> member.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobCompletionLayoutDegradationValidationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Completion endpoint rejects an out-of-domain layoutDegradation value")]
    public async Task CompleteAsync_OutOfDomainLayoutDegradation_ReturnsBadRequest()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        ISliceJobEventService evtSvc = scope.ServiceProvider.GetRequiredService<ISliceJobEventService>();
        ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        ILogger<SliceJobController> logger = loggerFactory.CreateLogger<SliceJobController>();
        IRateLimitService rateLimit = scope.ServiceProvider.GetRequiredService<IRateLimitService>();
        SliceJobMetrics metrics = new SliceJobMetrics();
        IWorkerAuthService workerAuth = scope.ServiceProvider.GetRequiredService<IWorkerAuthService>();
        IWorkerRepository workerRepository = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
        Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry slicerRegistry =
            scope.ServiceProvider.GetService<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>()
            ?? new Moq.Mock<Farm.Slicer.Module.Contracts.Libraries.ISlicerRegistry>().Object;
        Guid serviceId = Guid.NewGuid();
        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "Test Worker",
            EndpointUrl = "http://localhost:8080",
            CapabilitiesJson = "[\"orcaslicer\",\"orcaslicer-upstream\"]",
            Status = WorkerStatus.Online,
            ApiKey = "test-worker-key",
            TotalSlots = 1,
            LastHeartbeat = DateTime.UtcNow
        };
        await workerRepository.AddAsync(worker);
        await workerRepository.SaveChangesAsync();
        DefaultHttpContext httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Worker-Key"] = "test-worker-key";
        httpContext.Request.Headers["X-Worker-Id"] = serviceId.ToString();
        Guid leaseToken = Guid.NewGuid();
        httpContext.Request.Headers[WorkerLeaseHeaders.LeaseToken] = leaseToken.ToString();
        httpContext.Request.Headers[WorkerLeaseHeaders.LeaseFence] = "1";
        SliceJobController controller = new SliceJobController(
            jobRepo,
            evtSvc,
            logger,
            artifactsService,
            rateLimit,
            metrics,
            workerAuth,
            workerRepository,
            slicerRegistry)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Create job in Processing state under an active, fenced lease.
        Guid claimToken = Guid.NewGuid();
        httpContext.Request.Headers[WorkerClaimHeaders.ClaimToken] = claimToken.ToString();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-3),
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl",
            WorkerId = worker.Id,
            ClaimToken = claimToken,
            ClaimedAt = DateTime.UtcNow.AddMinutes(-2),
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = leaseToken,
            LeaseFence = 1
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        byte[] bytes = Encoding.UTF8.GetBytes("; gcode content");
        TestFormFile formFile = new TestFormFile(bytes, "primary.gcode", "application/gcode");
        Artifact primary = (await artifactsService.UploadForActiveLeaseAsync(
            formFile,
            job.Id,
            worker.Id,
            claimToken,
            "gcode",
            default))!;

        // Simulate a malformed/malicious worker payload where the enum value binds to an integer
        // that names no real LayoutDegradationReason member (e.g. via a raw numeric JSON token,
        // which JsonStringEnumConverter accepts by default).
        CompleteSliceJobRequest request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = primary.Id,
            LayoutDegradation = (LayoutDegradationReason)999,
        };

        IActionResult result = await controller.CompleteAsync(job.Id, request, claimToken, default);

        ObjectResult? objectResult = result as ObjectResult;
        _ = objectResult.Should().NotBeNull();
        _ = objectResult!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        // The job must remain untouched: no completion should have been applied for the
        // out-of-domain value, so it stays in its original Processing state.
        SliceJob? unchanged = await jobRepo.GetByIdAsync(job.Id, default);
        _ = unchanged.Should().NotBeNull();
        _ = unchanged!.Status.Should().Be(SliceJobStatus.Processing);
        _ = unchanged.LayoutDegradationReason.Should().BeNull();
    }

    private sealed class TestFormFile(byte[] data, string fileName, string contentType) : IFormFile
    {
        private readonly byte[] _data = data;

        public string ContentType { get; } = contentType;
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; } = data.Length;
        public string Name { get; } = "file";
        public string FileName { get; } = fileName;
        public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    }
}
