using System.Linq;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Covers how <see cref="CatalogService"/> threads <see cref="NozzleHardnessOverride"/>
/// through create and update, including the PATCH semantics that make "clear the pin"
/// expressible: <c>null</c> means leave unchanged, <c>Auto</c> means reset to material-derived.
/// </summary>
public sealed class CatalogServiceNozzleHardnessTests
{
    private readonly Mock<ICatalogRepository> _repo = new();
    private readonly CatalogService _service;
    private readonly List<NozzleModelDefinition> _stored = [];

    /// <summary>
    /// Materials keyed by <see cref="NozzleType"/> name, standing in for the built-in
    /// <see cref="NozzleMaterial"/> catalog rows that <see cref="CatalogService"/> resolves
    /// against (see #1824). Only the materials these tests exercise are stubbed.
    /// </summary>
    private readonly Dictionary<string, NozzleMaterial> _materials = new()
    {
        [nameof(NozzleType.Brass)] = new NozzleMaterial
        {
            Id = Guid.NewGuid(),
            Name = nameof(NozzleType.Brass),
            IsHardened = false,
            DefaultMaxTemp = 300,
            IsBuiltIn = true
        },
        [nameof(NozzleType.Diamond)] = new NozzleMaterial
        {
            Id = Guid.NewGuid(),
            Name = nameof(NozzleType.Diamond),
            IsHardened = true,
            DefaultMaxTemp = 500,
            IsBuiltIn = true
        },
    };

    public CatalogServiceNozzleHardnessTests()
    {
        _ = _repo.Setup(r => r.ManufacturerExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _ = _repo.Setup(r => r.GetNozzleMaterialByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => _materials.GetValueOrDefault(name));
        _ = _repo.Setup(r => r.AddNozzleModelAsync(It.IsAny<NozzleModelDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<NozzleModelDefinition, CancellationToken>((m, _) => _stored.Add(m))
            .Returns(Task.CompletedTask);
        // Mirrors EfCatalogRepository.GetNozzleModelByIdAsync's `.Include(n => n.NozzleMaterial)`
        // by re-resolving the navigation from the model's current FK on every fetch, so a
        // material change made mid-update (before the service's re-fetch) is reflected.
        _ = _repo.Setup(r => r.GetNozzleModelByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
            {
                NozzleModelDefinition? found = _stored.Find(m => m.Id == id);
                if (found is not null)
                {
                    found.NozzleMaterial = _materials.Values.FirstOrDefault(mat => mat.Id == found.NozzleMaterialId);
                }
                return found;
            });
        _ = _repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _service = new CatalogService(
            _repo.Object,
            Mock.Of<INormalizationEventLogger>(),
            Mock.Of<ICatalogCacheProvider>(),
            Mock.Of<ILogger<CatalogService>>());
    }

    private Task<NozzleModelDto> CreateAsync(
        NozzleType nozzleType, NozzleHardnessOverride hardnessOverride) =>
        _service.CreateNozzleModelAsync(
            new CreateNozzleModelDto(
                Name: "Test Nozzle",
                ManufacturerId: Guid.NewGuid(),
                Diameter: 0.4,
                MaxTemp: 500,
                NozzleType: nozzleType,
                HardnessOverride: hardnessOverride),
            CancellationToken.None);

    [Fact]
    public async Task Create_DefaultsToAuto_AndDerivesHardnessFromMaterial()
    {
        NozzleModelDto created = await _service.CreateNozzleModelAsync(
            new CreateNozzleModelDto(
                Name: "Diamondback V6",
                ManufacturerId: Guid.NewGuid(),
                NozzleType: NozzleType.Diamond),
            CancellationToken.None);

        created.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        created.IsHardened.Should().BeTrue();
    }

    [Fact]
    public async Task Create_PersistsPinnedHardened_OnSoftMaterial()
    {
        NozzleModelDto created = await CreateAsync(NozzleType.Brass, NozzleHardnessOverride.Hardened);

        created.HardnessOverride.Should().Be(NozzleHardnessOverride.Hardened);
        created.IsHardened.Should().BeTrue();
        _stored.Single().HardnessOverride.Should().Be(NozzleHardnessOverride.Hardened);
    }

    [Fact]
    public async Task Update_NullHardnessOverride_LeavesExistingPinUnchanged()
    {
        NozzleModelDto created = await CreateAsync(NozzleType.Brass, NozzleHardnessOverride.Hardened);

        NozzleModelDto? updated = await _service.UpdateNozzleModelAsync(
            created.Id,
            new UpdateNozzleModelDto(Name: "Renamed"),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.HardnessOverride.Should().Be(
            NozzleHardnessOverride.Hardened, "null means 'leave unchanged' in this PATCH DTO");
        updated.IsHardened.Should().BeTrue();
    }

    [Fact]
    public async Task Update_AutoHardnessOverride_ClearsThePin()
    {
        NozzleModelDto created = await CreateAsync(NozzleType.Brass, NozzleHardnessOverride.Hardened);

        NozzleModelDto? updated = await _service.UpdateNozzleModelAsync(
            created.Id,
            new UpdateNozzleModelDto(HardnessOverride: NozzleHardnessOverride.Auto),
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.HardnessOverride.Should().Be(NozzleHardnessOverride.Auto);
        updated.IsHardened.Should().BeFalse("clearing the pin falls back to Brass being soft");
    }

    [Fact]
    public async Task Update_PinNotHardened_OverridesHardMaterial()
    {
        NozzleModelDto created = await CreateAsync(NozzleType.Diamond, NozzleHardnessOverride.Auto);
        created.IsHardened.Should().BeTrue();

        NozzleModelDto? updated = await _service.UpdateNozzleModelAsync(
            created.Id,
            new UpdateNozzleModelDto(HardnessOverride: NozzleHardnessOverride.NotHardened),
            CancellationToken.None);

        updated!.IsHardened.Should().BeFalse();
    }

    [Fact]
    public async Task Update_MaterialChangeUnderAuto_RetracksHardness()
    {
        NozzleModelDto created = await CreateAsync(NozzleType.Brass, NozzleHardnessOverride.Auto);
        created.IsHardened.Should().BeFalse();

        NozzleModelDto? updated = await _service.UpdateNozzleModelAsync(
            created.Id,
            new UpdateNozzleModelDto(NozzleType: NozzleType.Diamond),
            CancellationToken.None);

        updated!.IsHardened.Should().BeTrue("Auto keeps hardness following the material");
    }
}
