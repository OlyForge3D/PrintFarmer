using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers
{
    public class FilamentTypeControllerTests
    {
        [Fact]
        public async Task GetFilamentTypesAsync_DelegatesToService()
        {
            Mock<IFilamentTypeService> mockService = new Mock<IFilamentTypeService>(MockBehavior.Strict);
            Mock<IStartupStatus> mockStartup = new Mock<IStartupStatus>();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            _ = mockStartup.Setup(s => s.IsReady).Returns(true);

            List<FilamentTypeDto> expected = new List<FilamentTypeDto> { new FilamentTypeDto(System.Guid.NewGuid(), "PLA", new TempTargets(200, 60)) };
            _ = mockService.Setup(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            FilamentTypeController controller = new FilamentTypeController(mockService.Object, mockStartup.Object, mockLogger.Object);

            ActionResult<IEnumerable<FilamentTypeDto>> result = await controller.GetFilamentTypesAsync(CancellationToken.None);

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
            IEnumerable<FilamentTypeDto> value = Assert.IsAssignableFrom<IEnumerable<FilamentTypeDto>>(ok.Value);
            _ = Assert.Single(value);

            mockService.Verify(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
