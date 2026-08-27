using System.Security.Cryptography;
using System.Text;
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
    public async Task CloneFamilyAsync_HashCollidesWithExistingFamily_ThrowsHashConflictNamingExistingFamily()
    {
        // #2080 (N-INT-1 / finding 3): a Hash collision on IX_MachineModelProfiles_Hash must
        // surface as ProfileFamilyHashConflictException, not a raw DbUpdateException/500.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);

        Guid modelId = Guid.NewGuid();
        CloneProfileFamilyRequestDto request = Request(modelId);
        request.FamilyName = "Family Two";

        string collidingHash = ComputeExpectedFamilyHash(
            request.FamilyName,
            request.SourceManufacturer,
            request.SourceMachineModelName,
            """{"speed":100}""");
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = Guid.NewGuid(),
            Name = "Existing Family",
            Manufacturer = "Existing",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = collidingHash,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();

        Mock<IProfileFamilyWorkerClient> workerClient = Worker();
        var service = CreateService(
            dbContext,
            Catalog(modelId),
            Aliases(modelId, slicerModelName: request.FamilyName),
            Renderer(),
            workerClient);

        Func<Task> act = () =>
            service.CloneFamilyAsync(request, Guid.NewGuid(), CancellationToken.None);

        await act.Should()
            .ThrowAsync<ProfileFamilyHashConflictException>()
            .WithMessage("*Existing Family*");
        workerClient.Verify(
            client => client.GetCatalogAsync("Prusa", null, It.IsAny<CancellationToken>()),
            Times.Once);
        workerClient.VerifyNoOtherCalls();
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CloneFamilyAsync_HashCollidesWithExistingMachineProfile_ThrowsHashConflictNamingExistingMachineProfile()
    {
        // #2080 (N-INT-1 / finding 3, review gap raised by Hicks): the family-hash-collision
        // catch clause was already covered, but the sibling IX_MachineProfiles_Hash catch
        // clause (a collision on a per-variant machine profile, not the family row itself) had
        // no regression test at all -- this proves it also surfaces as
        // ProfileFamilyHashConflictException, not a raw DbUpdateException/500.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);

        Guid modelId = Guid.NewGuid();
        CloneProfileFamilyRequestDto request = Request(modelId);
        request.FamilyName = "Family Four";

        // This family's own hash must NOT collide with anything, so the family-hash catch
        // clause never fires and the machine-profile-hash catch clause is exercised instead.
        string familyHash = ComputeExpectedFamilyHash(
            request.FamilyName,
            request.SourceManufacturer,
            request.SourceMachineModelName,
            """{"speed":100}""");
        string collidingMachineHash = ComputeExpectedMachineProfileHash(
            familyHash,
            "Stock 0.6 nozzle",
            """{"max_layer_height":["0.45"]}""");

        Guid unrelatedFamilyId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = unrelatedFamilyId,
            Name = "Unrelated Family",
            Manufacturer = "Existing",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = new string('B', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        dbContext.MachineProfiles.Add(new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = "Unrelated Variant",
            Manufacturer = "Existing",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            MachineModelProfileId = unrelatedFamilyId,
            Hash = collidingMachineHash,
            SourceSystemPresetName = "Stock 0.6 nozzle",
            OverridesJson = """{"max_layer_height":["0.45"]}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();

        Mock<IProfileFamilyWorkerClient> workerClient = Worker();
        var service = CreateService(
            dbContext,
            Catalog(modelId),
            Aliases(modelId, slicerModelName: request.FamilyName),
            Renderer(),
            workerClient);

        Func<Task> act = () =>
            service.CloneFamilyAsync(request, Guid.NewGuid(), CancellationToken.None);

        await act.Should()
            .ThrowAsync<ProfileFamilyHashConflictException>()
            .WithMessage("*Stock 0.6 nozzle*");
        workerClient.Verify(
            client => client.GetCatalogAsync("Prusa", null, It.IsAny<CancellationToken>()),
            Times.Once);
        workerClient.VerifyNoOtherCalls();
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(
            1,
            "the new family row must be rolled back along with the failed machine profile insert");
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CloneFamilyAsync_TwoContextsRaceWithCaseVariantNames_DatabaseEnforcesConflict()
    {
        string databaseName = $"ProfileFamilyConcurrency{Guid.NewGuid():N}";
        string connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using SqliteConnection keeper = new(connectionString);
        await keeper.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using SlicerDbContext firstContext = new(options);
        await using SlicerDbContext secondContext = new(options);
        await firstContext.Database.EnsureCreatedAsync();

        Guid modelId = Guid.NewGuid();
        var target = new ProfileFamilyWorkerTarget("http://worker", "2.3.0");
        var secondRequestPassedPrecheck =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestCommitted =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Mock<IProfileFamilyWorkerClient> firstWorker = new(MockBehavior.Strict);
        _ = firstWorker
            .Setup(service => service.GetCatalogAsync(
                "Prusa",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, CancellationToken ct) =>
            {
                _ = await secondRequestPassedPrecheck.Task.WaitAsync(ct);
                return (target, new AllProfilesResponseDto());
            });
        _ = firstWorker
            .Setup(service => service.WriteBundleAsync(
                target,
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IProfileFamilyWorkerClient> secondWorker = new(MockBehavior.Strict);
        _ = secondWorker
            .Setup(service => service.GetCatalogAsync(
                "Prusa",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, CancellationToken ct) =>
            {
                _ = secondRequestPassedPrecheck.TrySetResult(true);
                _ = await firstRequestCommitted.Task.WaitAsync(ct);
                return (target, new AllProfilesResponseDto());
            });

        Mock<IPrinterModelAliasService> secondAliases = new(MockBehavior.Strict);
        _ = secondAliases
            .Setup(service => service.ResolveModelAliasAsync("mîcron 180", "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        CloneProfileFamilyRequestDto firstRequest = Request(modelId);
        firstRequest.FamilyName = "Mîcron 180";
        var firstService = CreateService(
            firstContext,
            Catalog(modelId),
            Aliases(modelId, slicerModelName: firstRequest.FamilyName),
            Renderer(),
            firstWorker);
        var secondService = CreateService(
            secondContext,
            Catalog(modelId),
            secondAliases,
            Renderer(),
            secondWorker);
        CloneProfileFamilyRequestDto secondRequest = Request(modelId);
        secondRequest.FamilyName = "mîcron 180";

        Task<CloneProfileFamilyResponseDto> firstClone =
            firstService.CloneFamilyAsync(firstRequest, Guid.NewGuid(), CancellationToken.None);
        Task<CloneProfileFamilyResponseDto> secondClone =
            secondService.CloneFamilyAsync(secondRequest, Guid.NewGuid(), CancellationToken.None);
        try
        {
            CloneProfileFamilyResponseDto firstResponse = await firstClone;
            firstResponse.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        }
        finally
        {
            _ = firstRequestCommitted.TrySetResult(true);
        }

        Func<Task> act = async () => _ = await secondClone;

        await act.Should()
            .ThrowAsync<ProfileFamilyConflictException>()
            .WithMessage("*mîcron 180*already exists*");
        await using SlicerDbContext verificationContext = new(options);
        (await verificationContext.MachineModelProfiles.CountAsync())
            .Should().Be(1);
        (await verificationContext.MachineModelProfiles
                .Select(profile => profile.NameNormalized)
                .SingleAsync())
            .Should().Be("MÎCRON 180");
        (await verificationContext.MachineModelProfiles
                .CountAsync(profile => profile.Name == "mîcron 180"))
            .Should().Be(0, "SQLite's raw text equality is case-sensitive");
    }

    [Fact]
    public async Task NormalizeMachineModelProfileNamesAsync_LegacyNonAsciiName_UsesCSharpNormalization()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = Guid.NewGuid(),
            Name = " Mîcron 180 ",
            Manufacturer = "Existing",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = new string('A', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        _ = await dbContext.Database.ExecuteSqlRawAsync(
            """UPDATE "MachineModelProfiles" SET "NameNormalized" = "Name";""");
        dbContext.ChangeTracker.Clear();

        await dbContext.NormalizeMachineModelProfileNamesAsync(CancellationToken.None);

        (await dbContext.MachineModelProfiles
                .Select(profile => profile.NameNormalized)
                .SingleAsync())
            .Should().Be("MÎCRON 180");
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
        Exception? firstEnsureFailure = null,
        string slicerModelName = "Farm Test")
    {
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases
            .Setup(service => service.ResolveModelAliasAsync(slicerModelName, "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        if (firstEnsureFailure is null)
        {
            _ = aliases
                .Setup(service => service.EnsureModelAliasAsync(
                    modelId,
                    slicerModelName,
                    "OrcaSlicer",
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            _ = aliases
                .SetupSequence(service => service.EnsureModelAliasAsync(
                    modelId,
                    slicerModelName,
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

    /// <summary>
    /// Replicates <c>ProfileFamilyService.ComputeHash</c>'s family-hash formula (#2080) so this
    /// test can pre-insert a colliding row without reflecting into the private implementation.
    /// </summary>
    private static string ComputeExpectedFamilyHash(
        string familyName,
        string manufacturer,
        string modelName,
        string overridesJson)
    {
        string input = string.Join(
            '\n',
            familyName,
            $"{manufacturer.Trim()}/{modelName.Trim()}",
            overridesJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// Replicates <c>ProfileFamilyService.ComputeHash</c>'s machine-variant-hash formula
    /// (#2080) so this test can pre-insert a colliding <see cref="MachineProfile"/> row
    /// without reflecting into the private implementation.
    /// </summary>
    private static string ComputeExpectedMachineProfileHash(
        string familyHash,
        string sourceSystemPresetName,
        string overridesJson)
    {
        string input = string.Join('\n', familyHash, sourceSystemPresetName, overridesJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
