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
        request.FamilyName = " Farm Test ";
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
                It.Is<CloneProfileFamilyRequestDto>(candidate => candidate.FamilyName == "Farm Test"),
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
        request.FamilyName.Should().Be(" Farm Test ");
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

    [Fact]
    public async Task CloneFamilyAsync_WorkerFailure_RetryReusesFailedFamilyAndBundle()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases = Aliases(modelId);
        Mock<IProfileFamilyRenderer> renderer = Renderer();
        Mock<IProfileFamilyWorkerClient> worker =
            Worker(new HttpRequestException("worker unavailable"));
        var service = CreateService(dbContext, catalog, aliases, renderer, worker);
        CloneProfileFamilyRequestDto request = Request(modelId);

        Func<Task> firstAttempt = async () =>
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<HttpRequestException>();
        MachineModelProfile failedFamily = await dbContext.MachineModelProfiles
            .AsNoTracking()
            .SingleAsync();
        failedFamily.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);

        CloneProfileFamilyResponseDto response =
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        response.FamilyId.Should().Be(failedFamily.Id);
        response.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(1);
        WorkerBundleFamilyIds(worker).Should().Equal(failedFamily.Id, failedFamily.Id);
        aliases.Verify(
            service => service.EnsureModelAliasAsync(
                modelId,
                "Farm Test",
                "OrcaSlicer",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CloneFamilyAsync_WorkerCancellation_MarksFailedAndRetryReusesFamilyAndBundle()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases = Aliases(modelId);
        Mock<IProfileFamilyRenderer> renderer = Renderer();
        Mock<IProfileFamilyWorkerClient> worker =
            Worker(new TaskCanceledException("worker request timed out"));
        var service = CreateService(dbContext, catalog, aliases, renderer, worker);
        CloneProfileFamilyRequestDto request = Request(modelId);

        Func<Task> firstAttempt = async () =>
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<TaskCanceledException>();
        MachineModelProfile failedFamily = await dbContext.MachineModelProfiles
            .AsNoTracking()
            .SingleAsync();
        failedFamily.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);

        CloneProfileFamilyResponseDto response =
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        response.FamilyId.Should().Be(failedFamily.Id);
        response.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        WorkerBundleFamilyIds(worker).Should().Equal(failedFamily.Id, failedFamily.Id);
    }

    [Fact]
    public async Task CloneFamilyAsync_PendingFamily_RetryReusesFamilyAndBundle()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = familyId,
            Name = "Farm Test",
            Manufacturer = "Custom",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            Hash = new string('A', 64),
            IsSystem = false,
            RenderStatus = ProfileFamilyRenderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases = Aliases(modelId);
        Mock<IProfileFamilyRenderer> renderer = Renderer();
        Mock<IProfileFamilyWorkerClient> worker = Worker();
        var service = CreateService(dbContext, catalog, aliases, renderer, worker);

        CloneProfileFamilyResponseDto response =
            await service.CloneFamilyAsync(Request(modelId), userId, CancellationToken.None);

        response.FamilyId.Should().Be(familyId);
        response.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        WorkerBundleFamilyIds(worker).Should().Equal(familyId);
    }

    [Fact]
    public async Task CloneFamilyAsync_AliasFailure_RetryReusesFailedFamilyAndBundle()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases =
            Aliases(modelId, new DbUpdateException("transient alias write failure"));
        Mock<IProfileFamilyRenderer> renderer = Renderer();
        Mock<IProfileFamilyWorkerClient> worker = Worker();
        var service = CreateService(dbContext, catalog, aliases, renderer, worker);
        CloneProfileFamilyRequestDto request = Request(modelId);

        Func<Task> firstAttempt = async () =>
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        await firstAttempt.Should().ThrowAsync<DbUpdateException>();
        MachineModelProfile failedFamily = await dbContext.MachineModelProfiles
            .AsNoTracking()
            .SingleAsync();
        failedFamily.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);

        CloneProfileFamilyResponseDto response =
            await service.CloneFamilyAsync(request, userId, CancellationToken.None);

        response.FamilyId.Should().Be(failedFamily.Id);
        response.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(1);
        WorkerBundleFamilyIds(worker).Should().Equal(failedFamily.Id, failedFamily.Id);
        catalog.Verify(
            service => service.InvalidateModelAliasesAsync(
                modelId,
                It.IsAny<CancellationToken>()),
            Times.Once);
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
        _ = catalog
            .Setup(service => service.InvalidateModelAliasesAsync(
                modelId,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return catalog;
    }

    private static SlicerDbContext CreateContext(SqliteConnection connection)
    {
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection)
                .Options;
        var dbContext = new SlicerDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static Mock<IPrinterModelAliasService> Aliases(
        Guid modelId,
        Exception? firstEnsureFailure = null)
    {
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases
            .Setup(service => service.ResolveModelAliasAsync("Farm Test", "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        if (firstEnsureFailure is null)
        {
            _ = aliases
                .Setup(service => service.EnsureModelAliasAsync(
                    modelId,
                    "Farm Test",
                    "OrcaSlicer",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            _ = aliases
                .SetupSequence(service => service.EnsureModelAliasAsync(
                    modelId,
                    "Farm Test",
                    "OrcaSlicer",
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(firstEnsureFailure)
                .Returns(Task.CompletedTask);
        }

        return aliases;
    }

    private static Mock<IProfileFamilyRenderer> Renderer()
    {
        Mock<IProfileFamilyRenderer> renderer = new(MockBehavior.Strict);
        _ = renderer
            .Setup(service => service.Render(
                It.IsAny<Guid>(),
                It.IsAny<CloneProfileFamilyRequestDto>(),
                It.IsAny<AllProfilesResponseDto>()))
            .Returns((Guid familyId, CloneProfileFamilyRequestDto _, AllProfilesResponseDto _) =>
                new ProfileFamilyRenderResult(
                    new ProfileFamilyBundleDto(familyId, "Farm Test", "{}", []),
                    """{"speed":100}""",
                    [new RenderedMachineVariant(
                        "Farm Test 0.6 nozzle",
                        0.6,
                        "Stock 0.6 nozzle",
                        """{"max_layer_height":["0.45"]}""")],
                    3,
                    0));
        return renderer;
    }

    private static Mock<IProfileFamilyWorkerClient> Worker(Exception? firstWriteFailure = null)
    {
        var target = new ProfileFamilyWorkerTarget("http://worker", "2.3.0");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(
                "Prusa",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, new AllProfilesResponseDto()));
        if (firstWriteFailure is null)
        {
            _ = worker
                .Setup(service => service.WriteBundleAsync(
                    target,
                    It.IsAny<ProfileFamilyBundleDto>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            _ = worker
                .SetupSequence(service => service.WriteBundleAsync(
                    target,
                    It.IsAny<ProfileFamilyBundleDto>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(firstWriteFailure)
                .Returns(Task.CompletedTask);
        }

        return worker;
    }

    private static ProfileFamilyService CreateService(
        SlicerDbContext dbContext,
        Mock<ICatalogServiceAdapter> catalog,
        Mock<IPrinterModelAliasService> aliases,
        Mock<IProfileFamilyRenderer> renderer,
        Mock<IProfileFamilyWorkerClient> worker)
    {
        return new ProfileFamilyService(
            dbContext,
            catalog.Object,
            aliases.Object,
            renderer.Object,
            worker.Object,
            NullLogger<ProfileFamilyService>.Instance);
    }

    private static IEnumerable<Guid> WorkerBundleFamilyIds(
        Mock<IProfileFamilyWorkerClient> worker)
    {
        return worker.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IProfileFamilyWorkerClient.WriteBundleAsync))
            .Select(invocation => ((ProfileFamilyBundleDto)invocation.Arguments[1]).FamilyId);
    }
}
