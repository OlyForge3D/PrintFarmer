using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class MoonrakerDiagnosticsControllerTests
{
    [Fact]
    public async Task GetFileRootsAsync_ReturnsOk_WhenServiceReturnsRoots()
    {
        Mock<IMoonrakerDiagnosticsService> mockSvc = new Mock<IMoonrakerDiagnosticsService>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockSvc.Setup(s => s.GetFileRootsAsync(It.IsAny<string>()))
            .ReturnsAsync(new[] { new FileRoot { Path = "/gcodes" } });

        MoonrakerDiagnosticsController controller = new MoonrakerDiagnosticsController(mockSvc.Object, mockLogger.Object);

        ActionResult<FileRoot[]> result = await controller.GetFileRootsAsync("http://x");

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFileRootsAsync_ReturnsProblem_WhenServiceReturnsNull()
    {
        Mock<IMoonrakerDiagnosticsService> mockSvc = new Mock<IMoonrakerDiagnosticsService>();
        Mock<IUnifiedLoggingService> mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

        mockSvc.Setup(s => s.GetFileRootsAsync(It.IsAny<string>()))
            .ReturnsAsync((FileRoot[]?)null);

        MoonrakerDiagnosticsController controller = new MoonrakerDiagnosticsController(mockSvc.Object, mockLogger.Object);

        ActionResult<FileRoot[]> result = await controller.GetFileRootsAsync("http://x");

        Assert.IsType<ObjectResult>(result.Result);
        ObjectResult? obj = result.Result as ObjectResult;
        Assert.Equal(500, obj?.StatusCode);
    }
}
