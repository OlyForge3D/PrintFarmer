using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class ProfileManufacturerMaintenanceServiceTests
{
    [Fact]
    public async Task BackfillAsync_LegacyFamilies_UpdatesEligibleRowsAndIsIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        DateTime originalUpdatedAt = DateTime.UtcNow.AddDays(-1);
        dbContext.MachineModelProfiles.AddRange(
            Family(familyId, modelId, "Custom"),
            Family(Guid.NewGuid(), modelId, "Already Correct"),
            Family(Guid.NewGuid(), null, "Custom"));
        dbContext.MachineProfiles.AddRange(
            Variant(familyId, modelId, "Custom"),
            Variant(null, modelId, "Custom"),
            Variant(familyId, modelId, "Already Correct"));
        foreach (MachineModelProfile family in dbContext.MachineModelProfiles.Local)
        {
            family.UpdatedAt = originalUpdatedAt;
        }

        foreach (MachineProfile variant in dbContext.MachineProfiles.Local)
        {
            variant.UpdatedAt = originalUpdatedAt;
        }

        _ = await dbContext.SaveChangesAsync();

        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<PrinterModelDto>)[new PrinterModelDto(modelId, "Micron", manufacturerId)],
                null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<ManufacturerDto>)[new ManufacturerDto(manufacturerId, "PrintersForAnts")],
                null));
        var service = new ProfileManufacturerMaintenanceService(
            dbContext,
            catalog.Object,
            NullLogger<ProfileManufacturerMaintenanceService>.Instance);

        ProfileManufacturerBackfillResultDto first =
            await service.BackfillAsync(CancellationToken.None);
        ProfileManufacturerBackfillResultDto second =
            await service.BackfillAsync(CancellationToken.None);

        first.Should().Be(new ProfileManufacturerBackfillResultDto(1, 2, 0));
        second.Should().Be(new ProfileManufacturerBackfillResultDto(0, 0, 0));
        (await dbContext.MachineModelProfiles.SingleAsync(row => row.Id == familyId))
            .Manufacturer.Should().Be("PrintersForAnts");
        (await dbContext.MachineModelProfiles.SingleAsync(row => row.Id == familyId))
            .UpdatedAt.Should().BeAfter(originalUpdatedAt);
        (await dbContext.MachineProfiles.Where(row => row.Manufacturer == "PrintersForAnts").CountAsync())
            .Should().Be(2);
        (await dbContext.MachineProfiles.Where(row => row.Manufacturer == "Already Correct").CountAsync())
            .Should().Be(1);
        (await dbContext.MachineModelProfiles.Where(row => row.Manufacturer == "Custom").CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task BackfillAsync_ModelMissingFromCatalog_IncrementsSkippedAndLeavesRowsUntouched()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        DateTime originalUpdatedAt = DateTime.UtcNow.AddDays(-1);
        MachineModelProfile family = Family(familyId, modelId, "Custom");
        MachineProfile variant = Variant(familyId, modelId, "Custom");
        family.UpdatedAt = originalUpdatedAt;
        variant.UpdatedAt = originalUpdatedAt;
        dbContext.AddRange(family, variant);
        _ = await dbContext.SaveChangesAsync();

        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<PrinterModelDto>)[], null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ManufacturerDto>)[], null));
        var service = new ProfileManufacturerMaintenanceService(
            dbContext,
            catalog.Object,
            NullLogger<ProfileManufacturerMaintenanceService>.Instance);

        ProfileManufacturerBackfillResultDto result =
            await service.BackfillAsync(CancellationToken.None);

        result.Should().Be(new ProfileManufacturerBackfillResultDto(0, 0, 1));
        family.Manufacturer.Should().Be("Custom");
        variant.Manufacturer.Should().Be("Custom");
        family.UpdatedAt.Should().Be(originalUpdatedAt);
        variant.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public async Task BackfillAsync_SaveChangesFails_LogsNoResolvedEntriesForRolledBackWork()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using ThrowingOnSaveSlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(Family(familyId, modelId, "Custom"));
        dbContext.MachineProfiles.Add(Variant(familyId, modelId, "Custom"));
        _ = await dbContext.SaveChangesAsync();
        dbContext.ThrowOnNextSave = true;

        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<PrinterModelDto>)[new PrinterModelDto(modelId, "Micron", manufacturerId)],
                null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<ManufacturerDto>)[new ManufacturerDto(manufacturerId, "PrintersForAnts")],
                null));
        RecordingLogger<ProfileManufacturerMaintenanceService> recordingLogger = new();
        var service = new ProfileManufacturerMaintenanceService(
            dbContext,
            catalog.Object,
            recordingLogger);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BackfillAsync(CancellationToken.None));
        recordingLogger.Entries.Should()
            .NotContain(entry => entry.Message.Contains("resolved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackfillAsync_SavesSuccessfully_LogsPerFamilyLinesAtDebugAndOneInformationSummary()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(Family(familyId, modelId, "Custom"));
        dbContext.MachineProfiles.Add(Variant(familyId, modelId, "Custom"));
        _ = await dbContext.SaveChangesAsync();

        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<PrinterModelDto>)[new PrinterModelDto(modelId, "Micron", manufacturerId)],
                null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<ManufacturerDto>)[new ManufacturerDto(manufacturerId, "PrintersForAnts")],
                null));
        RecordingLogger<ProfileManufacturerMaintenanceService> recordingLogger = new();
        var service = new ProfileManufacturerMaintenanceService(
            dbContext,
            catalog.Object,
            recordingLogger);

        _ = await service.BackfillAsync(CancellationToken.None);

        recordingLogger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("completed", StringComparison.OrdinalIgnoreCase));
        recordingLogger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug
            && entry.Message.Contains("resolved", StringComparison.OrdinalIgnoreCase));
        recordingLogger.Entries.Should()
            .NotContain(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("resolved", StringComparison.OrdinalIgnoreCase));

        // The per-family "resolved" line must be recorded only after the commit-completing
        // "completed" summary line's underlying save — i.e. the Debug entry index must not
        // precede the point at which SaveChangesAsync has already committed. Since both
        // entries are logged after the same SaveChangesAsync call in production code, and
        // recorded here in call order, the "resolved" (Debug) entry must appear before the
        // "completed" (Information) summary in the log sequence, but both only after save.
        int resolvedIndex = recordingLogger.Entries.FindIndex(entry =>
            entry.Message.Contains("resolved", StringComparison.OrdinalIgnoreCase));
        int completedIndex = recordingLogger.Entries.FindIndex(entry =>
            entry.Message.Contains("completed", StringComparison.OrdinalIgnoreCase));
        resolvedIndex.Should().BeLessThan(completedIndex);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingOnSaveSlicerDbContext(DbContextOptions<SlicerDbContext> options)
        : SlicerDbContext(options)
    {
        public bool ThrowOnNextSave { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ThrowOnNextSave
                ? throw new InvalidOperationException("Simulated SaveChangesAsync failure.")
                : base.SaveChangesAsync(cancellationToken);
    }

    private static MachineModelProfile Family(Guid id, Guid? modelId, string manufacturer) =>
        new()
        {
            Id = id,
            Name = $"Family {id}",
            Manufacturer = manufacturer,
            PrinterModelId = modelId,
            SlicerType = SlicerType.OrcaSlicer,
            Hash = id.ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static MachineProfile Variant(Guid? familyId, Guid modelId, string manufacturer) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = $"Variant {Guid.NewGuid()}",
            Manufacturer = manufacturer,
            PrinterModelId = modelId,
            MachineModelProfileId = familyId,
            SlicerType = SlicerType.OrcaSlicer,
            Hash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
