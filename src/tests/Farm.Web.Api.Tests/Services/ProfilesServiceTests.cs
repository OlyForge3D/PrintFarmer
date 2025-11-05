using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class ProfilesServiceTests
    {
        [Fact]
        public async Task CreateProfileAsync_CreatesAndReturnsDto()
        {
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);

            mockRepo.Setup(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.SlicerProfile>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);

            var req = new CreateSlicerProfileDto
            {
                Name = "Test",
                Description = "desc",
                SlicerType = "PrusaSlicer",
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                EnableSupports = false,
                Material = "PLA",
                Quality = "standard",
                IsDefault = false,
                IsPublic = true,
                AdvancedSettings = "{}"
            };

            var dto = await svc.CreateProfileAsync(req, CancellationToken.None);

            Assert.Equal(req.Name, dto.Name);
            Assert.Equal(req.Description, dto.Description);
            Assert.Equal(req.Material, dto.Material);
            mockRepo.Verify(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.SlicerProfile>(), It.IsAny<CancellationToken>()), Times.Once);
            mockRepo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task GetProfileAsync_ReturnsDto_WhenExists()
        {
            var id = Guid.NewGuid();
            var profile = new Farm.Infrastructure.Domain.SlicerProfile { Id = id, Name = "p", Description = "d", AdvancedSettings = "{}" };

            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);

            var dto = await svc.GetProfileAsync(id, CancellationToken.None);
            Assert.NotNull(dto);
            Assert.Equal(profile.Id, dto!.Id);
            Assert.Equal(profile.Name, dto.Name);
        }

        [Fact]
        public async Task GetProfilesAsync_ReturnsList()
        {
            var list = new List<Farm.Infrastructure.Domain.SlicerProfile>
            {
                new() { Id = Guid.NewGuid(), Name = "A", AdvancedSettings = "{}" },
                new() { Id = Guid.NewGuid(), Name = "B", AdvancedSettings = "{}" }
            };
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(list);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);
            var result = await svc.GetProfilesAsync(CancellationToken.None);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task DeleteProfileAsync_Deletes_WhenExists()
        {
            var id = Guid.NewGuid();
            var profile = new Farm.Infrastructure.Domain.SlicerProfile { Id = id };
            var mockRepo = new Mock<IProfilesRepository>(MockBehavior.Strict);
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>(MockBehavior.Loose);
            mockRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
            mockRepo.Setup(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var svc = new ProfilesService(mockRepo.Object, mockLogger.Object);
            await svc.DeleteProfileAsync(id, CancellationToken.None);

            mockRepo.Verify(r => r.RemoveAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
            mockRepo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
