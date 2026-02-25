using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Filament;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.Startup;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
            Mock<ILogger<FilamentTypeController>> mockLogger = new Mock<ILogger<FilamentTypeController>>();
            Mock<ISpoolmanDbService> mockSpoolmanDb = new Mock<ISpoolmanDbService>();

            _ = mockStartup.Setup(s => s.IsReady).Returns(true);

            List<FilamentTypeDto> expected = new List<FilamentTypeDto> { new FilamentTypeDto(System.Guid.NewGuid(), "PLA", new TempTargets(200, 60), false, false) };
            _ = mockService.Setup(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            FilamentTypeController controller = new FilamentTypeController(mockService.Object, mockStartup.Object, mockLogger.Object, mockSpoolmanDb.Object);

            IActionResult result = await controller.GetFilamentTypesAsync(ct: CancellationToken.None);

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
            IEnumerable<FilamentTypeDto> value = Assert.IsAssignableFrom<IEnumerable<FilamentTypeDto>>(ok.Value);
            _ = Assert.Single(value);

            mockService.Verify(s => s.GetFilamentTypesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetFilamentTypesAsync_WithPage_ReturnsPagedResult()
        {
            Mock<IFilamentTypeService> mockService = new Mock<IFilamentTypeService>(MockBehavior.Strict);
            Mock<IStartupStatus> mockStartup = new Mock<IStartupStatus>();
            Mock<ILogger<FilamentTypeController>> mockLogger = new Mock<ILogger<FilamentTypeController>>();
            Mock<ISpoolmanDbService> mockSpoolmanDb = new Mock<ISpoolmanDbService>();

            _ = mockStartup.Setup(s => s.IsReady).Returns(true);

            List<FilamentTypeDto> items = new List<FilamentTypeDto> { new FilamentTypeDto(System.Guid.NewGuid(), "PLA", new TempTargets(200, 60), false, false) };
            PagedResult<FilamentTypeDto> pagedResult = new PagedResult<FilamentTypeDto>(items, 1, 1, 50, 1);
            _ = mockService.Setup(s => s.GetPagedFilamentTypesAsync(1, 50, null, It.IsAny<CancellationToken>())).ReturnsAsync(pagedResult);

            FilamentTypeController controller = new FilamentTypeController(mockService.Object, mockStartup.Object, mockLogger.Object, mockSpoolmanDb.Object);

            IActionResult result = await controller.GetFilamentTypesAsync(page: 1, ct: CancellationToken.None);

            OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
            PagedResult<FilamentTypeDto> value = Assert.IsType<PagedResult<FilamentTypeDto>>(ok.Value);
            Assert.Equal(1, value.TotalCount);

            mockService.Verify(s => s.GetPagedFilamentTypesAsync(1, 50, null, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
