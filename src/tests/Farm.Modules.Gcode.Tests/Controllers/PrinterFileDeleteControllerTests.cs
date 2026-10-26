using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Gcode.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Controllers;

public sealed class PrinterFileDeleteControllerTests
{
    [Fact]
    public void DeleteController_RequiresInteractiveSessionAndDedicatedDeletePermission()
    {
        var attributes = typeof(PrinterFileDeleteController).GetCustomAttributes(true);
        Assert.Contains(attributes.OfType<AuthorizeAttribute>(), x => x.Policy == InteractiveSessionRequirement.PolicyName);
        Assert.Contains(attributes.OfType<RequirePermissionAttribute>(), x =>
            x.Permission == PrintFarmerPermissions.PrinterFiles.Delete);
        Assert.DoesNotContain(attributes.OfType<RequirePermissionAttribute>(), x =>
            x.Permission == PrintFarmerPermissions.Queue.Write);
    }

    [Fact]
    public async Task Create_ReturnsDurableStatusAndSeparateConfirmationBinding_NotMutationAsync()
    {
        Guid actor = Guid.NewGuid();
        Guid location = Guid.NewGuid();
        var service = new Mock<IPrinterFileDeleteService>(MockBehavior.Strict);
        PrinterFileDeleteOperation operation = new(Guid.NewGuid(), 1,
            PrinterFileOperationState.AwaitingDeleteConfirmation, location, Guid.NewGuid(),
            "gcodes", "Folder/Part.gcode", 3, 1, PrinterFileDeleteGuarantee.CurrentExactPath,
            "delete-specific-binding", false, null, "AwaitingConfirmation", null);
        service.Setup(x => x.CreateAsync(actor, "key", new(location, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        var controller = Controller(service.Object, actor);
        IActionResult result = await controller.CreateAsync(location, new(3), "key", CancellationToken.None);
        AcceptedResult accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal($"/api/printer-file-index/delete-operations/{operation.Id}", accepted.Location);
        Assert.Same(operation, accepted.Value);
        service.Verify(x => x.CreateAsync(actor, "key", new(location, 3), It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Status_IsActorScopedAndNotFoundForDeniedIdentityAsync()
    {
        var service = new Mock<IPrinterFileDeleteService>();
        Guid actor = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        service.Setup(x => x.GetAsync(actor, operation, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterFileDeleteOperation?)null);
        Assert.IsType<NotFoundResult>(await Controller(service.Object, actor).GetAsync(operation, CancellationToken.None));
        service.Verify(x => x.GetAsync(actor, operation, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static PrinterFileDeleteController Controller(IPrinterFileDeleteService service, Guid actor) => new(service)
    {
        ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test")),
            },
        },
    };
}
