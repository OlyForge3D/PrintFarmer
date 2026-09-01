using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
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
