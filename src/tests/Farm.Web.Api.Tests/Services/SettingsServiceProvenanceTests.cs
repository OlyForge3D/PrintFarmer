using System.Reflection;
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
    /// dictionary in place (`_settings[key] = value`) instead of atomically swapping it
    /// out for a new instance, unlike the existing LoadSettings/Reload pattern. A caller
    /// that captured a reference to the dictionary before Save (e.g. via reflection, or
    /// any future code that snapshots the field) would see that same reference mutated
    /// underneath it. This test captures the private _settings field before Save and
    /// asserts Save replaces the field with a new dictionary instance rather than
    /// mutating the previously-captured one - the failure mode a "Collection was
    /// modified" test cannot reliably catch, because overwriting a value for an
    /// already-present key does not invalidate .NET's Dictionary enumerator/version.
    /// </summary>
    [Fact]
    public void Save_ReplacesSettingsDictionaryInstance_InsteadOfMutatingPreviousReference()
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
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var service = new SettingsService(
            configuration,
            dbFactory.Object,
            NullLogger<SettingsService>.Instance,
            repository.Object);

        FieldInfo settingsField = typeof(SettingsService)
            .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SettingsService no longer has a private _settings field; update this test.");

        var settingsBeforeSave = (Dictionary<string, object>)settingsField.GetValue(service)!;
        object previousSpoolCoverageSettings = settingsBeforeSave[SpoolCoverageSettings.SectionName];

        var newSpoolCoverageSettings = new SpoolCoverageSettings();
        service.Save(newSpoolCoverageSettings);

        var settingsAfterSave = (Dictionary<string, object>)settingsField.GetValue(service)!;

        settingsAfterSave.Should().NotBeSameAs(settingsBeforeSave, "Save must atomically swap in a new dictionary rather than mutate the existing one");
        settingsBeforeSave[SpoolCoverageSettings.SectionName].Should().BeSameAs(previousSpoolCoverageSettings, "a previously-captured dictionary reference must not observe later Save calls");
        settingsAfterSave[SpoolCoverageSettings.SectionName].Should().BeSameAs(newSpoolCoverageSettings);
    }
}
