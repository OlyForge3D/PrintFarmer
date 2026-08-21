using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.DataManagement;

/// <summary>
/// Proves nozzle material and hardness survive an export → import round trip.
/// <para>
/// Before this coverage existed, <c>NozzleModelExportDto</c> carried neither field, so a
/// backup/restore silently reset every nozzle to Brass/Auto. That is safety-relevant: an
/// operator-pinned <see cref="NozzleHardnessOverride.NotHardened"/> restored as
/// <see cref="NozzleHardnessOverride.Auto"/> re-admits the nozzle to abrasive-filament
/// dispatch.
/// </para>
/// </summary>
public sealed class NozzleModelExportRoundTripTests
{
    private readonly AppDbContext _sourceContext;
    private readonly AppDbContext _targetContext;
    private readonly DataExportService _exportService;
    private readonly DataImportService _importService;

    public NozzleModelExportRoundTripTests()
    {
        _sourceContext = NewContext();
        _targetContext = NewContext();

        _exportService = new DataExportService(_sourceContext, Mock.Of<ILogger<DataExportService>>());

        Mock<ISensitiveDataProtector> protector = new();
        _ = protector.Setup(x => x.Protect(It.IsAny<string?>()))
            .Returns<string?>(s => string.IsNullOrEmpty(s) ? null : $"prot:{s}");

        _importService = new DataImportService(
            _targetContext,
            Mock.Of<ILogger<DataImportService>>(),
            protector.Object,
            new EfPrintersRepository(_targetContext, protector.Object));
    }

    private static AppDbContext NewContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private async Task SeedSourceNozzleAsync(
        string name,
        NozzleType nozzleType,
        NozzleHardnessOverride hardnessOverride,
        NozzleInterfaceType nozzleInterface = NozzleInterfaceType.V6)
    {
        Manufacturer manufacturer =
            await _sourceContext.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Diamondback")
            ?? new Manufacturer { Id = Guid.NewGuid(), Name = "Diamondback" };

        if (_sourceContext.Entry(manufacturer).State == EntityState.Detached)
        {
            _ = await _sourceContext.Manufacturers.AddAsync(manufacturer);
        }

        _ = await _sourceContext.NozzleModelDefinitions.AddAsync(new NozzleModelDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            ManufacturerId = manufacturer.Id,
            Diameter = 0.4,
            MaxTemp = 550,
            NozzleType = nozzleType,
            HardnessOverride = hardnessOverride,
            NozzleInterface = nozzleInterface,
        });

        await _sourceContext.SaveChangesAsync();
    }

    private async Task<NozzleModelDefinition> RoundTripAsync(string name)
    {
        CatalogExportDto catalog = await _exportService.ExportCatalogAsync();
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);
        result.Success.Should().BeTrue(because: string.Join("; ", result.Errors));

        NozzleModelDefinition? restored = await _targetContext.NozzleModelDefinitions
            .FirstOrDefaultAsync(n => n.Name == name);
        restored.Should().NotBeNull();
        return restored!;
    }

    [Fact]
    public async Task RoundTrip_PreservesNozzleMaterial()
    {
        await SeedSourceNozzleAsync("Diamondback V6", NozzleType.Diamond, NozzleHardnessOverride.Auto);

        NozzleModelDefinition restored = await RoundTripAsync("Diamondback V6");

        restored.NozzleType.Should().Be(NozzleType.Diamond, "restoring a backup must not reset the material to Brass");
        restored.IsHardened.Should().BeTrue();
    }

    [Fact]
    public async Task RoundTrip_PreservesPinnedNotHardened()
    {
        await SeedSourceNozzleAsync(
            "Pinned Soft", NozzleType.Diamond, NozzleHardnessOverride.NotHardened);

        NozzleModelDefinition restored = await RoundTripAsync("Pinned Soft");

        restored.HardnessOverride.Should().Be(NozzleHardnessOverride.NotHardened);
        restored.IsHardened.Should().BeFalse(
            "losing an operator's NotHardened pin would silently re-admit the nozzle to abrasive dispatch");
    }

    [Fact]
    public async Task RoundTrip_PreservesPinnedHardened()
    {
        await SeedSourceNozzleAsync("Pinned Hard", NozzleType.Brass, NozzleHardnessOverride.Hardened);

        NozzleModelDefinition restored = await RoundTripAsync("Pinned Hard");

        restored.HardnessOverride.Should().Be(NozzleHardnessOverride.Hardened);
        restored.IsHardened.Should().BeTrue();
    }

    [Fact]
    public async Task RoundTrip_PreservesNozzleInterface()
    {
        await SeedSourceNozzleAsync(
            "Diamondback Volcano", NozzleType.Diamond, NozzleHardnessOverride.Auto, NozzleInterfaceType.Volcano);

        NozzleModelDefinition restored = await RoundTripAsync("Diamondback Volcano");

        restored.NozzleInterface.Should().Be(NozzleInterfaceType.Volcano);
    }

    [Fact]
    public async Task Import_LegacyBackupWithoutNozzleFields_FallsBackToBrassAuto()
    {
        // Backups written before these fields existed carry null. They must import cleanly
        // rather than throwing or landing an undefined enum value.
        CatalogExportDto legacy = new()
        {
            Manufacturers = [new ManufacturerExportDto { Name = "Legacy Mfg" }],
            Nozzles =
            [
                new NozzleModelExportDto
                {
                    Name = "Legacy Nozzle",
                    ManufacturerName = "Legacy Mfg",
                    Diameter = 0.4,
                    MaxTemp = 300,
                    NozzleType = null,
                    HardnessOverride = null,
                }
            ],
        };

        ImportResponseDto result = await _importService.ImportCatalogAsync(legacy, ImportMode.Merge);
        result.Success.Should().BeTrue(because: string.Join("; ", result.Errors));

        NozzleModelDefinition restored = (await _targetContext.NozzleModelDefinitions
            .FirstOrDefaultAsync(n => n.Name == "Legacy Nozzle"))!;
        restored.NozzleType.Should().Be(NozzleType.Brass);
        restored.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
    }

    [Fact]
    public async Task Import_UnrecognizedEnumNames_RejectsRowRatherThanGuessing()
    {
        // A present-but-unparseable value is corruption, not a legacy backup. Falling back
        // would be actively unsafe here: "NotHardend" on a Diamond nozzle would resolve
        // through Auto back to hardened, re-admitting a nozzle the operator excluded.
        CatalogExportDto corrupt = new()
        {
            Manufacturers = [new ManufacturerExportDto { Name = "Corrupt Mfg" }],
            Nozzles =
            [
                new NozzleModelExportDto
                {
                    Name = "Corrupt Nozzle",
                    ManufacturerName = "Corrupt Mfg",
                    Diameter = 0.4,
                    NozzleType = "Diamond",
                    HardnessOverride = "NotHardend",
                }
            ],
        };

        ImportResponseDto result = await _importService.ImportCatalogAsync(corrupt, ImportMode.Merge);

        result.Success.Should().BeFalse("a corrupt safety field must surface, not be silently defaulted");
        result.Errors.Should().Contain(e => e.Contains("hardnessOverride", StringComparison.OrdinalIgnoreCase));
        (await _targetContext.NozzleModelDefinitions.AnyAsync(n => n.Name == "Corrupt Nozzle"))
            .Should().BeFalse("the row is skipped rather than imported with a guessed value");
    }

    [Fact]
    public async Task Import_UnrecognizedNozzleType_RejectsRow()
    {
        CatalogExportDto corrupt = new()
        {
            Manufacturers = [new ManufacturerExportDto { Name = "Corrupt Mfg" }],
            Nozzles =
            [
                new NozzleModelExportDto
                {
                    Name = "Unobtanium Nozzle",
                    ManufacturerName = "Corrupt Mfg",
                    Diameter = 0.4,
                    NozzleType = "Unobtanium",
                }
            ],
        };

        ImportResponseDto result = await _importService.ImportCatalogAsync(corrupt, ImportMode.Merge);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("nozzleType", StringComparison.OrdinalIgnoreCase));
        (await _targetContext.NozzleModelDefinitions.AnyAsync(n => n.Name == "Unobtanium Nozzle"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Import_UndefinedNumericEnumValue_RejectsRow()
    {
        // Enum.TryParse accepts raw numeric text and undefined ordinals; Enum.IsDefined
        // is what stops "42" landing as an invalid enum in the database.
        CatalogExportDto corrupt = new()
        {
            Manufacturers = [new ManufacturerExportDto { Name = "Corrupt Mfg" }],
            Nozzles =
            [
                new NozzleModelExportDto
                {
                    Name = "Numeric Nozzle",
                    ManufacturerName = "Corrupt Mfg",
                    Diameter = 0.4,
                    HardnessOverride = "42",
                }
            ],
        };

        ImportResponseDto result = await _importService.ImportCatalogAsync(corrupt, ImportMode.Merge);

        result.Success.Should().BeFalse();
        (await _targetContext.NozzleModelDefinitions.AnyAsync(n => n.Name == "Numeric Nozzle"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Import_DefinedOrdinalAsString_IsRejectedBecauseContractIsNames()
    {
        // "1" maps onto a *defined* member, so Enum.IsDefined alone would accept it. The
        // export contract is enum names precisely so a future renumbering cannot remap
        // restored rows; accepting ordinals on input would silently undo that guarantee.
        CatalogExportDto ordinal = new()
        {
            Manufacturers = [new ManufacturerExportDto { Name = "Corrupt Mfg" }],
            Nozzles =
            [
                new NozzleModelExportDto
                {
                    Name = "Ordinal Nozzle",
                    ManufacturerName = "Corrupt Mfg",
                    Diameter = 0.4,
                    HardnessOverride = "1",
                }
            ],
        };

        ImportResponseDto result = await _importService.ImportCatalogAsync(ordinal, ImportMode.Merge);

        result.Success.Should().BeFalse();
        (await _targetContext.NozzleModelDefinitions.AnyAsync(n => n.Name == "Ordinal Nozzle"))
            .Should().BeFalse();
    }
}
