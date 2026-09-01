using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Regression tests for issue #2348: a client abort (request cancellation) on the
/// slicer profile hierarchy endpoints must propagate as an aborted-connection
/// cancellation, not be swallowed by the controller's general exception handler
/// and reported as an HTTP 500.
/// </summary>
public class ProfilesControllerHierarchyCancellationTests
{
    [Fact]
    public async Task GetWorkerProfilesHierarchyAsync_ClientCancels_PropagatesOperationCanceledException()
    {
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        _ = profilesService
            .Setup(s => s.GetWorkerProfilesHierarchyAsync(It.IsAny<HttpClient>(), cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        ProfilesController controller = CreateController(profilesService, catalogService);
        using HttpClient httpClient = new();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.GetWorkerProfilesHierarchyAsync(httpClient, cts.Token));
    }

    [Fact]
    public async Task GetLibraryProfilesHierarchyAsync_ClientCancels_PropagatesOperationCanceledException()
    {
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        _ = profilesService
            .Setup(s => s.GetCatalogAttributedWorkerHierarchyAsync(It.IsAny<HttpClient>(), "catalog", cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        ProfilesController controller = CreateController(profilesService, catalogService);
        using HttpClient httpClient = new();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.GetLibraryProfilesHierarchyAsync(httpClient, "catalog", cts.Token));
    }

    [Fact]
    public async Task GetWorkerProfilesHierarchyAsync_InternalCancellationUnrelatedToRequest_Returns500()
    {
        // The service throws OperationCanceledException tied to a DIFFERENT, already-cancelled
        // token (e.g. an internal timeout), while the request's own token is still live. The
        // guard `when (ct.IsCancellationRequested)` must NOT treat this as a client abort - it
        // should fall through to the general handler and still surface as a 500, so a genuine
        // internal fault is not silently masked as a benign disconnect.
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        using CancellationTokenSource unrelatedCts = new();
        unrelatedCts.Cancel();
        CancellationToken requestToken = CancellationToken.None;

        _ = profilesService
            .Setup(s => s.GetWorkerProfilesHierarchyAsync(It.IsAny<HttpClient>(), requestToken))
            .ThrowsAsync(new OperationCanceledException(unrelatedCts.Token));

        ProfilesController controller = CreateController(profilesService, catalogService);
        using HttpClient httpClient = new();

        IActionResult result = await controller.GetWorkerProfilesHierarchyAsync(httpClient, requestToken);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetLibraryProfilesHierarchyAsync_InternalCancellationUnrelatedToRequest_Returns500()
    {
        // Same discrimination as the worker-hierarchy negative test above, but for the
        // library-hierarchy action's own independent catch clause: an unrelated internal
        // cancellation (different, already-cancelled token) must still 500, not be masked
        // by the `when (ct.IsCancellationRequested)` guard.
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        using CancellationTokenSource unrelatedCts = new();
        unrelatedCts.Cancel();
        CancellationToken requestToken = CancellationToken.None;

        _ = profilesService
            .Setup(s => s.GetCatalogAttributedWorkerHierarchyAsync(It.IsAny<HttpClient>(), "catalog", requestToken))
            .ThrowsAsync(new OperationCanceledException(unrelatedCts.Token));

        ProfilesController controller = CreateController(profilesService, catalogService);
        using HttpClient httpClient = new();

        IActionResult result =
            await controller.GetLibraryProfilesHierarchyAsync(httpClient, "catalog", requestToken);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    private static ProfilesController CreateController(
        Mock<IProfilesService> profilesService,
        Mock<ICatalogServiceAdapter> catalogService)
    {
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            profilesService.Object,
            catalogService.Object);
    }
}
