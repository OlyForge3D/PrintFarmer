using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public sealed class SettingsServiceProvenanceTests
{
    [Fact]
    public void Save_AdvancesOnlySavedSettingsOrigin()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Mock<IDbContextFactory<AppDbContext>> dbFactory = new(MockBehavior.Strict);
        dbFactory
            .Setup(factory => factory.CreateDbContext())
            .Returns(() => new AppDbContext(options));
        Mock<IAppSettingsRepository> repository = new(MockBehavior.Strict);
        repository
            .Setup(repo => repo.SetAsync(
                SpoolCoverageSettings.SectionName,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(repo => repo.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IMutationWatermarkReader> watermarkReader = new(MockBehavior.Strict);
        watermarkReader
            .SetupSequence(reader => reader.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(10)
            .ReturnsAsync(20);
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var service = new SettingsService(
            configuration,
            dbFactory.Object,
            NullLogger<SettingsService>.Instance,
            repository.Object,
            watermarkReader.Object);

        SettingsSnapshot<ShiftPlanSettings> before = service.GetSnapshot<ShiftPlanSettings>();
        service.Save(new SpoolCoverageSettings());

        service.GetSnapshot<SpoolCoverageSettings>().OriginWatermark.Should().Be(20);
        service.GetSnapshot<ShiftPlanSettings>().OriginWatermark.Should().Be(before.OriginWatermark);
        before.OriginWatermark.Should().Be(10);
    }
}
