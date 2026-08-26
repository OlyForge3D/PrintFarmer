using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Verifies authoritative family persistence and collision handling.
/// </summary>
public sealed class ProfileFamilyServiceTests
{
    [Fact]
    public async Task CloneFamilyAsync_PersistsNonNullHashesAndHealthyRenderState()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection)
                .Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid modelId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        CloneProfileFamilyRequestDto request = Request(modelId);
        Mock<ICatalogServiceAdapter> catalogService = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Strict);
        _ = aliasService
            .Setup(service => service.ResolveModelAliasAsync("Farm Test", "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        _ = aliasService
            .Setup(service => service.EnsureModelAliasAsync(
                modelId,
                "Farm Test",
                "OrcaSlicer",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IProfileFamilyRenderer> renderer = new(MockBehavior.Strict);
        _ = renderer
            .Setup(service => service.Render(
                It.IsAny<Guid>(),
                request,
                It.IsAny<AllProfilesResponseDto>()))
            .Returns((Guid familyId, CloneProfileFamilyRequestDto _, AllProfilesResponseDto _) =>
                new ProfileFamilyRenderResult(
                    new ProfileFamilyBundleDto(familyId, "Farm Test", "{}", []),
                    """{"speed":100}""",
                    [new RenderedMachineVariant("Farm Test 0.6 nozzle", 0.6, "Stock 0.6 nozzle", """{"max_layer_height":["0.45"]}""")],
                    3,
                    0));

        ProfileFamilyWorkerTarget target = new("http://worker", "2.3.0");
        Mock<IProfileFamilyWorkerClient> workerClient = new(MockBehavior.Strict);
        _ = workerClient
            .Setup(service => service.GetCatalogAsync(
                "Prusa",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, new AllProfilesResponseDto()));
        _ = workerClient
            .Setup(service => service.WriteBundleAsync(
                target,
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new ProfileFamilyService(
            dbContext,
            catalogService.Object,
            aliasService.Object,
            renderer.Object,
            workerClient.Object,
            NullLogger<ProfileFamilyService>.Instance);

        CloneProfileFamilyResponseDto response =
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        MachineModelProfile family =
            await dbContext.MachineModelProfiles.SingleAsync();
        MachineProfile machine = await dbContext.MachineProfiles.SingleAsync();
        family.Hash.Should().HaveLength(64);
        machine.Hash.Should().HaveLength(64);
        family.Hash.Should().NotBe(machine.Hash);
        family.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        family.LastRenderedAt.Should().NotBeNull();
        family.RenderedForOrcaVersion.Should().Be("2.3.0");
        family.CreatedByUserId.Should().Be(userId);
        machine.SourceSystemPresetName.Should().Be("Stock 0.6 nozzle");
        response.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        aliasService.VerifyAll();
    }

    [Fact]
    public async Task CloneFamilyAsync_DuplicateGlobalName_ThrowsConflictBeforeWorkerCall()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection)
                .Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();
        Guid modelId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = Guid.NewGuid(),
            Name = "Farm Test",
            Manufacturer = "Existing",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = new string('A', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();

        Mock<IProfileFamilyWorkerClient> workerClient = new(MockBehavior.Strict);
        var service = new ProfileFamilyService(
            dbContext,
            Catalog(modelId).Object,
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict).Object,
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict).Object,
            workerClient.Object,
            NullLogger<ProfileFamilyService>.Instance);

        Func<Task> act = () =>
            service.CloneFamilyAsync(Request(modelId), Guid.NewGuid(), CancellationToken.None);

        await act.Should()
            .ThrowAsync<ProfileFamilyConflictException>()
            .WithMessage("*Farm Test*already exists*");
        workerClient.VerifyNoOtherCalls();
    }

    private static CloneProfileFamilyRequestDto Request(Guid modelId) =>
        new()
        {
            FamilyName = "Farm Test",
            TargetPrinterModelId = modelId,
            SourceManufacturer = "Prusa",
            SourceMachineModelName = "Prusa Test",
            NozzleDiameters = [0.6]
        };

    private static Mock<ICatalogServiceAdapter> Catalog(Guid modelId)
    {
        Mock<ICatalogServiceAdapter> catalog = new(MockBehavior.Strict);
        _ = catalog
            .Setup(service => service.GetModelByIdAsync(
                modelId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Target Model", "Target"));
        return catalog;
    }
}
