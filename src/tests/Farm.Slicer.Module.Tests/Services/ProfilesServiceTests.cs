using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Web.Api.Services.Catalog;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

// Module DTO type aliases — service now returns module DTOs
using ProcessProfileResponseDto = Farm.Slicer.Module.Dtos.ProcessProfileResponseDto;
using SlicerProfileDto = Farm.Slicer.Module.Dtos.SlicerProfileDto;

namespace Farm.Slicer.Module.Tests.Services
{
    public class ProfilesServiceTests
    {
        private static ProfilesService CreateService(IProfilesRepository repo, IUnifiedLoggingService logger)
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
                slicersService.Object);
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
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);

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
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);

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
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);
            IReadOnlyList<SlicerProfileDto> result = await svc.GetProfilesAsync(CancellationToken.None);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetProfilesAsync_ReturnsEmptyList_WhenNoProfiles()
        {
            Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProcessProfile>());

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);
            IReadOnlyList<SlicerProfileDto> result = await svc.GetProfilesAsync(CancellationToken.None);
            Assert.Empty(result);
        }

        [Fact]
        public async Task DeleteProfileAsync_Deletes_WhenExists()
        {
            Guid id = Guid.NewGuid();
            ProcessProfile profile = new ProcessProfile { Id = id, Name = "test" };
            Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
            _ = mockRepo.Setup(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);
            await svc.DeleteProfileAsync(id, CancellationToken.None);

            mockRepo.Verify(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteProfileAsync_Throws_WhenNotFound()
        {
            Guid id = Guid.NewGuid();
            Mock<IProfilesRepository> mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            _ = mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

            ProfilesService svc = CreateService(mockRepo.Object, mockLogger.Object);

            _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteProfileAsync(id, CancellationToken.None));
        }
    }
}
