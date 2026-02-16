using System;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Artifacts;
using Farm.Slicer.Module.Tests.Slicing;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts
{
    [Collection(IntegrationTestCollection.Name)]
    public class ArtifactsControllerKindValidationTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory = factory;

        [Fact(DisplayName = "Unsupported kind returns 400 with allowedKinds (controller direct)")]
        public async Task Unsupported_Kind_Returns_BadRequest_With_Allowed_List()
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
            IOptions<ArtifactStorageSettings> opts = Options.Create(new ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail" });
            JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
            ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
            TestFormFile file = new TestFormFile(Encoding.UTF8.GetBytes("dummy"), "a.txt", "text/plain");
            IActionResult result = await controller.UploadAsync(Guid.NewGuid(), "invalid-kind", null, file, default);
            _ = result.Should().BeOfType<BadRequestObjectResult>();
            BadRequestObjectResult bad = (BadRequestObjectResult)result;
            _ = bad.Value!.ToString()!.Should().Contain("allowedKinds");
        }

        private sealed class TestFormFile(byte[] d, string name, string ct) : IFormFile
        {
            private readonly byte[] _data = d;

            public string ContentType { get; } = ct;
            public string ContentDisposition { get; set; } = string.Empty;
            public IHeaderDictionary Headers { get; } = new HeaderDictionary();
            public long Length { get; } = d.Length;
            public string Name { get; } = "file";
            public string FileName { get; } = name;
            public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
            public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
            {
                target.Write(_data, 0, _data.Length);
                return Task.CompletedTask;
            }
            public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
        }
    }
}
