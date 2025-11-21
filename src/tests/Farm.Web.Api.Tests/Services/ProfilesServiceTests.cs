using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class ProfilesServiceTests
    {
        // NOTE: Tests using non-existent CreateSlicerProfileDto DTO have been removed.
        // The DTO structure was refactored to use composite profiles (Machine/Process/Filament).
        // ProfilesService primarily handles database operations - unit tests for DTO creation
        // are covered by integration tests that use actual infrastructure.

        [Fact]
        public async Task GetProfileAsync_ReturnsDto_WhenExists()
        {
            var id = Guid.NewGuid();
            var profile = new Farm.Infrastructure.Domain.ProcessProfile { Id = id, Name = "p", Description = "d", RawJson = "{}" };

            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);

            var dto = await svc.GetProfileAsync(id, CancellationToken.None);
            Assert.NotNull(dto);
            Assert.Equal(profile.Id, dto!.Id);
            Assert.Equal(profile.Name, dto.Name);
        }

        [Fact]
        public async Task GetProfileAsync_ReturnsNull_WhenNotFound()
        {
            var id = Guid.NewGuid();
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.ProcessProfile?)null);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);

            var dto = await svc.GetProfileAsync(id, CancellationToken.None);
            Assert.Null(dto);
        }

        [Fact]
        public async Task GetProfilesAsync_ReturnsList()
        {
            var list = new List<Farm.Infrastructure.Domain.ProcessProfile>
            {
                new() { Id = Guid.NewGuid(), Name = "A", RawJson = "{}" },
                new() { Id = Guid.NewGuid(), Name = "B", RawJson = "{}" }
            };
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);
            var result = await svc.GetProfilesAsync(CancellationToken.None);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task GetProfilesAsync_ReturnsEmptyList_WhenNoProfiles()
        {
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Farm.Infrastructure.Domain.ProcessProfile>());

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);
            var result = await svc.GetProfilesAsync(CancellationToken.None);
            Assert.Empty(result);
        }

        [Fact]
        public async Task DeleteProfileAsync_Deletes_WhenExists()
        {
            var id = Guid.NewGuid();
            var profile = new Farm.Infrastructure.Domain.ProcessProfile { Id = id, Name = "test" };
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
            mockRepo.Setup(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);
            await svc.DeleteProfileAsync(id, CancellationToken.None);

            mockRepo.Verify(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteProfileAsync_Throws_WhenNotFound()
        {
            var id = Guid.NewGuid();
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.FindByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.ProcessProfile?)null);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteProfileAsync(id, CancellationToken.None));
        }
    }
}
