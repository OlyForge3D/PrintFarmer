using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Filament;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shared = Farm.Web.Shared;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers
{
    public class FilamentTypeControllerTests
    {
        [Fact]
        public async Task GetFilamentTypesAsync_DelegatesToService()
        {
            var mockService = new Mock<IFilamentTypeService>(MockBehavior.Strict);
            var mockStartup = new Mock<Farm.Web.Api.Services.Interfaces.IStartupStatus>();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            mockStartup.Setup(s => s.IsReady).Returns(true);

            var expected = new List<Shared.FilamentTypeDto> { new Shared.FilamentTypeDto(System.Guid.NewGuid(), "PLA", new Shared.TempTargets(200, 60)) };
            mockService.Setup(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            var controller = new FilamentTypeController(mockService.Object, mockStartup.Object, mockLogger.Object);

            var result = await controller.GetFilamentTypesAsync(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var value = Assert.IsAssignableFrom<IEnumerable<Shared.FilamentTypeDto>>(ok.Value);
            Assert.Single(value);

            mockService.Verify(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
