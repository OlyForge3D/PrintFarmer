using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Modules.Printers.Services.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Printers.Tests.Services;

public class CatalogServiceTests
{
    [Fact]
    public async Task GetManufacturersAsync_DelegatesToCacheProvider()
    {
        Mock<ICatalogCacheProvider> mockCacheProvider = new Mock<ICatalogCacheProvider>();
        (IReadOnlyList<ManufacturerDto>, string?) expected = (new List<ManufacturerDto> { new ManufacturerDto(Guid.NewGuid(), "Test") }, "etag1");
        _ = mockCacheProvider.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        Mock<INormalizationEventLogger> normLogger = new Mock<INormalizationEventLogger>();
        Mock<ILogger<CatalogService>> unifiedLogging = new Mock<ILogger<CatalogService>>();
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();

        Farm.Infrastructure.Services.Catalog.CatalogService svc = new Farm.Infrastructure.Services.Catalog.CatalogService(
            mockRepo.Object,
            normLogger.Object,
            mockCacheProvider.Object,
            unifiedLogging.Object);

        (IReadOnlyList<ManufacturerDto>? list, string? etag) = await svc.GetManufacturersAsync(CancellationToken.None);

        Assert.Equal(expected.Item2, etag);
        Assert.Equal(expected.Item1, list);
        mockCacheProvider.Verify(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static CatalogService CreateService(Mock<ICatalogRepository> mockRepo)
    {
        Mock<INormalizationEventLogger> normLogger = new Mock<INormalizationEventLogger>();
        Mock<ILogger<CatalogService>> unifiedLogging = new Mock<ILogger<CatalogService>>();
        Mock<ICatalogCacheProvider> mockCacheProvider = new Mock<ICatalogCacheProvider>();
        return new CatalogService(mockRepo.Object, normLogger.Object, mockCacheProvider.Object, unifiedLogging.Object);
    }

    // Covers issue #1824's ResolveNozzleMaterialIdAsync resolution logic (via CreateNozzleModelAsync,
    // since the resolver itself is private): a recognized material name (matching a built-in
    // NozzleMaterial row) must resolve to that row's ID. Per #1826, the wire contract is now an
    // open-set string, not a closed enum - so this test deliberately uses "Vibranium", a name
    // with no matching NozzleType enum member. A regression back to serializing the enum (e.g.
    // NozzleModel.NozzleType.ToString(), which would collapse to "Unknown") would fail the
    // result.NozzleType assertion below even though every enum-backed field still resolves fine.
    [Fact]
    public async Task CreateNozzleModelAsync_ResolvesNozzleTypeToMaterialId()
    {
        Guid manufacturerId = Guid.NewGuid();
        NozzleMaterial vibranium = new NozzleMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Vibranium",
            IsHardened = true,
            DefaultMaxTemp = 300,
            IsBuiltIn = false
        };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.ManufacturerExistsAsync(manufacturerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByNameAsync("Vibranium", It.IsAny<CancellationToken>())).ReturnsAsync(vibranium);

        NozzleModelDefinition? capturedModel = null;
        _ = mockRepo.Setup(r => r.AddNozzleModelAsync(It.IsAny<NozzleModelDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<NozzleModelDefinition, CancellationToken>((m, _) =>
            {
                // Mimic the real repository's re-fetch-with-Include(NozzleMaterial) behavior so
                // this test exercises the same navigation-populated path production code takes.
                m.NozzleMaterial = vibranium;
                capturedModel = m;
            })
            .Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.GetNozzleModelByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => capturedModel);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleModelDto dto = new CreateNozzleModelDto("Vanadium", manufacturerId, NozzleType: "Vibranium");

        NozzleModelDto result = await svc.CreateNozzleModelAsync(dto, CancellationToken.None);

        Assert.NotNull(capturedModel);
        Assert.Equal(vibranium.Id, capturedModel!.NozzleMaterialId);
        Assert.Equal("Vibranium", result.NozzleType);
        mockRepo.Verify(r => r.GetNozzleMaterialByNameAsync("Vibranium", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Covers issue #1824's error path: an unrecognized material name must fail fast rather than
    // silently persisting an orphan/garbage FK.
    [Fact]
    public async Task CreateNozzleModelAsync_WithUnrecognizedNozzleType_ThrowsKeyNotFoundException()
    {
        Guid manufacturerId = Guid.NewGuid();
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.ManufacturerExistsAsync(manufacturerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((NozzleMaterial?)null);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleModelDto dto = new CreateNozzleModelDto("Mystery", manufacturerId, NozzleType: "Unobtainium");

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CreateNozzleModelAsync(dto, CancellationToken.None));
        mockRepo.Verify(r => r.AddNozzleModelAsync(It.IsAny<NozzleModelDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Per #1826, the wire contract is an open-set string, not a closed enum - so this test
    // deliberately uses "Adamantium", a name with no matching NozzleType enum member. A
    // regression back to serializing the enum would fail the result.NozzleType assertion below.
    [Fact]
    public async Task UpdateNozzleModelAsync_ChangesNozzleTypeAndUpdatesNozzleMaterialId()
    {
        Guid nozzleId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        NozzleMaterial brass = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Brass", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true };
        NozzleMaterial adamantium = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Adamantium", IsHardened = true, DefaultMaxTemp = 500, IsBuiltIn = false };

        NozzleModelDefinition existing = new NozzleModelDefinition
        {
            Id = nozzleId,
            Name = "Undertaker",
            ManufacturerId = manufacturerId,
            NozzleMaterialId = brass.Id,
            NozzleMaterial = brass
        };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleModelByIdAsync(nozzleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => existing);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByNameAsync("Adamantium", It.IsAny<CancellationToken>())).ReturnsAsync(adamantium);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Mimic the real repository's re-fetch-with-Include(NozzleMaterial): once the
                // FK is persisted, the navigation property reflects the new material too.
                existing.NozzleMaterial = existing.NozzleMaterialId == adamantium.Id ? adamantium : brass;
            })
            .Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        UpdateNozzleModelDto dto = new UpdateNozzleModelDto(NozzleType: "Adamantium");

        NozzleModelDto? result = await svc.UpdateNozzleModelAsync(nozzleId, dto, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(adamantium.Id, existing.NozzleMaterialId);
        Assert.Equal("Adamantium", result!.NozzleType);
        mockRepo.Verify(r => r.GetNozzleMaterialByNameAsync("Adamantium", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateNozzleModelAsync_WithNullNozzleType_DoesNotChangeNozzleMaterialId()
    {
        Guid nozzleId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        NozzleMaterial brass = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Brass", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true };

        NozzleModelDefinition existing = new NozzleModelDefinition
        {
            Id = nozzleId,
            Name = "Undertaker",
            ManufacturerId = manufacturerId,
            NozzleMaterialId = brass.Id,
            NozzleMaterial = brass
        };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.SetupSequence(r => r.GetNozzleModelByIdAsync(nozzleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(existing);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        UpdateNozzleModelDto dto = new UpdateNozzleModelDto(Description: "Updated description only");

        NozzleModelDto? result = await svc.UpdateNozzleModelAsync(nozzleId, dto, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(brass.Id, existing.NozzleMaterialId);
        mockRepo.Verify(r => r.GetNozzleMaterialByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Covers #1825's additive wire-contract extension: an explicit NozzleMaterialId (e.g. a custom,
    // non-enum material created through the new CRUD) must take precedence over the legacy NozzleType
    // enum field and must be resolved by direct ID lookup rather than by name.
    [Fact]
    public async Task CreateNozzleModelAsync_WithExplicitNozzleMaterialId_TakesPrecedenceOverNozzleType()
    {
        Guid manufacturerId = Guid.NewGuid();
        NozzleMaterial custom = new NozzleMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Custom Exotic Alloy",
            IsHardened = true,
            DefaultMaxTemp = 450,
            IsBuiltIn = false
        };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.ManufacturerExistsAsync(manufacturerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(custom);

        NozzleModelDefinition? capturedModel = null;
        _ = mockRepo.Setup(r => r.AddNozzleModelAsync(It.IsAny<NozzleModelDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<NozzleModelDefinition, CancellationToken>((m, _) => capturedModel = m)
            .Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.GetNozzleModelByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => capturedModel);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleModelDto dto = new CreateNozzleModelDto("Vanadium", manufacturerId, NozzleType: "Brass", NozzleMaterialId: custom.Id);

        NozzleModelDto result = await svc.CreateNozzleModelAsync(dto, CancellationToken.None);

        Assert.NotNull(capturedModel);
        Assert.Equal(custom.Id, capturedModel!.NozzleMaterialId);
        Assert.Equal(custom.Id, result.NozzleMaterialId);
        mockRepo.Verify(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>()), Times.Once);
        mockRepo.Verify(r => r.GetNozzleMaterialByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateNozzleModelAsync_WithUnrecognizedNozzleMaterialId_ThrowsKeyNotFoundException()
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid missingMaterialId = Guid.NewGuid();

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.ManufacturerExistsAsync(manufacturerId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(missingMaterialId, It.IsAny<CancellationToken>())).ReturnsAsync((NozzleMaterial?)null);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleModelDto dto = new CreateNozzleModelDto("Mystery", manufacturerId, NozzleMaterialId: missingMaterialId);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CreateNozzleModelAsync(dto, CancellationToken.None));
        mockRepo.Verify(r => r.AddNozzleModelAsync(It.IsAny<NozzleModelDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateNozzleModelAsync_WithExplicitNozzleMaterialId_TakesPrecedenceOverNozzleType()
    {
        Guid nozzleId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        NozzleMaterial brass = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Brass", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true };
        NozzleMaterial custom = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Custom Exotic Alloy", IsHardened = true, DefaultMaxTemp = 450, IsBuiltIn = false };

        NozzleModelDefinition existing = new NozzleModelDefinition
        {
            Id = nozzleId,
            Name = "Undertaker",
            ManufacturerId = manufacturerId,
            NozzleMaterialId = brass.Id,
            NozzleMaterial = brass
        };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.SetupSequence(r => r.GetNozzleModelByIdAsync(nozzleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(existing);
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(custom);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        UpdateNozzleModelDto dto = new UpdateNozzleModelDto(NozzleType: "TungstenCarbide", NozzleMaterialId: custom.Id);

        NozzleModelDto? result = await svc.UpdateNozzleModelAsync(nozzleId, dto, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(custom.Id, existing.NozzleMaterialId);
        mockRepo.Verify(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>()), Times.Once);
        mockRepo.Verify(r => r.GetNozzleMaterialByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #region Nozzle Material CRUD

    [Fact]
    public async Task GetNozzleMaterialsAsync_ReturnsMappedDtos()
    {
        NozzleMaterial brass = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Brass", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true };
        NozzleMaterial custom = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Custom", IsHardened = true, DefaultMaxTemp = 450, IsBuiltIn = false, Description = "My alloy" };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NozzleMaterial> { brass, custom });

        CatalogService svc = CreateService(mockRepo);
        IReadOnlyList<NozzleMaterialDto> result = await svc.GetNozzleMaterialsAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, m => m.Id == brass.Id && m.IsBuiltIn && !m.IsHardened);
        Assert.Contains(result, m => m.Id == custom.Id && !m.IsBuiltIn && m.IsHardened && m.Description == "My alloy");
    }

    [Fact]
    public async Task CreateNozzleMaterialAsync_CreatesNonBuiltInMaterial()
    {
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.NozzleMaterialNameExistsAsync("Custom Exotic Alloy", null, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        NozzleMaterial? added = null;
        _ = mockRepo.Setup(r => r.AddNozzleMaterialAsync(It.IsAny<NozzleMaterial>(), It.IsAny<CancellationToken>()))
            .Callback<NozzleMaterial, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleMaterialDto dto = new CreateNozzleMaterialDto("Custom Exotic Alloy", true, 450, "Notes");

        NozzleMaterialDto result = await svc.CreateNozzleMaterialAsync(dto, CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsBuiltIn);
        Assert.Equal("Custom Exotic Alloy", result.Name);
        Assert.True(result.IsHardened);
        Assert.Equal(450, result.DefaultMaxTemp);
        Assert.False(result.IsBuiltIn);
    }

    [Fact]
    public async Task CreateNozzleMaterialAsync_WithDuplicateName_ThrowsArgumentException()
    {
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.NozzleMaterialNameExistsAsync("Brass", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        CatalogService svc = CreateService(mockRepo);
        CreateNozzleMaterialDto dto = new CreateNozzleMaterialDto("Brass", false, 260, null);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => svc.CreateNozzleMaterialAsync(dto, CancellationToken.None));
        mockRepo.Verify(r => r.AddNozzleMaterialAsync(It.IsAny<NozzleMaterial>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateNozzleMaterialAsync_UpdatesFields()
    {
        NozzleMaterial material = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Custom", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = false };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(material.Id, It.IsAny<CancellationToken>())).ReturnsAsync(material);
        _ = mockRepo.Setup(r => r.NozzleMaterialNameExistsAsync("Renamed", material.Id, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        UpdateNozzleMaterialDto dto = new UpdateNozzleMaterialDto("Renamed", true, 500, "New notes");

        NozzleMaterialDto? result = await svc.UpdateNozzleMaterialAsync(material.Id, dto, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Renamed", result!.Name);
        Assert.True(result.IsHardened);
        Assert.Equal(500, result.DefaultMaxTemp);
        Assert.Equal("New notes", result.Description);
    }

    [Fact]
    public async Task UpdateNozzleMaterialAsync_WhenNotFound_ReturnsNull()
    {
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((NozzleMaterial?)null);

        CatalogService svc = CreateService(mockRepo);
        UpdateNozzleMaterialDto dto = new UpdateNozzleMaterialDto("Renamed", null, null, null);

        NozzleMaterialDto? result = await svc.UpdateNozzleMaterialAsync(Guid.NewGuid(), dto, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteNozzleMaterialAsync_WhenBuiltIn_ThrowsInvalidOperationException()
    {
        NozzleMaterial builtIn = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Brass", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = true };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(builtIn.Id, It.IsAny<CancellationToken>())).ReturnsAsync(builtIn);

        CatalogService svc = CreateService(mockRepo);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteNozzleMaterialAsync(builtIn.Id, CancellationToken.None));
        mockRepo.Verify(r => r.RemoveNozzleMaterialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteNozzleMaterialAsync_WhenInUse_ThrowsInvalidOperationException()
    {
        NozzleMaterial custom = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Custom", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = false };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(custom);
        _ = mockRepo.Setup(r => r.CountNozzleModelsByMaterialAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(2);

        CatalogService svc = CreateService(mockRepo);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteNozzleMaterialAsync(custom.Id, CancellationToken.None));
        mockRepo.Verify(r => r.RemoveNozzleMaterialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteNozzleMaterialAsync_WhenUnusedAndCustom_Deletes()
    {
        NozzleMaterial custom = new NozzleMaterial { Id = Guid.NewGuid(), Name = "Custom", IsHardened = false, DefaultMaxTemp = 260, IsBuiltIn = false };

        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(custom);
        _ = mockRepo.Setup(r => r.CountNozzleModelsByMaterialAsync(custom.Id, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _ = mockRepo.Setup(r => r.RemoveNozzleMaterialAsync(custom.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        CatalogService svc = CreateService(mockRepo);
        await svc.DeleteNozzleMaterialAsync(custom.Id, CancellationToken.None);

        mockRepo.Verify(r => r.RemoveNozzleMaterialAsync(custom.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteNozzleMaterialAsync_WhenNotFound_ThrowsKeyNotFoundException()
    {
        Mock<ICatalogRepository> mockRepo = new Mock<ICatalogRepository>();
        _ = mockRepo.Setup(r => r.GetNozzleMaterialByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((NozzleMaterial?)null);

        CatalogService svc = CreateService(mockRepo);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteNozzleMaterialAsync(Guid.NewGuid(), CancellationToken.None));
    }

    #endregion
}

