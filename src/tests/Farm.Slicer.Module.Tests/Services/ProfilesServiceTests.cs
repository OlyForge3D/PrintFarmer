using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;


namespace Farm.Slicer.Module.Tests.Services;

public class ProfilesServiceTests
{
    private static ProfilesService CreateService(
        IProfilesRepository repo,
        ILogger<ProfilesService> logger,
        Farm.Slicer.Module.Services.ISlicersService? slicersServiceOverride = null)
    {
        Mock<IProcessProfileRepository> processProfileRepo = new(MockBehavior.Loose);
        Mock<IMachineProfileRepository> machineProfileRepo = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentProfileRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);

        return new ProfilesService(
            repo,
            logger,
            processProfileRepo.Object,
            machineProfileRepo.Object,
            filamentProfileRepo.Object,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersServiceOverride ?? slicersService.Object);
    }

    // NOTE: Tests using non-existent CreateSlicerProfileDto DTO have been removed.
    // The DTO structure was refactored to use composite profiles (Machine/Process/Filament).
    // ProfilesService primarily handles database operations - unit tests for DTO creation
    // are covered by integration tests that use actual infrastructure.

    [Fact]
    public async Task GetProfileAsync_ReturnsDto_WhenExists()
    {
        Guid id = Guid.NewGuid();
        ProcessProfile profile = new ProcessProfile { Id = id, Name = "p", Description = "d", RawJson = "{}" };

        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);

        ProcessProfileResponseDto? dto = await svc.GetProfileAsync(id, CancellationToken.None);
        Assert.NotNull(dto);
        Assert.Equal(profile.Id, dto!.Id);
        Assert.Equal(profile.Name, dto.Name);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsNull_WhenNotFound()
    {
        Guid id = Guid.NewGuid();
        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);

        ProcessProfileResponseDto? dto = await svc.GetProfileAsync(id, CancellationToken.None);
        Assert.Null(dto);
    }

    [Fact]
    public async Task GetProfilesAsync_ReturnsList()
    {
        List<ProcessProfile> list = new List<ProcessProfile>
        {
            new() { Id = Guid.NewGuid(), Name = "A", RawJson = "{}" },
            new() { Id = Guid.NewGuid(), Name = "B", RawJson = "{}" }
        };
        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);
        IReadOnlyList<SlicerProfileDto> result = await svc.GetProfilesAsync(CancellationToken.None);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetProfilesAsync_ReturnsEmptyList_WhenNoProfiles()
    {
        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProcessProfile>());

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);
        IReadOnlyList<SlicerProfileDto> result = await svc.GetProfilesAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DeleteProfileAsync_Deletes_WhenExists()
    {
        Guid id = Guid.NewGuid();
        ProcessProfile profile = new ProcessProfile { Id = id, Name = "test" };
        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        _ = mockRepo.Setup(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);
        await svc.DeleteProfileAsync(id, CancellationToken.None);

        mockRepo.Verify(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteProfileAsync_Throws_WhenNotFound()
    {
        Guid id = Guid.NewGuid();
        Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
        ILogger<ProfilesService> mockLogger = NullLogger<ProfilesService>.Instance;
        _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        ProfilesService svc = CreateService(mockRepo.Object, mockLogger);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteProfileAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task GetMachineProfilesForCatalogModelAsync_AliasReturnsEmpty_DoesNotFallbackToManufacturerModel()
    {
        Mock<IProfilesRepository> mockRepo = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Strict);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online"
                }
            });

        List<string> requestedPaths = [];
        using HttpClient httpClient = new(new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }));

        ProfilesService svc = CreateService(mockRepo.Object, NullLogger<ProfilesService>.Instance, slicersService.Object);

        IReadOnlyList<MachineProfileDto> result = await svc.GetMachineProfilesForCatalogModelAsync(
            httpClient,
            ["Wrong Alias"],
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(requestedPaths, path => path.Contains("/api/profiles/machine/Wrong%20Alias", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedPaths, path => path.Contains("/api/profiles/machine/Prusa/Prusa%20CORE%20One", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMachineProfilesForCatalogModelAsync_FirstAliasEmpty_TriesSecondAlias()
    {
        Mock<IProfilesRepository> mockRepo = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = CreateOnlineSlicerService();

        List<string> requestedPaths = [];
        using HttpClient httpClient = new(new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            string responseJson = request.RequestUri.AbsolutePath.EndsWith("/Second%20Alias", StringComparison.Ordinal)
                ? "[{\"name\":\"Second Alias 0.4 nozzle\",\"manufacturer\":\"Test\"}]"
                : "[]";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        }));

        ProfilesService svc = CreateService(mockRepo.Object, NullLogger<ProfilesService>.Instance, slicersService.Object);

        IReadOnlyList<MachineProfileDto> result = await svc.GetMachineProfilesForCatalogModelAsync(
            httpClient,
            ["First Alias", "Second Alias"],
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Second Alias 0.4 nozzle", result[0].Name);
        Assert.Contains(requestedPaths, path => path.EndsWith("/First%20Alias", StringComparison.Ordinal));
        Assert.Contains(requestedPaths, path => path.EndsWith("/Second%20Alias", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetMachineProfilesForCatalogModelAsync_DuplicateAliases_DeduplicatesCaseInsensitively()
    {
        Mock<IProfilesRepository> mockRepo = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = CreateOnlineSlicerService();

        List<string> requestedPaths = [];
        using HttpClient httpClient = new(new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }));

        ProfilesService svc = CreateService(mockRepo.Object, NullLogger<ProfilesService>.Instance, slicersService.Object);

        IReadOnlyList<MachineProfileDto> result = await svc.GetMachineProfilesForCatalogModelAsync(
            httpClient,
            [" Prusa CORE One ", "PRUSA CORE ONE", "prusa core one"],
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Single(requestedPaths);
        Assert.EndsWith("/Prusa%20CORE%20One", requestedPaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMachineProfilesForCatalogModelAsync_WhitespaceAliases_IgnoresWhitespaceAliases()
    {
        Mock<IProfilesRepository> mockRepo = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = CreateOnlineSlicerService();

        List<string> requestedPaths = [];
        using HttpClient httpClient = new(new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }));

        ProfilesService svc = CreateService(mockRepo.Object, NullLogger<ProfilesService>.Instance, slicersService.Object);

        IReadOnlyList<MachineProfileDto> result = await svc.GetMachineProfilesForCatalogModelAsync(
            httpClient,
            [" ", "\t", "Valid Alias"],
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Single(requestedPaths);
        Assert.EndsWith("/Valid%20Alias", requestedPaths[0], StringComparison.Ordinal);
    }

    private static Mock<Farm.Slicer.Module.Services.ISlicersService> CreateOnlineSlicerService()
    {
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Strict);
        _ = slicersService
            .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerService>
            {
                new()
                {
                    Name = "orca",
                    SlicerType = 1,
                    Host = "http://worker",
                    Status = "Online"
                }
            });

        return slicersService;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send(request));
        }
    }
}
