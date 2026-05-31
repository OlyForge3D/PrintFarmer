using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

/// <summary>
/// Unit tests for GET /api/artifacts/{id} (binary download) ownership enforcement.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ArtifactsDownloadEndpointTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "GetArtifact returns file for artifact owner")]
    public async Task GetArtifact_ReturnsFile_ForOwner()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        Guid ownerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        db.Set<SliceJob>().Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });
        await db.SaveChangesAsync();

        byte[] data = Encoding.UTF8.GetBytes("G1 X0 Y0 ; owner download test");
        TestFormFileHelper file = new TestFormFileHelper(data, "owner.gcode", "application/octet-stream");
        Artifact artifact = await svc.UploadAsync(file, jobId, null, "gcode", default);

        StubSliceJobRepository jobRepo = new StubSliceJobRepository();
        jobRepo.Jobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });

        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings());
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        controller.ControllerContext = BuildControllerContext(ownerId.ToString(), isAdmin: false);

        IActionResult result = await controller.GetAsync(artifact.Id, default);

        _ = result.Should().BeOfType<PhysicalFileResult>();
    }

    [Fact(DisplayName = "GetArtifact returns 404 for non-owner (prevents enumeration)")]
    public async Task GetArtifact_Returns404_ForNonOwner()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        Guid ownerId = Guid.NewGuid();
        Guid intruderId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        db.Set<SliceJob>().Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });
        await db.SaveChangesAsync();

        byte[] data = Encoding.UTF8.GetBytes("G1 X0 Y0 ; non-owner test");
        TestFormFileHelper file = new TestFormFileHelper(data, "secret.gcode", "application/octet-stream");
        Artifact artifact = await svc.UploadAsync(file, jobId, null, "gcode", default);

        StubSliceJobRepository jobRepo = new StubSliceJobRepository();
        jobRepo.Jobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });

        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings());
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        controller.ControllerContext = BuildControllerContext(intruderId.ToString(), isAdmin: false);

        IActionResult result = await controller.GetAsync(artifact.Id, default);

        _ = result.Should().BeOfType<NotFoundResult>();
    }

    [Fact(DisplayName = "GetArtifact returns file for farm_admin")]
    public async Task GetArtifact_ReturnsFile_ForAdmin()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        Guid ownerId = Guid.NewGuid();
        Guid adminId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        db.Set<SliceJob>().Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });
        await db.SaveChangesAsync();

        byte[] data = Encoding.UTF8.GetBytes("G1 X0 Y0 ; admin download test");
        TestFormFileHelper file = new TestFormFileHelper(data, "admin.gcode", "application/octet-stream");
        Artifact artifact = await svc.UploadAsync(file, jobId, null, "gcode", default);

        StubSliceJobRepository jobRepo = new StubSliceJobRepository();
        jobRepo.Jobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = ownerId,
            Status = SliceJobStatus.Completed,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        });

        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings());
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        controller.ControllerContext = BuildControllerContext(adminId.ToString(), isAdmin: true);

        IActionResult result = await controller.GetAsync(artifact.Id, default);

        _ = result.Should().BeOfType<PhysicalFileResult>();
    }

    [Fact(DisplayName = "GetArtifact returns 404 for unknown artifact ID")]
    public async Task GetArtifact_Returns404_ForUnknownId()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        StubSliceJobRepository jobRepo = new StubSliceJobRepository();
        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings());
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        controller.ControllerContext = BuildControllerContext(Guid.NewGuid().ToString(), isAdmin: false);

        IActionResult result = await controller.GetAsync(Guid.NewGuid(), default);

        _ = result.Should().BeOfType<NotFoundResult>();
    }

    private static ControllerContext BuildControllerContext(string userId, bool isAdmin)
    {
        List<Claim> claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, "testuser"),
        };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "farm_admin"));
        }

        ClaimsPrincipal user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private sealed class TestFormFileHelper(byte[] data, string fileName, string contentType) : IFormFile
    {
        private readonly byte[] _data = data;

        public string ContentType { get; } = contentType;
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; } = data.Length;
        public string Name { get; } = "file";
        public string FileName { get; } = fileName;
        public void CopyTo(System.IO.Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(System.IO.Stream target, System.Threading.CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public System.IO.Stream OpenReadStream() => new System.IO.MemoryStream(_data, writable: false);
    }
}
