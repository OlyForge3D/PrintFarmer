using System.Reflection;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Modules.Administration.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

public sealed class HostUpdateAutomationPolicyControllerTests
{
    [Fact]
    public void RequiresFarmAdminSystemSettingsPermission()
    {
        RequirePermissionAttribute? permission = typeof(HostUpdateAutomationPolicyController)
            .GetCustomAttribute<RequirePermissionAttribute>();

        Assert.NotNull(permission);
        Assert.Equal("system_settings", permission.Resource);
        Assert.Equal("admin", permission.Action);
    }

    [Fact]
    public async Task ReplaceReturnsBadRequestForInvalidPayload()
    {
        Mock<IHostUpdateAutomationPolicyRepository> repository = new(MockBehavior.Strict);
        repository.Setup(value => value.ReplaceAsync(It.IsAny<HostUpdateAutomationPolicy>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostUpdatePolicyReadResult(false, new HostUpdateAutomationPolicy(), "host_update_policy_invalid"));

        HostUpdateAutomationPolicyController controller = new(repository.Object, null!);
        ActionResult<HostUpdateAutomationPolicy> result = await controller.ReplaceAsync(
            new HostUpdateAutomationPolicyRequest(0, true, false, "bogus", false, 3600, null, 0, 24),
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        repository.VerifyAll();
    }

    [Theory]
    [InlineData("host_update_policy_corrupt")]
    [InlineData("host_update_policy_unavailable")]
    public async Task ReplaceReturnsServiceUnavailableForPolicyStorageFailures(string error)
    {
        Mock<IHostUpdateAutomationPolicyRepository> repository = new(MockBehavior.Strict);
        repository.Setup(value => value.ReplaceAsync(It.IsAny<HostUpdateAutomationPolicy>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostUpdatePolicyReadResult(false, new HostUpdateAutomationPolicy(), error));

        HostUpdateAutomationPolicyController controller = new(repository.Object, null!);
        ActionResult<HostUpdateAutomationPolicy> result = await controller.ReplaceAsync(
            new HostUpdateAutomationPolicyRequest(0, true, false, "stable", false, 3600, null, 0, 24),
            CancellationToken.None);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        repository.VerifyAll();
    }

    [Fact]
    public async Task ReplaceReturnsConflictForStaleRevision()
    {
        Mock<IHostUpdateAutomationPolicyRepository> repository = new(MockBehavior.Strict);
        HostUpdateAutomationPolicy current = new(Revision: 4);
        repository.Setup(value => value.ReplaceAsync(It.IsAny<HostUpdateAutomationPolicy>(), 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostUpdatePolicyReadResult(false, current, "host_update_policy_revision_conflict"));

        HostUpdateAutomationPolicyController controller = new(repository.Object, null!);
        ActionResult<HostUpdateAutomationPolicy> result = await controller.ReplaceAsync(
            new HostUpdateAutomationPolicyRequest(3, true, false, "stable", false, 3600, null, 0, 24),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        repository.VerifyAll();
    }
}
