using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.DataManagement;

/// <summary>
/// Covers <c>SeedNozzlesAsync</c>, which had no test coverage while the symmetric backup
/// import path did.
/// <para>
/// The gap mattered: <c>NozzleModelSeedDto</c> originally had no <c>NozzleInterface</c>
/// property and <c>YamlSeedDataReader</c> is built with <c>IgnoreUnmatchedProperties()</c>,
/// so every <c>nozzleInterface:</c> key in the seed YAML was silently discarded and all
/// nozzles seeded as V6. Nothing failed, nothing logged. These tests pin the assignment in
/// <em>both</em> upsert branches so that regression cannot recur unnoticed.
/// </para>
/// </summary>
public sealed class DataSeedServiceNozzleTests
{
    private readonly AppDbContext _context;
    private readonly Mock<IYamlSeedDataReader> _reader = new();
    private readonly Mock<ILogger<DataSeedService>> _logger = new();
    private readonly DataSeedService _service;
    private readonly Guid _manufacturerId = Guid.NewGuid();

    public DataSeedServiceNozzleTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"SeedTestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _context.Manufacturers.Add(new Manufacturer { Id = _manufacturerId, Name = "Diamondback" });
        _context.SaveChanges();

        // Only nozzles matter here; the other component readers return empty lists.
        _ = _reader.Setup(r => r.ReadHotendsAsync()).ReturnsAsync([]);
        _ = _reader.Setup(r => r.ReadExtrudersAsync()).ReturnsAsync([]);
        _ = _reader.Setup(r => r.ReadToolheadsAsync()).ReturnsAsync([]);

        _service = new DataSeedService(_context, _reader.Object, _logger.Object);
    }

    private async Task SeedAsync(params NozzleModelSeedDto[] nozzles)
    {
        _ = _reader.Setup(r => r.ReadNozzlesAsync()).ReturnsAsync([.. nozzles]);
        await _service.SeedComponentModelsAsync();
    }

    private static NozzleModelSeedDto Dto(
        string name = "Diamondback Volcano",
        string nozzleType = "Diamond",
        string? hardnessOverride = null,
        string? nozzleInterface = null) => new()
        {
            Name = name,
            Manufacturer = "Diamondback",
            Diameter = 0.4,
            MaxTemp = 550,
            NozzleType = nozzleType,
            HardnessOverride = hardnessOverride,
            NozzleInterface = nozzleInterface,
        };

    private Task<NozzleModelDefinition?> FindAsync(string name) =>
        _context.NozzleModelDefinitions.FirstOrDefaultAsync(n => n.Name == name);

    private bool WarningLogged() => _logger.Invocations.Any(i =>
        i.Method.Name == nameof(ILogger.Log) &&
        i.Arguments.Count > 0 &&
        i.Arguments[0] is LogLevel.Warning);

    [Fact]
    public async Task Seed_AssignsNozzleInterface_OnInsert()
    {
        await SeedAsync(Dto(nozzleInterface: "Volcano"));

        NozzleModelDefinition? seeded = await FindAsync("Diamondback Volcano");
        seeded.Should().NotBeNull();
        seeded!.NozzleInterface.Should().Be(
            NozzleInterfaceType.Volcano,
            "a dropped nozzleInterface silently catalogues every nozzle as V6");
    }

    [Fact]
    public async Task Seed_AssignsNozzleInterface_OnUpdate()
    {
        // The regression this guards: an update branch that forgets the assignment leaves
        // already-seeded rows stale forever, since re-seed is the only correction path.
        _context.NozzleModelDefinitions.Add(new NozzleModelDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Diamondback Volcano",
            ManufacturerId = _manufacturerId,
            NozzleInterface = NozzleInterfaceType.V6,
            NozzleType = NozzleType.Brass,
        });
        await _context.SaveChangesAsync();

        await SeedAsync(Dto(nozzleInterface: "Volcano"));

        // Assert exactly one row so this stays a genuine update-branch test: if the upsert
        // lookup ever regressed into inserting a duplicate, the assertion below would
        // otherwise depend on which row FirstOrDefaultAsync happened to return.
        (await _context.NozzleModelDefinitions.CountAsync()).Should().Be(1);

        NozzleModelDefinition? seeded = await FindAsync("Diamondback Volcano");
        seeded!.NozzleInterface.Should().Be(NozzleInterfaceType.Volcano);
        seeded.NozzleType.Should().Be(NozzleType.Diamond);
    }

    [Fact]
    public async Task Seed_AbsentNozzleInterface_DefaultsToV6()
    {
        await SeedAsync(Dto(nozzleInterface: null));

        (await FindAsync("Diamondback Volcano"))!.NozzleInterface.Should().Be(NozzleInterfaceType.V6);
    }

    [Fact]
    public async Task Seed_AssignsHardnessOverride_OnInsertAndUpdate()
    {
        await SeedAsync(Dto(hardnessOverride: "NotHardened"));
        NozzleModelDefinition? first = await FindAsync("Diamondback Volcano");
        first!.HardnessOverride.Should().Be(NozzleHardnessOverride.NotHardened);
        first.IsHardened.Should().BeFalse("the pin must beat the Diamond material default");

        await SeedAsync(Dto(hardnessOverride: "Hardened"));
        (await FindAsync("Diamondback Volcano"))!.HardnessOverride
            .Should().Be(NozzleHardnessOverride.Hardened);
    }

    [Fact]
    public async Task Seed_AbsentHardnessOverride_DefaultsToAutoWithoutWarning()
    {
        await SeedAsync(Dto(hardnessOverride: null));

        (await FindAsync("Diamondback Volcano"))!.HardnessOverride
            .Should().Be(NozzleHardnessOverride.Auto);
        WarningLogged().Should().BeFalse("an absent optional field is not a problem worth warning about");
    }

    [Theory]
    [InlineData("NotHardend")]      // typo
    [InlineData("Unobtanium")]      // unknown name
    [InlineData("42")]              // undefined ordinal
    [InlineData("1")]               // *defined* ordinal — contract is names, not ordinals
    [InlineData("+ 5")]             // sign detached from digits; must not slip past the numeric guard
    public async Task Seed_UnrecognizedHardnessOverride_WarnsAndFallsBackToAuto(string rawValue)
    {
        await SeedAsync(Dto(hardnessOverride: rawValue));

        NozzleModelDefinition? seeded = await FindAsync("Diamondback Volcano");
        seeded!.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        Enum.IsDefined(seeded.HardnessOverride).Should().BeTrue();
        WarningLogged().Should().BeTrue("a silent fallback on a dispatch-safety field is what this guards");
    }

    [Theory]
    [InlineData("Unobtanium")]
    [InlineData("5")]               // defined ordinal for NozzleType.Diamond
    [InlineData("+ 5")]
    public async Task Seed_UnrecognizedNozzleType_WarnsAndFallsBackToBrass(string rawValue)
    {
        await SeedAsync(Dto(nozzleType: rawValue));

        NozzleModelDefinition? seeded = await FindAsync("Diamondback Volcano");
        seeded!.NozzleType.Should().Be(NozzleType.Brass);
        WarningLogged().Should().BeTrue();
    }

    [Fact]
    public async Task Seed_MaterialNameIsCaseInsensitiveAndSpaceTolerant()
    {
        await SeedAsync(Dto(nozzleType: "hardened steel"));

        (await FindAsync("Diamondback Volcano"))!.NozzleType.Should().Be(NozzleType.HardenedSteel);
    }

    [Fact]
    public async Task Seed_UnknownManufacturer_SkipsRow()
    {
        NozzleModelSeedDto dto = Dto();
        dto.Manufacturer = "Nonexistent";

        await SeedAsync(dto);

        (await FindAsync("Diamondback Volcano")).Should().BeNull();
        WarningLogged().Should().BeTrue();
    }
}
