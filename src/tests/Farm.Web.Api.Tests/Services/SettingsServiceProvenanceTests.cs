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

    /// <summary>
    /// Regression test for #2320: Save&lt;T&gt;() used to mutate the shared _settings
    /// dictionary in place instead of atomically swapping it. That meant a concurrent
    /// enumeration of <see cref="ISettingsService.All"/> (e.g. via Get/GetByKey callers
    /// iterating settings while another request calls Save) could observe a
    /// "Collection was modified" InvalidOperationException. With the atomic swap, a
    /// reader captures a reference to the current dictionary instance up front, so
    /// concurrent Save calls that replace _settings with a new instance never affect
    /// an in-flight enumeration.
    /// </summary>
    [Fact]
    public void Save_DuringConcurrentEnumeration_DoesNotThrowOrCorruptState()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Mock<IDbContextFactory<AppDbContext>> dbFactory = new(MockBehavior.Strict);
        dbFactory
            .Setup(factory => factory.CreateDbContext())
            .Returns(() => new AppDbContext(options));
        Mock<IAppSettingsRepository> repository = new(MockBehavior.Loose);
        repository
            .Setup(repo => repo.SetAsync(
                SpoolCoverageSettings.SectionName,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(repo => repo.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var service = new SettingsService(
            configuration,
            dbFactory.Object,
            NullLogger<SettingsService>.Instance,
            repository.Object);

        const int iterations = 200;
        using ManualResetEventSlim start = new(initialState: false);
        Exception? readerException = null;

        var readerThread = new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    foreach (object value in service.All)
                    {
                        _ = value;
                    }
                }
                catch (Exception ex)
                {
                    readerException = ex;
                    return;
                }
            }
        });

        var writerThread = new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < iterations; i++)
            {
                service.Save(new SpoolCoverageSettings());
            }
        });

        readerThread.Start();
        writerThread.Start();
        start.Set();
        readerThread.Join();
        writerThread.Join();

        readerException.Should().BeNull("concurrent Save calls must not mutate the dictionary an in-flight enumeration is iterating over");
        service.GetByKey(SpoolCoverageSettings.SectionName).Should().NotBeNull();
    }
}
