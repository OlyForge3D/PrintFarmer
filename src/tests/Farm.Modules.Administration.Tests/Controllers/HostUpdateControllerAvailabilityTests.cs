using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

/// <summary>
/// Bishop/Hicks review (issue #2663): the manual admin API must reject execute/recover while the
/// executor's positively-proven <see cref="HostUpdateExecutionAvailabilityHolder"/> reports
/// <see cref="HostUpdateExecutionAvailabilityState.Unavailable"/> -- including, critically, while
/// restart reconciliation still reports an unresolved prior release -- rather than allowing a
/// caller to reach <see cref="IHostUpdateExecutor"/>/<see cref="IHostUpdateRecoveryCoordinator"/>
/// against a host that is not actually ready. These tests use <see cref="MockBehavior.Strict"/>
/// on the executor/recovery mocks specifically to prove the rejected calls never delegate.
/// </summary>
public class HostUpdateControllerAvailabilityTests
{
    private static Farm.Modules.Administration.Controllers.Admin.HostUpdateExecuteRequestBody ValidBody() => new(
        "release-1",
        1,
        "sha256:" + new string('a', 64),
        new string('b', 40),
        "Stable",
        new[]
        {
            new HostUpdateExecutionTarget("api", "linux/amd64", "sha256:" + new string('c', 64)),
            new HostUpdateExecutionTarget("web", "linux/amd64", "sha256:" + new string('d', 64)),
            new HostUpdateExecutionTarget("worker", "linux/amd64", "sha256:" + new string('e', 64)),
            new HostUpdateExecutionTarget("slicer", "linux/amd64", "sha256:" + new string('f', 64)),
            new HostUpdateExecutionTarget("proxy", "linux/amd64", "sha256:" + new string('0', 64)),
            new HostUpdateExecutionTarget("db", "linux/amd64", "sha256:" + new string('1', 64)),
        });

    [Fact]
    public async Task ExecuteAsync_WhenUnavailable_Returns503AndNeverDelegates()
    {
        Mock<IHostUpdateExecutor> executor = new(MockBehavior.Strict);
        Mock<IHostUpdateExecutionJournal> journal = new(MockBehavior.Strict);
        Mock<IHostUpdateRecoveryCoordinator> recovery = new(MockBehavior.Strict);
        var holder = new HostUpdateExecutionAvailabilityHolder();
        holder.Update(HostUpdateExecutionAvailability.Unavailable(DateTimeOffset.UtcNow, ["root_directory_not_configured"]));
        var controller = new Farm.Modules.Administration.Controllers.Admin.HostUpdateController(executor.Object, journal.Object, recovery.Object, holder);

        ActionResult<Farm.Modules.Administration.Controllers.Admin.HostUpdateStatusResponse> result =
            await controller.ExecuteAsync(ValidBody(), CancellationToken.None);

        ObjectResult objectResult = result.Result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverAsync_WhenUnavailable_Returns503AndNeverDelegates()
    {
        Mock<IHostUpdateExecutor> executor = new(MockBehavior.Strict);
        Mock<IHostUpdateExecutionJournal> journal = new(MockBehavior.Strict);
        Mock<IHostUpdateRecoveryCoordinator> recovery = new(MockBehavior.Strict);
        var holder = new HostUpdateExecutionAvailabilityHolder();
        holder.Update(HostUpdateExecutionAvailability.Unavailable(DateTimeOffset.UtcNow, ["restart_reconciliation_pending:release-1:Fenced"]));
        var controller = new Farm.Modules.Administration.Controllers.Admin.HostUpdateController(executor.Object, journal.Object, recovery.Object, holder);

        ActionResult<HostUpdateRecoveryResult> result =
            await controller.RecoverAsync("release-1", ValidBody(), CancellationToken.None);

        ObjectResult objectResult = result.Result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        journal.VerifyNoOtherCalls();
        recovery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAvailable_DelegatesToExecutor()
    {
        Mock<IHostUpdateExecutor> executor = new(MockBehavior.Strict);
        Mock<IHostUpdateExecutionJournal> journal = new(MockBehavior.Strict);
        Mock<IHostUpdateRecoveryCoordinator> recovery = new(MockBehavior.Strict);
        var holder = new HostUpdateExecutionAvailabilityHolder();
        holder.Update(HostUpdateExecutionAvailability.Available(DateTimeOffset.UtcNow));
        var expected = new HostUpdateExecutionResult("release-1", HostUpdateExecutionState.Completed, null, []);
        executor.Setup(e => e.ExecuteAsync(It.IsAny<HostUpdateExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new Farm.Modules.Administration.Controllers.Admin.HostUpdateController(executor.Object, journal.Object, recovery.Object, holder);

        ActionResult<Farm.Modules.Administration.Controllers.Admin.HostUpdateStatusResponse> result =
            await controller.ExecuteAsync(ValidBody(), CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().BeOfType<Farm.Modules.Administration.Controllers.Admin.HostUpdateStatusResponse>();
        executor.Verify(e => e.ExecuteAsync(It.IsAny<HostUpdateExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
