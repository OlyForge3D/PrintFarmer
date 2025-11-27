using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Filament;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Tests.Controllers
{
    public class FilamentTypeControllerTests
    {
        [Fact]
        public async Task GetFilamentTypesAsync_DelegatesToService()
        {
            Mock<IFilamentTypeService> mockService = new Mock<IFilamentTypeService>(MockBehavior.Strict);
            Mock<IStartupStatus> mockStartup = new Mock<Farm.Web.Api.Services.Interfaces.IStartupStatus>();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            mockStartup.Setup(s => s.IsReady).Returns(true);

            List<FilamentTypeDto> expected = new List<Shared.FilamentTypeDto> { new Shared.FilamentTypeDto(System.Guid.NewGuid(), "PLA", new Shared.TempTargets(200, 60)) };
            mockService.Setup(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            FilamentTypeController controller = new FilamentTypeController(mockService.Object, mockStartup.Object, mockLogger.Object);

            ActionResult<IEnumerable<FilamentTypeDto>> result = await controller.GetFilamentTypesAsync(CancellationToken.None);

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
            IEnumerable<FilamentTypeDto> value = Assert.IsAssignableFrom<IEnumerable<Shared.FilamentTypeDto>>(ok.Value);
            Assert.Single(value);

            mockService.Verify(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
