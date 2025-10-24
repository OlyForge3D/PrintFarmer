using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts
{
    [Collection("Artifacts")]
    public class ArtifactsControllerKindValidationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        public ArtifactsControllerKindValidationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact(DisplayName = "Unsupported kind returns 400 with allowedKinds (controller direct)")]
        public async Task Unsupported_Kind_Returns_BadRequest_With_Allowed_List()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
            var opts = Options.Create(new Farm.Infrastructure.Settings.ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail" });
            var jobRepo = new Farm.Web.Api.Tests.Slicing.JobDispatcherServiceTests.StubSliceJobRepository();
            var controller = new Farm.Web.Api.Controllers.ArtifactsController(svc, jobRepo, opts);
            var file = new TestFormFile(System.Text.Encoding.UTF8.GetBytes("dummy"), "a.txt", "text/plain");
            var result = await controller.UploadAsync(Guid.NewGuid(), "invalid-kind", null, file, default);
            result.Should().BeOfType<BadRequestObjectResult>();
            var bad = (BadRequestObjectResult)result;
            bad.Value!.ToString()!.Should().Contain("allowedKinds");
        }

        private sealed class TestFormFile : IFormFile
        {
            private readonly byte[] _data;
            public TestFormFile(byte[] d, string name, string ct)
            {
                _data = d;
                FileName = name;
                ContentType = ct;
                Name = "file";
                Length = d.Length;
            }
            public string ContentType { get; }
            public string ContentDisposition { get; set; } = string.Empty;
            public IHeaderDictionary Headers { get; } = new HeaderDictionary();
            public long Length { get; }
            public string Name { get; }
            public string FileName { get; }
            public void CopyTo(System.IO.Stream target) => target.Write(_data, 0, _data.Length);
            public Task CopyToAsync(System.IO.Stream target, System.Threading.CancellationToken cancellationToken = default)
            {
                target.Write(_data, 0, _data.Length);
                return Task.CompletedTask;
            }
            public System.IO.Stream OpenReadStream() => new System.IO.MemoryStream(_data, writable: false);
        }
    }
}
