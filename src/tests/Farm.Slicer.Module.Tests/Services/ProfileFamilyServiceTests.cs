using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Repositories;
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
            PrinterRefs().Object,
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
            PrinterRefs().Object,
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

    [Fact]
    public async Task ListFamiliesAsync_ReturnsOnlyCustomOrcaFamilies_ExcludingSystemAndOtherSlicers()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        _ = SeedHealthyFamily(dbContext, modelId, name: "Custom Family");
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = Guid.NewGuid(),
            Name = "Stock Model",
            Manufacturer = "Prusa",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = new string('C', 64),
            IsSystem = true,
            RenderStatus = ProfileFamilyRenderStatus.NotApplicable,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = Guid.NewGuid(),
            Name = "Prusa Family",
            Manufacturer = "Custom",
            SlicerType = SlicerType.PrusaSlicer,
            Hash = new string('D', 64),
            IsSystem = false,
            RenderStatus = ProfileFamilyRenderStatus.Healthy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), Aliases(modelId), Renderer(), Worker());

        IReadOnlyList<ProfileFamilySummaryDto> families =
            await service.ListFamiliesAsync(null, CancellationToken.None);

        _ = families.Should().ContainSingle();
        ProfileFamilySummaryDto only = families[0];
        only.FamilyName.Should().Be("Custom Family");
        only.TargetPrinterModelId.Should().Be(modelId);
        only.SourceMachineModelName.Should().Be("Prusa Test");
        only.SourceManufacturer.Should().BeNull("manufacturer is not persisted and is not recoverable");
        only.ProcessProfileCount.Should().BeNull("derived counts are not persisted post-render");
        only.FilamentProfileCount.Should().BeNull();
        _ = only.Variants.Should().ContainSingle();
        only.Variants[0].NozzleDiameter.Should().Be(0.4);
        only.Variants[0].SourceSystemPresetName.Should().Be("Prusa Test 0.4 nozzle");
    }

    [Fact]
    public async Task ListFamiliesAsync_FiltersByRenderStatus()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        _ = SeedHealthyFamily(dbContext, modelId, name: "Healthy One");
        _ = SeedHealthyFamily(
            dbContext,
            Guid.NewGuid(),
            name: "Failed One",
            status: ProfileFamilyRenderStatus.Failed);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), Aliases(modelId), Renderer(), Worker());

        IReadOnlyList<ProfileFamilySummaryDto> failed =
            await service.ListFamiliesAsync(ProfileFamilyRenderStatus.Failed, CancellationToken.None);

        _ = failed.Should().ContainSingle();
        failed[0].FamilyName.Should().Be("Failed One");
        failed[0].RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
    }

    [Fact]
    public async Task GetFamilyAsync_UnknownId_ThrowsNotFound()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), Aliases(modelId), Renderer(), Worker());

        Func<Task> act = () => service.GetFamilyAsync(Guid.NewGuid(), CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyNotFoundException>();
    }

    [Fact]
    public async Task GetFamilyAsync_SystemRow_ThrowsNotFound()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid stockId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = stockId,
            Name = "Stock Model",
            Manufacturer = "Prusa",
            SlicerType = SlicerType.OrcaSlicer,
            Hash = new string('C', 64),
            IsSystem = true,
            RenderStatus = ProfileFamilyRenderStatus.NotApplicable,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), Aliases(modelId), Renderer(), Worker());

        Func<Task> act = () => service.GetFamilyAsync(stockId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyNotFoundException>();
    }

    [Fact]
    public async Task DeleteFamilyAsync_RemovesFamilyVariantsBundleAndAlias()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases = DeleteAliases(modelId);
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext, catalog, aliases, Renderer(), worker);

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(0);
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(0);
        worker.Verify(
            client => client.DeleteBundleAsync(null, familyId, It.IsAny<CancellationToken>()),
            Times.Once,
            "delete must target any fresh online worker (null version), never the render-time version, "
            + "so a Stale family whose engine was upgraded in place can still be deleted (C1)");
        aliases.Verify(
            service => service.RemoveModelAliasAsync(
                modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once);
        catalog.Verify(
            service => service.InvalidateModelAliasesAsync(modelId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteFamilyAsync_UnknownId_ThrowsNotFound_WithoutWorkerCall()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), worker);

        Func<Task> act = () => service.DeleteFamilyAsync(Guid.NewGuid(), force: false, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyNotFoundException>();
        worker.Verify(
            client => client.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteFamilyAsync_NonTerminalSliceJob_ThrowsInUse_BeforeWorkerCall()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        Guid jobId = Guid.NewGuid();
        dbContext.SliceJobs.Add(new SliceJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            MachineProfileId = variantId,
            Status = SliceJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), worker);

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await act.Should().ThrowAsync<ProfileFamilyInUseException>())
            .Which.Message.Should().Contain(jobId.ToString());
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        worker.Verify(
            client => client.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteFamilyAsync_TerminalSliceJob_DoesNotBlockDeletion()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        dbContext.SliceJobs.Add(new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            MachineProfileId = variantId,
            Status = SliceJobStatus.Completed,
            CreatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), DeleteWorker());

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteFamilyAsync_PrinterReference_ThrowsInUse_BeforeWorkerCall()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        var blockingPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Bench Printer",
            TemplateMachineProfileId = variantId
        };
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext,
            Catalog(modelId),
            DeleteAliases(modelId),
            Renderer(),
            worker,
            PrinterRefs(blockingPrinter));

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await act.Should().ThrowAsync<ProfileFamilyInUseException>())
            .Which.Message.Should().Contain("Bench Printer");
        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(1);
        worker.Verify(
            client => client.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteFamilyAsync_WorkerDeleteFails_AbortsBeforeDbAndAliasMutation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        Mock<IProfileFamilyWorkerClient> worker =
            DeleteWorker(new HttpRequestException("worker unavailable"));
        ProfileFamilyService service = CreateService(
            dbContext, catalog, aliases, Renderer(), worker);

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(1, "the family must remain listed when the worker bundle delete fails");
        (await dbContext.MachineProfiles.CountAsync()).Should().Be(1);
        aliases.Verify(
            service => service.RemoveModelAliasAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        catalog.Verify(
            service => service.InvalidateModelAliasesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EditFamilyAsync_OverridesChange_ReRendersAndUpdatesRenderedVersion()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        Mock<IProfileFamilyWorkerClient> worker = EditWorker();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        EditProfileFamilyRequestDto request = new()
        {
            FamilyOverrides = Overrides("""{"printable_height":"250"}""")
        };

        ProfileFamilySummaryDto result = await service.EditFamilyAsync(familyId, request, CancellationToken.None);

        result.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        result.RenderedForOrcaVersion.Should().Be("2.5.0");
        MachineModelProfile persisted = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(family => family.Id == familyId);
        persisted.FamilyOverridesJson.Should().Contain("printable_height");
        (await dbContext.MachineProfiles.AsNoTracking()
            .SingleAsync(variant => variant.MachineModelProfileId == familyId)).Id
            .Should().Be(variantId, "an overrides-only edit preserves the surviving variant id");
    }

    [Fact]
    public async Task EditFamilyAsync_Rename_MovesAliasAndPreservesVariantId()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        Mock<IProfileFamilyWorkerClient> worker = EditWorker();
        Mock<IPrinterModelAliasService> aliases = EditAliases(modelId, "Renamed", renameFrom: "Farm Test");
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), aliases, EchoRenderer(), worker);

        ProfileFamilySummaryDto result = await service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { Name = "Renamed" }, CancellationToken.None);

        result.FamilyName.Should().Be("Renamed");
        aliases.Verify(
            s => s.EnsureModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once);
        aliases.Verify(
            s => s.RemoveModelAliasAsync(modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once);
        (await dbContext.MachineProfiles.AsNoTracking()
            .SingleAsync(variant => variant.MachineModelProfileId == familyId)).Id
            .Should().Be(variantId, "a rename preserves the surviving variant id");
    }

    [Fact]
    public async Task EditFamilyAsync_RenameToExistingName_Throws409()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        _ = SeedHealthyFamily(dbContext, Guid.NewGuid(), name: "Taken");
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            new Mock<IProfileFamilyWorkerClient>(MockBehavior.Strict));

        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { Name = "Taken" }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyConflictException>();
    }

    [Fact]
    public async Task EditFamilyAsync_AddNozzle_MaterializesNewVariantAndPreservesExistingId()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        _ = await service.EditFamilyAsync(
            familyId,
            new EditProfileFamilyRequestDto { NozzleDiameters = [0.4, 0.8] },
            CancellationToken.None);

        List<MachineProfile> variants = await dbContext.MachineProfiles.AsNoTracking()
            .Where(variant => variant.MachineModelProfileId == familyId).ToListAsync();
        variants.Should().HaveCount(2);
        variants.Select(variant => variant.Id).Should().Contain(variantId);
        variants.Should().Contain(variant => variant.Name.Contains("0.8"));
    }

    [Fact]
    public async Task EditFamilyAsync_RemoveUnreferencedNozzle_Succeeds()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid keptId, _) = SeedFamilyWithTwoNozzles(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        _ = await service.EditFamilyAsync(
            familyId,
            new EditProfileFamilyRequestDto { NozzleDiameters = [0.4] },
            CancellationToken.None);

        List<MachineProfile> variants = await dbContext.MachineProfiles.AsNoTracking()
            .Where(variant => variant.MachineModelProfileId == familyId).ToListAsync();
        variants.Should().ContainSingle().Which.Id.Should().Be(keptId);
    }

    [Fact]
    public async Task EditFamilyAsync_RemoveNozzleReferencedByPrinter_Throws409()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _, Guid removedId) = SeedFamilyWithTwoNozzles(dbContext, modelId);
        Mock<IPrinterProfileCheckRepository> printerRefs = PrinterRefs(new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Bench Printer",
            TemplateMachineProfileId = removedId
        });
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            new Mock<IProfileFamilyWorkerClient>(MockBehavior.Strict),
            printerRefs);

        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { NozzleDiameters = [0.4] }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyInUseException>();
        (await dbContext.MachineProfiles.CountAsync(variant => variant.MachineModelProfileId == familyId))
            .Should().Be(2, "a blocked removal must not mutate the variant set");
    }

    [Fact]
    public async Task EditFamilyAsync_RemoveNozzleReferencedByActiveJob_Throws409()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _, Guid removedId) = SeedFamilyWithTwoNozzles(dbContext, modelId);
        dbContext.SliceJobs.Add(new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            MachineProfileId = removedId,
            CreatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            new Mock<IProfileFamilyWorkerClient>(MockBehavior.Strict));

        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { NozzleDiameters = [0.4] }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyInUseException>();
    }

    [Fact]
    public async Task EditFamilyAsync_EmptyNozzleArray_Throws400()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            new Mock<IProfileFamilyWorkerClient>(MockBehavior.Strict));

        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { NozzleDiameters = [] }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditFamilyAsync_SourceModelUnavailable_Throws422()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        // Catalog offers only "Prusa Test"; the requested re-bind target is absent.
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        Func<Task> act = () => service.EditFamilyAsync(
            familyId,
            new EditProfileFamilyRequestDto { SourceMachineModelName = "Removed Model" },
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilySourceException>();
        (await dbContext.MachineModelProfiles.AsNoTracking().SingleAsync(f => f.Id == familyId))
            .RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy,
                "a source failure is detected before any mutation");
    }

    [Fact]
    public async Task RenderFamilyAsync_StaleFamily_BecomesHealthyAndUpdatesVersion()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(
            dbContext, modelId, status: ProfileFamilyRenderStatus.Stale);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        ProfileFamilySummaryDto result = await service.RenderFamilyAsync(familyId, CancellationToken.None);

        result.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
        result.RenderedForOrcaVersion.Should().Be("2.5.0");
        result.LastRenderedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RenderFamilyAsync_FailedFamily_Recovers()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(
            dbContext, modelId, status: ProfileFamilyRenderStatus.Failed);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        ProfileFamilySummaryDto result = await service.RenderFamilyAsync(familyId, CancellationToken.None);

        result.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Healthy);
    }

    [Fact]
    public async Task RenderFamilyAsync_CalledTwice_IsIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), EditWorker());

        _ = await service.RenderFamilyAsync(familyId, CancellationToken.None);
        _ = await service.RenderFamilyAsync(familyId, CancellationToken.None);

        List<MachineProfile> variants = await dbContext.MachineProfiles.AsNoTracking()
            .Where(variant => variant.MachineModelProfileId == familyId).ToListAsync();
        variants.Should().ContainSingle().Which.Id
            .Should().Be(variantId, "a repeated re-render neither duplicates nor churns variants");
    }

    [Fact]
    public async Task RenderFamilyAsync_WriteFails_MarksFailedAndRestoresPreviousBundle()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        // The first WriteBundleAsync (the new bundle) fails; the second (the restore) succeeds.
        Mock<IProfileFamilyWorkerClient> worker = EditWorker(
            firstWriteFailure: new HttpRequestException("worker load rejected the new bundle"));
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
        (await dbContext.MachineModelProfiles.AsNoTracking().SingleAsync(f => f.Id == familyId))
            .RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
        worker.Verify(
            s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "the previous good bundle must be re-installed after a failed re-render");
    }

    [Fact]
    public async Task RenderFamilyAsync_SourceUnavailable_MarksFailed()
    {
        // H1: a re-render whose persisted source no longer resolves fails at source derivation — BEFORE
        // the install try. That happens on the same pre-install region as the catalog fetch and the
        // in-memory render, so the row must be stamped Failed (not left Healthy/Stale) or render-stale
        // never retries it and the family reports healthy while being unrenderable.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        // The catalog no longer offers the family's persisted "Prusa Test" source, so DeriveSource
        // manufacturer throws ProfileFamilySourceException (422) before any worker/DB mutation.
        ProfileFamilyService service = CreateService(
            dbContext,
            Catalog(modelId),
            EditAliases(modelId, "Farm Test"),
            EchoRenderer(),
            EditWorker(sourceModelNames: "Other Model"));

        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilySourceException>();
        (await dbContext.MachineModelProfiles.AsNoTracking().SingleAsync(f => f.Id == familyId))
            .RenderStatus.Should().Be(
                ProfileFamilyRenderStatus.Failed,
                "a re-render source failure must persist Failed so render-stale retries it (H1)");
    }

    [Fact]
    public async Task RenderFamilyAsync_WorkerUnavailable_MarksFailed()
    {
        // H1: a re-render whose catalog fetch fails (worker down, 503) fails at the very first pre-install
        // step. The persisted row must still be stamped Failed rather than left Healthy, so the caller's
        // 503 is matched by a row render-stale will re-attempt.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(
                string.Empty, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("worker offline"));
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
        (await dbContext.MachineModelProfiles.AsNoTracking().SingleAsync(f => f.Id == familyId))
            .RenderStatus.Should().Be(
                ProfileFamilyRenderStatus.Failed,
                "a re-render worker failure must persist Failed so render-stale retries it (H1)");
    }

    [Fact]
    public async Task RenderStaleFamiliesAsync_ReturnsPerFamilyResults_WithPartialFailureSurfaced()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid healthyId, _) = SeedHealthyFamily(
            dbContext, modelId, name: "Renderable", status: ProfileFamilyRenderStatus.Stale);
        (Guid brokenId, _) = SeedHealthyFamily(
            dbContext, Guid.NewGuid(), name: "Broken", status: ProfileFamilyRenderStatus.Stale,
            sourceMachineModelName: "Removed Model");
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Renderable"), EchoRenderer(), EditWorker());

        RenderStaleFamiliesResponseDto response =
            await service.RenderStaleFamiliesAsync(CancellationToken.None);

        IReadOnlyList<ProfileFamilyRenderResultDto> results = response.Results;
        response.RemainingCount.Should().Be(0);
        results.Should().HaveCount(2);
        results.Single(r => r.FamilyId == healthyId).RenderStatus
            .Should().Be(ProfileFamilyRenderStatus.Healthy);
        ProfileFamilyRenderResultDto broken = results.Single(r => r.FamilyId == brokenId);
        broken.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
        broken.Code.Should().Be("source_preset_unavailable");
        broken.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListFamiliesAsync_MarksHealthyFamilyStale_WhenLiveVersionDiffers()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId); // RenderedForOrcaVersion = 2.4.2
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            StalenessWorker("2.5.0"));

        IReadOnlyList<ProfileFamilySummaryDto> families =
            await service.ListFamiliesAsync(null, CancellationToken.None);

        families.Single(f => f.FamilyId == familyId).RenderStatus
            .Should().Be(ProfileFamilyRenderStatus.Stale);
    }

    [Fact]
    public async Task ListFamiliesAsync_DoesNotMarkNeverRenderedFamilyStale()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(
            dbContext, modelId, status: ProfileFamilyRenderStatus.Failed, renderedForOrcaVersion: null);
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            StalenessWorker("2.5.0"));

        IReadOnlyList<ProfileFamilySummaryDto> families =
            await service.ListFamiliesAsync(null, CancellationToken.None);

        families.Single(f => f.FamilyId == familyId).RenderStatus
            .Should().Be(ProfileFamilyRenderStatus.Failed,
                "a family that never rendered (RenderedForOrcaVersion null) must not be flipped to Stale");
    }

    [Fact]
    public async Task DeleteFamilyAsync_RenderedForDifferentVersionThanLiveWorker_DeletesUsingNullVersion()
    {
        // C1: a family rendered for 2.4.2 whose worker was upgraded in place to a different version must
        // still be deletable. The delete must select any fresh online worker (null version), never the
        // render-time version, or the version-exact worker selector throws 503 forever.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(
            dbContext, modelId, status: ProfileFamilyRenderStatus.Stale, renderedForOrcaVersion: "2.4.2");
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), worker);

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync()).Should().Be(0);
        worker.Verify(
            client => client.DeleteBundleAsync(null, familyId, It.IsAny<CancellationToken>()),
            Times.Once,
            "a Stale family whose engine version differs from the live worker must delete via null version (C1)");
    }

    [Fact]
    public async Task DeleteFamilyAsync_DbCleanupFailsAfterWorkerDelete_MarksFailedAndRethrows()
    {
        // C3: the worker bundle delete succeeds, then cache invalidation fails. Leaving the row Healthy
        // would report a family whose bundle is gone. Compensate by marking it Failed (visibly broken and
        // re-deletable) and rethrow, rather than a silent half-delete reported as Healthy.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = new(MockBehavior.Strict);
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto>
            {
                new(Guid.NewGuid(), modelId, "Farm Test", "OrcaSlicer"),
                new(Guid.NewGuid(), modelId, "Other Coverage", "OrcaSlicer"),
            });
        _ = catalog
            .Setup(service => service.InvalidateModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("alias cache is down"));
        ProfileFamilyService service = CreateService(
            dbContext, catalog, DeleteAliases(modelId), Renderer(), DeleteWorker());

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        MachineModelProfile persisted = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        persisted.RenderStatus.Should().Be(
            ProfileFamilyRenderStatus.Failed,
            "a post-worker-delete cleanup failure must leave the row visibly broken, not Healthy (C3)");
    }

    [Fact]
    public async Task DeleteFamilyAsync_LastOrcaCoverageForModelUsedByPrinter_Throws409_BeforeWorkerCall()
    {
        // #2086: the family's OrcaSlicer alias is the model's ONLY coverage and a registered printer uses
        // that model, so deleting it would leave that printer with zero machine profiles
        // (GET .../machine/for-model/{modelId} would start returning 404 no_profiles_for_model). Refuse with
        // a specific 409 whose detail names the affected printer, and do so BEFORE touching the worker.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        // Only the family's own alias covers the model, so its removal strips the last OrcaSlicer coverage.
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnlyFamilyAlias(modelId));
        var affectedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Shop Printer",
            ModelId = modelId
        };
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext,
            catalog,
            DeleteAliases(modelId),
            Renderer(),
            worker,
            PrinterRefs(modelPrinter: affectedPrinter));

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await act.Should().ThrowAsync<ProfileFamilyLastCoverageException>())
            .Which.Message.Should().Contain("Shop Printer")
            .And.Contain(affectedPrinter.Id.ToString());
        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(1, "a coverage-loss refusal must not delete the family");
        worker.Verify(
            client => client.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the coverage-loss refusal must fire before any worker mutation");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ModelHasAnotherOrcaAlias_DeletesEvenWithDependentPrinter()
    {
        // #2086: when the model keeps a DISTINCT OrcaSlicer alias after this family's alias is removed, the
        // model retains coverage, so deletion must succeed even though a registered printer uses the model.
        // A distinct alias short-circuits before FindByModelIdAsync is ever consulted, so no false refusal.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        var dependentPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Shop Printer",
            ModelId = modelId
        };
        // Catalog(modelId) returns the family alias PLUS a second distinct OrcaSlicer alias by default.
        ProfileFamilyService service = CreateService(
            dbContext,
            Catalog(modelId),
            DeleteAliases(modelId),
            Renderer(),
            DeleteWorker(),
            PrinterRefs(modelPrinter: dependentPrinter));

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(0, "another OrcaSlicer alias keeps the model covered, so deletion must succeed");
    }

    [Fact]
    public async Task DeleteFamilyAsync_LastCoverageButNoDependentPrinter_Deletes()
    {
        // #2086: even when this family's alias is the model's last OrcaSlicer coverage, deletion must
        // succeed when NO registered printer uses that model — there is nothing to orphan. This is the
        // FullLifecycle E2E scenario in miniature, which must keep passing unchanged.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnlyFamilyAlias(modelId));
        // PrinterRefs() default reports no printer bound to the model (FindByModelIdAsync -> null).
        ProfileFamilyService service = CreateService(
            dbContext, catalog, DeleteAliases(modelId), Renderer(), DeleteWorker());

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(0, "no printer depends on the model, so removing its last coverage orphans nothing");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ForceTrue_OverridesLastCoverageRefusal()
    {
        // #2086 escape hatch: ?force=true bypasses ONLY the indirect coverage-loss check, so a mis-created
        // family that a printer happens to depend on can still be deleted rather than being stuck forever.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnlyFamilyAlias(modelId));
        var affectedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Shop Printer",
            ModelId = modelId
        };
        ProfileFamilyService service = CreateService(
            dbContext,
            catalog,
            DeleteAliases(modelId),
            Renderer(),
            DeleteWorker(),
            PrinterRefs(modelPrinter: affectedPrinter));

        await service.DeleteFamilyAsync(familyId, force: true, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(0, "force=true must bypass the coverage-loss refusal and delete the family");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ForceTrue_DoesNotOverrideDirectTemplateProfileRefusal()
    {
        // #2086: force waives ONLY the indirect coverage check, NEVER the direct-reference refusal. A
        // variant bound as a printer's template machine profile is a concrete FK-ish binding whose removal
        // orphans that printer, so it must stay refused regardless of force.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        var blockingPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Bench Printer",
            TemplateMachineProfileId = variantId
        };
        Mock<IProfileFamilyWorkerClient> worker = DeleteWorker();
        ProfileFamilyService service = CreateService(
            dbContext,
            Catalog(modelId),
            DeleteAliases(modelId),
            Renderer(),
            worker,
            PrinterRefs(blockingPrinter));

        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: true, CancellationToken.None);

        (await act.Should().ThrowAsync<ProfileFamilyInUseException>())
            .Which.Message.Should().Contain("Bench Printer");
        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(1, "force must never bypass the direct template-profile refusal");
        worker.Verify(
            client => client.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the direct-reference refusal must fire before any worker mutation, even under force");
    }

    [Fact]
    public async Task RenderFamilyAsync_FamilyDeletedConcurrentlyDuringPersist_RemovesBundleAndThrows404()
    {
        // #2087: a render racing a delete installs its bundle on the worker, then its persist matches zero
        // rows (the row was deleted) and EF throws DbUpdateConcurrencyException. The handler must remove the
        // orphaned bundle it just installed and surface a clean 404, never a raw 500 that strands the bundle.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentDeleteOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.4.2");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.4.2");
        _ = worker
            .Setup(service => service.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = worker
            .Setup(service => service.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        // Arm the concurrent delete: the next persist (the render's single Healthy save) removes the family
        // rows out-of-band and reports the optimistic-concurrency conflict EF raises on a zero-row update.
        dbContext.DeleteFamilyOnNextSave = true;
        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        (await act.Should().ThrowAsync<ProfileFamilyConcurrentlyDeletedException>())
            .Which.Message.Should().Contain(familyId.ToString());
        worker.Verify(
            service => service.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the bundle was installed before the persist lost the race");
        worker.Verify(
            service => service.DeleteBundleAsync(null, familyId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the orphaned bundle installed before the concurrent delete must be compensated");
    }

    [Fact]
    public async Task RenderFamilyAsync_ConcurrencyConflictButFamilyStillExists_Throws409_WithoutBundleRemoval()
    {
        // #2087: a DbUpdateConcurrencyException whose row still exists (a concurrent modification, not a
        // delete) maps to a clean 409, and must NOT remove the installed bundle — the row still drives it.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ThrowingSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.4.2");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.4.2");
        _ = worker
            .Setup(service => service.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = worker
            .Setup(service => service.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        // Every persist throws DbUpdateConcurrencyException, but the row is never actually removed.
        dbContext.ThrowOnSave = true;
        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyConcurrencyException>();
        worker.Verify(
            service => service.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a concurrent modification (row still present) must not remove the bundle the row still drives");
    }

    [Fact]
    public async Task RenderFamilyAsync_FamilyDeletedConcurrently_WithCancelledRequestToken_StillRemovesBundle()
    {
        // Cancellation-after-conflict (render path): the render installs its bundle, the persist loses the
        // race to a concurrent DELETE, AND the caller's request token is cancelled at that exact instant.
        // The post-conflict existence re-check must NOT observe that token (it uses CancellationToken.None),
        // so the deleted-concurrently branch is still taken and the orphaned bundle is still removed. If the
        // read threaded `ct`, it would throw OperationCanceledException, fall through to the generic restore
        // handler, and re-install (re-orphan) the bundle it just installed — the unrecoverable state this
        // whole fix exists to prevent.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentDeleteOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.4.2");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.4.2");
        _ = worker
            .Setup(service => service.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = worker
            .Setup(service => service.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        // The request token becomes cancelled at the instant the persist loses the delete race — exactly the
        // window the compensation reads run in. A pre-cancelled token from the start would instead abort the
        // pre-persist worker/catalog work, so cancellation is armed to fire WITH the conflict.
        using var cts = new CancellationTokenSource();
        dbContext.DeleteFamilyOnNextSave = true;
        dbContext.CancelRequestOnConflict = cts;
        Func<Task> act = () => service.RenderFamilyAsync(familyId, cts.Token);

        // Post-fix: the deleted-concurrently branch runs despite cancellation -> clean 404-mapping exception.
        // Pre-fix (ct threaded): the re-check throws OperationCanceledException, so this assertion fails.
        (await act.Should().ThrowAsync<ProfileFamilyConcurrentlyDeletedException>())
            .Which.Message.Should().Contain(familyId.ToString());
        worker.Verify(
            service => service.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "only the initial install runs; the generic restore path (which would re-orphan the bundle) must not");
        worker.Verify(
            service => service.DeleteBundleAsync(null, familyId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the orphaned bundle must still be compensated even though the request token was cancelled");
    }

    [Fact]
    public async Task EditFamilyAsync_RenameRacesConcurrentModification_RestoresOldBundleAndAlias_Throws409()
    {
        // H1: an edit RENAME whose persist loses a concurrency race but whose family row SURVIVES must not
        // leave the farm split-brained (worker describing "Renamed", DB still "Farm Test"). Force the failure
        // on a RENAME (not a plain re-render) so restoring the OLD bundle/alias is observable: a plain
        // re-render's previous and new states are identical, so it cannot distinguish restore from leave.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentModifyOnceOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.5.0");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker.Setup(s => s.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker.Setup(s => s.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.5.0");
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Forward rename installs "Renamed" then, on failure, the restore re-installs the OLD "Farm Test"
        // bundle, re-adds the OLD-name alias, and drops the TARGET-name alias.
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases.Setup(s => s.ResolveModelAliasAsync("Renamed", "OrcaSlicer")).ReturnsAsync((Guid?)null);
        _ = aliases.Setup(s => s.EnsureModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases.Setup(s => s.EnsureModelAliasAsync(modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases.Setup(s => s.RemoveModelAliasAsync(modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases.Setup(s => s.RemoveModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(dbContext, Catalog(modelId), aliases, EchoRenderer(), worker);

        // The single Healthy persist loses the race (row survives); every later save (mark-Failed) proceeds.
        dbContext.ConflictOnNextSave = true;
        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { Name = "Renamed" }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyConcurrencyException>();

        // The restore re-installed the OLD ("Farm Test") bundle content, never re-PUT the failed new one.
        worker.Verify(
            s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Farm Test"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the previous good (old-name) bundle must be re-installed after the lost-race rename");
        aliases.Verify(
            s => s.EnsureModelAliasAsync(modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once,
            "the OLD-name alias must be restored");
        aliases.Verify(
            s => s.RemoveModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once,
            "the new TARGET-name alias installed before the failed persist must be dropped");
        MachineModelProfile after = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        after.Name.Should().Be("Farm Test", "the row still holds the OLD state — the rename never persisted");
        after.RenderStatus.Should().Be(
            ProfileFamilyRenderStatus.Failed,
            "the surviving row must be marked Failed so the divergence is visible and re-renderable (H1)");
    }

    [Fact]
    public async Task EditFamilyAsync_RenameDeletedConcurrently_RemovesOrphanedTargetAlias_Throws404()
    {
        // H3: an edit RENAME whose family row is DELETED concurrently during persist already created the
        // TARGET-name alias ("Renamed") before the save. The concurrent delete only knew the PREVIOUS name,
        // so without compensation the target alias survives pointing at a model with no family/bundle. The
        // handler must best-effort remove that orphaned target alias. Uses a RENAME, not a plain render,
        // because only a rename creates a distinct target alias to orphan.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentDeleteOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.5.0");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker.Setup(s => s.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker.Setup(s => s.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.5.0");
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.IsAny<ProfileFamilyBundleDto>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = worker.Setup(s => s.DeleteBundleAsync(
                It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Forward rename: ensure "Renamed", drop "Farm Test". Compensation: remove the orphaned "Renamed".
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases.Setup(s => s.ResolveModelAliasAsync("Renamed", "OrcaSlicer")).ReturnsAsync((Guid?)null);
        _ = aliases.Setup(s => s.EnsureModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases.Setup(s => s.RemoveModelAliasAsync(modelId, "Farm Test", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases.Setup(s => s.RemoveModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(dbContext, Catalog(modelId), aliases, EchoRenderer(), worker);

        // Arm the concurrent delete: the persist removes the family rows out-of-band and reports the conflict.
        dbContext.DeleteFamilyOnNextSave = true;
        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { Name = "Renamed" }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyConcurrentlyDeletedException>();

        aliases.Verify(
            s => s.RemoveModelAliasAsync(modelId, "Renamed", "OrcaSlicer", It.IsAny<CancellationToken>()),
            Times.Once,
            "the orphaned TARGET-name alias created before the concurrent delete must be removed (H3)");
        worker.Verify(
            s => s.DeleteBundleAsync(null, familyId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the orphaned bundle installed before the concurrent delete must still be removed");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ConcurrencyConflictButFamilyStillExists_MarksFailedAndThrows409()
    {
        // H2: on the delete path the worker bundle and alias are removed FIRST (worker-first ordering), then
        // the row delete loses a concurrency race but the family row SURVIVES (a concurrent modification, not
        // a delete). Leaving it Healthy would list a family whose bundle is gone and whose slicing is broken.
        // The handler must mark the surviving row Failed (identical to C3 on the other delete failure path).
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentModifyOnceOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), DeleteWorker());

        // The transactional row delete loses the race (row survives); the mark-Failed save then proceeds.
        dbContext.ConflictOnNextSave = true;
        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyConcurrencyException>();
        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(1, "a lost delete race must leave the surviving row in place");
        MachineModelProfile persisted = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        persisted.RenderStatus.Should().Be(
            ProfileFamilyRenderStatus.Failed,
            "the surviving row whose bundle/alias were already removed must be marked Failed, not Healthy (H2)");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ConcurrencyConflictButFamilyStillExists_WithCancelledRequestToken_StillMarksFailed()
    {
        // Cancellation-after-conflict (delete path): the worker bundle and alias are removed FIRST, the row
        // delete loses the race but the family row SURVIVES, AND the caller's request token is cancelled at
        // that exact instant. The post-conflict existence re-check must NOT observe that token (it uses
        // CancellationToken.None), so the surviving row is still marked Failed. If the read threaded `ct`, it
        // would throw OperationCanceledException, skip TryMarkRenderFailedAsync, and leave a family reporting
        // Healthy with no bundle behind it — the exact H2 defect, reopened via cancellation.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new ConcurrentModifyOnceOnSaveDbContext(options);
        _ = dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), DeleteAliases(modelId), Renderer(), DeleteWorker());

        // The request token becomes cancelled at the instant the row delete loses the race — exactly the
        // window the existence re-check and mark-Failed compensation run in.
        using var cts = new CancellationTokenSource();
        dbContext.ConflictOnNextSave = true;
        dbContext.CancelRequestOnConflict = cts;
        Func<Task> act = () => service.DeleteFamilyAsync(familyId, force: false, cts.Token);

        // Post-fix: the compensation runs despite cancellation -> clean 409 + surviving row marked Failed.
        // Pre-fix (ct threaded): the re-check throws OperationCanceledException, so BOTH assertions fail.
        _ = await act.Should().ThrowAsync<ProfileFamilyConcurrencyException>();
        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(1, "a lost delete race must leave the surviving row in place");
        MachineModelProfile persisted = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        persisted.RenderStatus.Should().Be(
            ProfileFamilyRenderStatus.Failed,
            "the compensation must mark the surviving row Failed even though the request token was cancelled");
    }

    [Fact]
    public async Task DeleteFamilyAsync_NeverAliasedFamilyWithDependentPrinter_Deletes_WithoutForce()
    {
        // H4: a family whose original render failed can be persisted before its OrcaSlicer alias was ever
        // created. Deleting it strands nothing — there is no alias to remove and no coverage to lose — so it
        // must delete cleanly WITHOUT force even though the model is otherwise uncovered and a printer uses
        // it. Before the fix this was a false refusal (empty alias set -> printer check -> refuse).
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);
        Mock<ICatalogServiceAdapter> catalog = Catalog(modelId);
        // The model has NO OrcaSlicer coverage at all, and crucially the family's OWN alias is absent.
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto>());
        var dependentPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Shop Printer",
            ModelId = modelId
        };
        ProfileFamilyService service = CreateService(
            dbContext,
            catalog,
            DeleteAliases(modelId),
            Renderer(),
            DeleteWorker(),
            PrinterRefs(modelPrinter: dependentPrinter));

        await service.DeleteFamilyAsync(familyId, force: false, CancellationToken.None);

        (await dbContext.MachineModelProfiles.CountAsync())
            .Should().Be(0, "a never-aliased family strands nothing, so it deletes without force despite a dependent printer");
    }

    [Fact]
    public async Task EditFamilyAsync_WorkerWriteFails_RollsBackDbRowExceptRenderStatus()
    {
        // C2 (i): a failed edit must leave the DB row byte-identical to pre-edit except RenderStatus.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, Guid variantId) = SeedHealthyFamily(dbContext, modelId);
        MachineModelProfile before = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        string? beforeName = before.Name;
        string? beforeSource = before.SourceMachineModelName;
        string? beforeOverrides = before.FamilyOverridesJson;
        string? beforeRenderedVersion = before.RenderedForOrcaVersion;

        Mock<IProfileFamilyWorkerClient> worker = EditWorker(
            firstWriteFailure: new HttpRequestException("worker load rejected the new bundle"));
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), EditAliases(modelId, "Farm Test"), EchoRenderer(), worker);

        // Change BOTH an override and the nozzle set so multiple facets would mutate on success.
        Func<Task> act = () => service.EditFamilyAsync(
            familyId,
            new EditProfileFamilyRequestDto
            {
                FamilyOverrides = Overrides("""{"printable_height":"250"}"""),
                NozzleDiameters = [0.4, 0.8]
            },
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();

        MachineModelProfile after = await dbContext.MachineModelProfiles
            .AsNoTracking().Include(f => f.MachineProfiles).SingleAsync(f => f.Id == familyId);
        after.Name.Should().Be(beforeName);
        after.SourceMachineModelName.Should().Be(beforeSource);
        after.FamilyOverridesJson.Should().Be(beforeOverrides, "a failed edit must roll the overrides back");
        after.RenderedForOrcaVersion.Should().Be(beforeRenderedVersion);
        after.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed, "only the status may change");
        after.MachineProfiles.Should().ContainSingle()
            .Which.Id.Should().Be(variantId, "the added 0.8 variant must never be persisted (install-then-persist leaves the variant set untouched until the single Healthy save)");
    }

    [Fact]
    public async Task EditFamilyAsync_WorkerInstallFails_RestoresOldBundleContentNotNew()
    {
        // C2 (iii): force the failure at the worker install step and prove the RESTORED bundle content is
        // the OLD one (previous name), never a re-PUT of the failed new bundle.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.5.0");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker.Setup(s => s.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker.Setup(s => s.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.5.0");
        // The worker rejects the NEW bundle ("Renamed") but accepts the previous good bundle ("Farm Test").
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Renamed"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("worker rejected the renamed bundle"));
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Farm Test"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IPrinterModelAliasService> aliases = RenameRestoreAliases(modelId, "Farm Test", "Renamed");
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), aliases, EchoRenderer(), worker);

        Func<Task> act = () => service.EditFamilyAsync(
            familyId, new EditProfileFamilyRequestDto { Name = "Renamed" }, CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
        // The restore re-installed the OLD bundle; the NEW bundle was never successfully installed.
        worker.Verify(
            s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Farm Test"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the previous good (old-name) bundle must be re-installed after a failed rename edit");
        MachineModelProfile after = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        after.Name.Should().Be("Farm Test", "a failed rename must roll the name back");
        after.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
    }

    [Fact]
    public async Task EditFamilyAsync_TwoConsecutiveFailures_KeepOriginalGoodBundleInstalled()
    {
        // C2 (ii): two consecutive failed edits must still leave the ORIGINAL good bundle installed. With
        // install-then-persist, the DB row and variant set are never mutated on a failed attempt (the
        // single Healthy save only runs once the install succeeds), so every restore re-installs the good
        // ("Farm Test") bundle — the failed ("Renamed") bundle is never accepted.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId);

        ProfileFamilyWorkerTarget target = new("http://worker", "2.5.0");
        AllProfilesResponseDto catalog = WorkerCatalog("Prusa Test");
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker.Setup(s => s.GetCatalogAsync(string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker.Setup(s => s.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("2.5.0");
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Renamed"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("worker rejected the renamed bundle"));
        _ = worker.Setup(s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Farm Test"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IPrinterModelAliasService> aliases = RenameRestoreAliases(modelId, "Farm Test", "Renamed");
        ProfileFamilyService service = CreateService(
            dbContext, Catalog(modelId), aliases, EchoRenderer(), worker);

        EditProfileFamilyRequestDto rename = new() { Name = "Renamed" };
        _ = await service.Invoking(s => s.EditFamilyAsync(familyId, rename, CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
        _ = await service.Invoking(s => s.EditFamilyAsync(familyId, rename, CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();

        worker.Verify(
            s => s.WriteBundleAsync(
                It.IsAny<ProfileFamilyWorkerTarget>(),
                It.Is<ProfileFamilyBundleDto>(b => b.FamilyName == "Farm Test"),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "each of the two failed attempts must restore the ORIGINAL good bundle, never the bad one");
        MachineModelProfile after = await dbContext.MachineModelProfiles
            .AsNoTracking().SingleAsync(f => f.Id == familyId);
        after.Name.Should().Be("Farm Test");
        after.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
    }

    [Fact]
    public async Task ListFamiliesAsync_DetectionSaveFails_StillReturnsList()
    {
        // C4: a SaveChangesAsync failure inside staleness detection must never break the read.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using ThrowingSaveDbContext dbContext = new(
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options);
        dbContext.Database.EnsureCreated();
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(dbContext, modelId); // RenderedForOrcaVersion = 2.4.2
        dbContext.ThrowOnSave = true; // the detection save (2.4.2 -> Stale) will fail
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            StalenessWorker("2.5.0"));

        IReadOnlyList<ProfileFamilySummaryDto> families =
            await service.ListFamiliesAsync(null, CancellationToken.None);

        families.Should().ContainSingle().Which.FamilyId.Should().Be(
            familyId, "a failing detection save must be swallowed so the list still returns");
    }

    [Fact]
    public async Task RenderStaleFamiliesAsync_UnexpectedFailure_ReturnsFixedDetailNotExceptionMessage()
    {
        // S5: the bulk render-stale response must never leak a raw internal exception message.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        (Guid familyId, _) = SeedHealthyFamily(
            dbContext, modelId, status: ProfileFamilyRenderStatus.Stale);
        const string secret = "SECRET-INTERNAL-connection-string-9f3";
        Mock<IProfileFamilyRenderer> renderer = new(MockBehavior.Strict);
        _ = renderer
            .Setup(service => service.Render(
                It.IsAny<Guid>(),
                It.IsAny<CloneProfileFamilyRequestDto>(),
                It.IsAny<AllProfilesResponseDto>()))
            .Throws(new InvalidOperationException(secret));
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            renderer,
            EditWorker());

        RenderStaleFamiliesResponseDto response =
            await service.RenderStaleFamiliesAsync(CancellationToken.None);

        ProfileFamilyRenderResultDto result = response.Results.Single(r => r.FamilyId == familyId);
        result.RenderStatus.Should().Be(ProfileFamilyRenderStatus.Failed);
        result.Code.Should().Be("profile_family_render_failed");
        result.Detail.Should().Be("Profile family re-render failed unexpectedly.");
        result.Detail.Should().NotContain(secret, "an internal exception message must never leak");
    }

    [Fact]
    public async Task RenderFamilyAsync_ReRenderWouldDropReferencedVariant_Throws409()
    {
        // S6: a plain re-render must not be able to orphan a printer-referenced variant. A variant whose
        // name has no parseable nozzle diameter is dropped by the id-preserving merge; the re-render path
        // must run the same live reference check as an edit and refuse when the variant is referenced.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = familyId,
            Name = "Farm Test",
            Manufacturer = "Custom",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            Hash = familyId.ToString("N") + familyId.ToString("N"),
            IsSystem = false,
            RenderStatus = ProfileFamilyRenderStatus.Healthy,
            SourceMachineModelName = "Prusa Test",
            SlicerDistribution = "orca",
            RenderedForOrcaVersion = "2.4.2",
            LastRenderedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineProfiles =
            {
                new MachineProfile
                {
                    Id = variantId,
                    Name = "Farm Test custom variant", // no "X.Y nozzle" suffix => unparseable => dropped
                    Manufacturer = "Custom",
                    SlicerType = SlicerType.OrcaSlicer,
                    MachineModelProfileId = familyId,
                    Hash = variantId.ToString("N") + variantId.ToString("N"),
                    SourceSystemPresetName = "Prusa Test custom",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        });
        _ = await dbContext.SaveChangesAsync();
        Mock<IPrinterProfileCheckRepository> printerRefs = PrinterRefs(new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Bench Printer",
            TemplateMachineProfileId = variantId
        });
        ProfileFamilyService service = CreateService(
            dbContext,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Strict),
            new Mock<IPrinterModelAliasService>(MockBehavior.Strict),
            new Mock<IProfileFamilyRenderer>(MockBehavior.Strict),
            new Mock<IProfileFamilyWorkerClient>(MockBehavior.Strict),
            printerRefs);

        Func<Task> act = () => service.RenderFamilyAsync(familyId, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ProfileFamilyInUseException>();
        (await dbContext.MachineProfiles.CountAsync(v => v.MachineModelProfileId == familyId))
            .Should().Be(1, "a blocked re-render must not drop the referenced variant");
    }

    [Fact]
    public async Task RenderStaleFamiliesAsync_MoreThanBatchCap_ProcessesBatchAndReportsRemaining()
    {
        // S4: the bulk render-stale batch is bounded; a client drains the queue across calls.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext dbContext = CreateContext(connection);
        const int total = 27; // > MaxStaleRenderBatch (25)
        for (int i = 0; i < total; i++)
        {
            _ = SeedHealthyFamily(
                dbContext,
                Guid.NewGuid(),
                name: $"Stale Family {i:00}",
                status: ProfileFamilyRenderStatus.Stale);
        }

        // A loose alias service: each family has a distinct name, so per-name strict setup is impractical.
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Loose);
        _ = aliases
            .Setup(s => s.EnsureModelAliasAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<ICatalogServiceAdapter> catalog = new(MockBehavior.Loose);
        _ = catalog
            .Setup(s => s.InvalidateModelAliasesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProfileFamilyService service = CreateService(
            dbContext, catalog, aliases, EchoRenderer(), EditWorker());

        RenderStaleFamiliesResponseDto response =
            await service.RenderStaleFamiliesAsync(CancellationToken.None);

        response.Results.Should().HaveCount(25, "the batch is capped at MaxStaleRenderBatch");
        response.RemainingCount.Should().Be(total - 25, "the caller must be told how many remain");
    }

    /// <summary>
    /// A <see cref="SlicerDbContext"/> whose <see cref="SaveChangesAsync(CancellationToken)"/> throws on
    /// demand, used to prove staleness detection swallows a persistence failure on the read path (C4).
    /// </summary>
    private sealed class ThrowingSaveDbContext(DbContextOptions<SlicerDbContext> options)
        : SlicerDbContext(options)
    {
        public bool ThrowOnSave { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return ThrowOnSave
                ? throw new DbUpdateConcurrencyException("simulated concurrent staleness write conflict")
                : base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A <see cref="SlicerDbContext"/> that simulates a concurrent DELETE landing between a render's worker
    /// install and its persist (#2087): the next armed <see cref="SaveChangesAsync(CancellationToken)"/>
    /// removes the family and variant rows out-of-band and then throws the
    /// <see cref="DbUpdateConcurrencyException"/> EF raises when an UPDATE matches zero rows.
    /// </summary>
    private sealed class ConcurrentDeleteOnSaveDbContext(DbContextOptions<SlicerDbContext> options)
        : SlicerDbContext(options)
    {
        public bool DeleteFamilyOnNextSave { get; set; }

        /// <summary>
        /// When set, this source is cancelled at the instant the armed delete-conflict fires — i.e. the
        /// caller's request token becomes cancelled EXACTLY when the persist loses the race, so the
        /// post-conflict compensation reads run against an already-cancelled token. Proves those reads use
        /// <see cref="CancellationToken.None"/> and not the caller token.
        /// </summary>
        public CancellationTokenSource? CancelRequestOnConflict { get; set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (DeleteFamilyOnNextSave)
            {
                DeleteFamilyOnNextSave = false;
                // Child rows first so SQLite's per-connection foreign-key enforcement (enabled by the EF
                // Core provider) does not reject the parent delete.
                _ = await Database.ExecuteSqlRawAsync(
                    "DELETE FROM MachineProfiles; DELETE FROM MachineModelProfiles;", cancellationToken);
                CancelRequestOnConflict?.Cancel();
                throw new DbUpdateConcurrencyException("simulated concurrent family delete during render");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A <see cref="SlicerDbContext"/> that throws <see cref="DbUpdateConcurrencyException"/> on the NEXT
    /// armed <see cref="SaveChangesAsync(CancellationToken)"/> WITHOUT removing any row — a concurrent
    /// MODIFICATION whose family row SURVIVES (not a delete) — then lets every later save proceed. Models
    /// the render/delete-vs-modification race the persist loses: the guarded mark-Failed compensation that
    /// follows must still succeed against the surviving row (H1, H2).
    /// </summary>
    private sealed class ConcurrentModifyOnceOnSaveDbContext(DbContextOptions<SlicerDbContext> options)
        : SlicerDbContext(options)
    {
        public bool ConflictOnNextSave { get; set; }

        /// <summary>
        /// When set, this source is cancelled at the instant the armed conflict fires — i.e. the caller's
        /// request token becomes cancelled EXACTLY when the persist loses the race, so the post-conflict
        /// existence re-check runs against an already-cancelled token. Proves that read uses
        /// <see cref="CancellationToken.None"/> and not the caller token, so the mark-Failed compensation
        /// still runs.
        /// </summary>
        public CancellationTokenSource? CancelRequestOnConflict { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ConflictOnNextSave)
            {
                ConflictOnNextSave = false;
                CancelRequestOnConflict?.Cancel();
                throw new DbUpdateConcurrencyException(
                    "simulated concurrent family modification during persist (row survives)");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A single OrcaSlicer alias mapping named for the seeded family ("Farm Test"), i.e. the family's own
    /// alias is the model's ONLY coverage, so removing it strips the model's last OrcaSlicer coverage.
    /// </summary>
    private static IReadOnlyList<SlicerModelAliasDto> OnlyFamilyAlias(Guid modelId) =>
        new List<SlicerModelAliasDto> { new(Guid.NewGuid(), modelId, "Farm Test", "OrcaSlicer") };

    private static (Guid FamilyId, Guid VariantId) SeedHealthyFamily(
        SlicerDbContext dbContext,
        Guid modelId,
        string name = "Farm Test",
        ProfileFamilyRenderStatus status = ProfileFamilyRenderStatus.Healthy,
        string sourceMachineModelName = "Prusa Test",
        string? renderedForOrcaVersion = "2.4.2")
    {
        Guid familyId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = familyId,
            Name = name,
            Manufacturer = "Custom",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            Hash = familyId.ToString("N") + familyId.ToString("N"),
            IsSystem = false,
            RenderStatus = status,
            SourceMachineModelName = sourceMachineModelName,
            SlicerDistribution = "orca",
            RenderedForOrcaVersion = renderedForOrcaVersion,
            LastRenderedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineProfiles =
            {
                new MachineProfile
                {
                    Id = variantId,
                    Name = $"{name} 0.4 nozzle",
                    Manufacturer = "Custom",
                    SlicerType = SlicerType.OrcaSlicer,
                    MachineModelProfileId = familyId,
                    Hash = variantId.ToString("N") + variantId.ToString("N"),
                    SourceSystemPresetName = "Prusa Test 0.4 nozzle",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        });
        _ = dbContext.SaveChanges();
        return (familyId, variantId);
    }

    /// <summary>
    /// Seeds a Healthy family owning two nozzle variants (0.4 and 0.8), returning the family id, the
    /// 0.4 variant id (kept in the remove tests) and the 0.8 variant id (removed/referenced).
    /// </summary>
    private static (Guid FamilyId, Guid KeptVariantId, Guid RemovedVariantId) SeedFamilyWithTwoNozzles(
        SlicerDbContext dbContext,
        Guid modelId,
        string name = "Farm Test")
    {
        Guid familyId = Guid.NewGuid();
        Guid keptId = Guid.NewGuid();
        Guid removedId = Guid.NewGuid();
        dbContext.MachineModelProfiles.Add(new MachineModelProfile
        {
            Id = familyId,
            Name = name,
            Manufacturer = "Custom",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = modelId,
            Hash = familyId.ToString("N") + familyId.ToString("N"),
            IsSystem = false,
            RenderStatus = ProfileFamilyRenderStatus.Healthy,
            SourceMachineModelName = "Prusa Test",
            SlicerDistribution = "orca",
            RenderedForOrcaVersion = "2.4.2",
            LastRenderedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MachineProfiles =
            {
                new MachineProfile
                {
                    Id = keptId,
                    Name = $"{name} 0.4 nozzle",
                    Manufacturer = "Custom",
                    SlicerType = SlicerType.OrcaSlicer,
                    MachineModelProfileId = familyId,
                    Hash = keptId.ToString("N") + keptId.ToString("N"),
                    SourceSystemPresetName = "Prusa Test 0.4 nozzle",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                new MachineProfile
                {
                    Id = removedId,
                    Name = $"{name} 0.8 nozzle",
                    Manufacturer = "Custom",
                    SlicerType = SlicerType.OrcaSlicer,
                    MachineModelProfileId = familyId,
                    Hash = removedId.ToString("N") + removedId.ToString("N"),
                    SourceSystemPresetName = "Prusa Test 0.8 nozzle",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            }
        });
        _ = dbContext.SaveChanges();
        return (familyId, keptId, removedId);
    }

    /// <summary>
    /// Builds a family-shared overrides dictionary from a JSON object literal, mirroring how the
    /// controller binds the request DTO's <c>FamilyOverrides</c>.
    /// </summary>
    private static Dictionary<string, JsonElement> Overrides(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> overrides = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            overrides[property.Name] = property.Value.Clone();
        }

        return overrides;
    }

    /// <summary>
    /// A renderer that echoes the request: it produces one variant per requested nozzle, named
    /// <c>"{family} {nozzle} nozzle"</c> (so <c>ParseNozzleDiameter</c> recovers it), and serialises the
    /// requested family overrides as the canonical overrides JSON so a re-render is observable.
    /// </summary>
    private static Mock<IProfileFamilyRenderer> EchoRenderer()
    {
        Mock<IProfileFamilyRenderer> renderer = new(MockBehavior.Strict);
        _ = renderer
            .Setup(service => service.Render(
                It.IsAny<Guid>(),
                It.IsAny<CloneProfileFamilyRequestDto>(),
                It.IsAny<AllProfilesResponseDto>()))
            .Returns((Guid familyId, CloneProfileFamilyRequestDto request, AllProfilesResponseDto _) =>
            {
                List<RenderedMachineVariant> variants = request.NozzleDiameters
                    .Select(nozzle =>
                    {
                        string formatted = nozzle.ToString("0.###", CultureInfo.InvariantCulture);
                        return new RenderedMachineVariant(
                            $"{request.FamilyName} {formatted} nozzle",
                            nozzle,
                            $"{request.SourceMachineModelName} {formatted} nozzle",
                            $$"""{"nozzle_diameter":["{{formatted}}"]}""");
                    })
                    .ToList();
                string canonicalOverrides = JsonSerializer.Serialize(request.FamilyOverrides);
                return new ProfileFamilyRenderResult(
                    new ProfileFamilyBundleDto(familyId, request.FamilyName, "{}", []),
                    canonicalOverrides,
                    variants,
                    3,
                    2);
            });
        return renderer;
    }

    /// <summary>
    /// A worker client for the edit/re-render path: the full catalog contains the given source models
    /// (default <c>"Prusa Test"</c>), the selected worker reports OrcaSlicer <paramref name="activeVersion"/>,
    /// and <c>WriteBundleAsync</c> succeeds unless <paramref name="firstWriteFailure"/> forces the first
    /// write to throw (the second — the restore — then succeeds).
    /// </summary>
    private static Mock<IProfileFamilyWorkerClient> EditWorker(
        Exception? firstWriteFailure = null,
        string activeVersion = "2.5.0",
        params string[] sourceModelNames)
    {
        string[] models = sourceModelNames.Length == 0 ? ["Prusa Test"] : sourceModelNames;
        ProfileFamilyWorkerTarget target = new("http://worker", activeVersion);
        AllProfilesResponseDto catalog = WorkerCatalog(models);
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetCatalogAsync(
                string.Empty, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((target, catalog));
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeVersion);
        if (firstWriteFailure is null)
        {
            _ = worker
                .Setup(service => service.WriteBundleAsync(
                    It.IsAny<ProfileFamilyWorkerTarget>(),
                    It.IsAny<ProfileFamilyBundleDto>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            _ = worker
                .SetupSequence(service => service.WriteBundleAsync(
                    It.IsAny<ProfileFamilyWorkerTarget>(),
                    It.IsAny<ProfileFamilyBundleDto>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(firstWriteFailure)
                .Returns(Task.CompletedTask);
        }

        return worker;
    }

    /// <summary>
    /// A worker client used by the staleness-detection tests: it only reports the live OrcaSlicer
    /// version (or none, when <paramref name="activeVersion"/> is <see langword="null"/>).
    /// </summary>
    private static Mock<IProfileFamilyWorkerClient> StalenessWorker(string? activeVersion)
    {
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeVersion);
        return worker;
    }

    /// <summary>
    /// An alias service for the edit/re-render path: the (possibly new) name resolves to no existing
    /// mapping, ensuring the alias succeeds, and the old name is removed on a rename.
    /// </summary>
    private static Mock<IPrinterModelAliasService> EditAliases(
        Guid modelId,
        string name,
        string? renameFrom = null)
    {
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases
            .Setup(service => service.ResolveModelAliasAsync(name, "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        _ = aliases
            .Setup(service => service.EnsureModelAliasAsync(
                modelId, name, "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases
            .Setup(service => service.RemoveModelAliasAsync(
                modelId, renameFrom ?? name, "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return aliases;
    }

    /// <summary>
    /// Builds a worker catalog whose single manufacturer ("Prusa") exposes the given human-readable
    /// source machine-model names, so <c>DeriveSourceManufacturer</c> can resolve them.
    /// </summary>
    private static AllProfilesResponseDto WorkerCatalog(params string[] modelNames)
    {
        ManufacturerProfilesDto manufacturer = new() { Name = "Prusa" };
        int index = 0;
        foreach (string modelName in modelNames)
        {
            manufacturer.Models[$"model_{index++}"] = new PrinterModelProfilesDto
            {
                Name = modelName,
                ModelId = $"Prusa_{index}"
            };
        }

        return new AllProfilesResponseDto
        {
            ByHierarchy = { ["Prusa"] = manufacturer }
        };
    }



    /// <summary>
    /// An alias service for a FAILED rename edit: the new name passes the collision check (resolves to no
    /// mapping), and the restore path re-adds the previous name's alias and drops the target name's alias.
    /// The forward install never runs (the worker write throws first), so only these calls occur.
    /// </summary>
    private static Mock<IPrinterModelAliasService> RenameRestoreAliases(
        Guid modelId,
        string previousName,
        string targetName)
    {
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases
            .Setup(service => service.ResolveModelAliasAsync(targetName, "OrcaSlicer"))
            .ReturnsAsync((Guid?)null);
        _ = aliases
            .Setup(service => service.EnsureModelAliasAsync(
                modelId, previousName, "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = aliases
            .Setup(service => service.RemoveModelAliasAsync(
                modelId, targetName, "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return aliases;
    }

    private static Mock<IPrinterModelAliasService> DeleteAliases(
        Guid modelId,
        string familyName = "Farm Test")
    {
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases
            .Setup(service => service.RemoveModelAliasAsync(
                modelId, familyName, "OrcaSlicer", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return aliases;
    }

    private static Mock<IProfileFamilyWorkerClient> DeleteWorker(Exception? deleteFailure = null)
    {
        Mock<IProfileFamilyWorkerClient> worker = new(MockBehavior.Strict);
        if (deleteFailure is null)
        {
            _ = worker
                .Setup(client => client.DeleteBundleAsync(
                    It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }
        else
        {
            _ = worker
                .Setup(client => client.DeleteBundleAsync(
                    It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(deleteFailure);
        }

        return worker;
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
        // Default coverage: the model carries the family's own OrcaSlicer alias PLUS a second, distinct
        // OrcaSlicer alias, so the delete-time last-coverage check (EnsureNoLastCoverageLossAsync) always
        // short-circuits to "other coverage remains" and never blocks. The #2086 last-coverage tests
        // re-Setup this to return ONLY the family alias to exercise the refusal path.
        _ = catalog
            .Setup(service => service.GetModelAliasesAsync(
                modelId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SlicerModelAliasDto>
            {
                new(Guid.NewGuid(), modelId, "Farm Test", "OrcaSlicer"),
                new(Guid.NewGuid(), modelId, "Other Coverage", "OrcaSlicer"),
            });
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
        // List/get now run detection-on-read; returning no live version makes staleness detection a
        // safe no-op so these clone/list/get tests keep their seeded statuses.
        _ = worker
            .Setup(service => service.GetActiveOrcaVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
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
        Mock<IProfileFamilyWorkerClient> worker,
        Mock<IPrinterProfileCheckRepository>? printerRefs = null)
    {
        return new ProfileFamilyService(
            dbContext,
            catalog.Object,
            aliases.Object,
            renderer.Object,
            worker.Object,
            (printerRefs ?? PrinterRefs()).Object,
            NullLogger<ProfileFamilyService>.Instance);
    }

    /// <summary>
    /// A printer-reference repository that reports no printer bound to any variant, i.e. deletion
    /// is not blocked by a printer. Pass <paramref name="blockingPrinter"/> to exercise the direct
    /// template-profile block, or <paramref name="modelPrinter"/> to exercise the indirect
    /// last-coverage block (a printer that uses the family's bound catalog model).
    /// </summary>
    private static Mock<IPrinterProfileCheckRepository> PrinterRefs(
        Printer? blockingPrinter = null,
        Printer? modelPrinter = null)
    {
        Mock<IPrinterProfileCheckRepository> printerRefs = new();
        _ = printerRefs
            .Setup(repository => repository.FindByTemplateMachineProfileIdsAsync(
                It.IsAny<IReadOnlyCollection<Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(blockingPrinter);
        _ = printerRefs
            .Setup(repository => repository.FindByModelIdAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(modelPrinter);
        return printerRefs;
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
