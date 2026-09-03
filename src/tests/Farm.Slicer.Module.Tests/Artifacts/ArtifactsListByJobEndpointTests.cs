using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Tests.Artifacts;

public sealed class ArtifactsListByJobEndpointTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Artifacts by job identifies exactly the authoritative primary G-code")]
    public async Task ListByJobAsync_WithCompletedPrimary_ReturnsTypedCamelCasePrimaryFlag()
    {
        Guid ownerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        (IServiceScope scope, IArtifactsService service, Artifact primary, Artifact secondary) seeded =
            await SeedArtifactsAsync(ownerId, jobId);
        using IServiceScope scope = seeded.scope;
        StubSliceJobRepository jobs = CreateJobRepository(
            ownerId,
            jobId,
            $"/api/artifacts/{seeded.primary.Id}");
        ArtifactsController controller = CreateController(seeded.service, jobs, ownerId);

        IActionResult result = await controller.ListByJobAsync(jobId, CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        List<ArtifactListItemDto> items = ok.Value.Should()
            .BeOfType<List<ArtifactListItemDto>>().Subject;
        _ = items.Should().ContainSingle(item => item.IsPrimary)
            .Which.Id.Should().Be(seeded.primary.Id);
        _ = items.Single(item => item.Id == seeded.secondary.Id).IsPrimary.Should().BeFalse();

        using JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        JsonElement primaryJson = json.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == seeded.primary.Id);
        _ = primaryJson.GetProperty("isPrimary").ValueKind.Should().Be(JsonValueKind.True);
        _ = primaryJson.TryGetProperty("IsPrimary", out _).Should().BeFalse();
    }

    [Fact(DisplayName = "Artifacts by job does not infer a primary from list order")]
    public async Task ListByJobAsync_WithMultipleGcodesAndNoValidPrimary_ReturnsAllFalse()
    {
        Guid ownerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        (IServiceScope scope, IArtifactsService service, Artifact primary, Artifact secondary) seeded =
            await SeedArtifactsAsync(ownerId, jobId);
        using IServiceScope scope = seeded.scope;
        StubSliceJobRepository jobs = CreateJobRepository(ownerId, jobId, resultFileUrl: null);
        ArtifactsController controller = CreateController(seeded.service, jobs, ownerId);

        IActionResult result = await controller.ListByJobAsync(jobId, CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        List<ArtifactListItemDto> items = ok.Value.Should()
            .BeOfType<List<ArtifactListItemDto>>().Subject;
        _ = items.Should().HaveCount(2);
        _ = items.Should().OnlyContain(item => !item.IsPrimary);
    }

    private async Task<(IServiceScope Scope, IArtifactsService Service, Artifact Primary, Artifact Secondary)>
        SeedArtifactsAsync(
            Guid ownerId,
            Guid jobId)
    {
        IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = db.SliceJobs.Add(CreateJob(ownerId, jobId, resultFileUrl: null));
        _ = await db.SaveChangesAsync();

        Artifact primary = await service.UploadAsync(
            new TestFormFile(Encoding.UTF8.GetBytes("G28"), "primary.gcode"),
            jobId,
            null,
            SlicerArtifactKinds.Gcode,
            CancellationToken.None);
        Artifact secondary = await service.UploadAsync(
            new TestFormFile(Encoding.UTF8.GetBytes("G29"), "secondary.gcode"),
            jobId,
            null,
            SlicerArtifactKinds.Gcode,
            CancellationToken.None);
        return (scope, service, primary, secondary);
    }

    private static StubSliceJobRepository CreateJobRepository(
        Guid ownerId,
        Guid jobId,
        string? resultFileUrl)
    {
        StubSliceJobRepository repository = new();
        repository.Jobs.Add(CreateJob(ownerId, jobId, resultFileUrl));
        return repository;
    }

    private static SliceJob CreateJob(Guid ownerId, Guid jobId, string? resultFileUrl) => new()
    {
        Id = jobId,
        UserId = ownerId,
        Status = SliceJobStatus.Completed,
        ResultFileUrl = resultFileUrl,
        QueuedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        SlicerEngine = 0,
        ModelFileName = "model.stl",
        ModelFileUrl = "http://example/model.stl",
    };

    private static ArtifactsController CreateController(
        IArtifactsService service,
        StubSliceJobRepository jobs,
        Guid ownerId)
    {
        ArtifactsController controller = new(
            service,
            jobs,
            Options.Create(new SlicerArtifactStorageSettings()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, ownerId.ToString()),
                        new Claim(ClaimTypes.Name, "artifact-owner"),
                    ],
                    "TestAuth")),
            },
        };
        return controller;
    }

    private sealed class TestFormFile(byte[] bytes, string fileName) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => bytes.Length;
        public string Name => "file";
        public string FileName => fileName;
        public void CopyTo(Stream target) => target.Write(bytes);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            target.WriteAsync(bytes, cancellationToken).AsTask();
        public Stream OpenReadStream() => new MemoryStream(bytes, writable: false);
    }
}
