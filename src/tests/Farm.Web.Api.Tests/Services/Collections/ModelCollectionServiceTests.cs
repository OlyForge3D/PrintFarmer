using Farm.Infrastructure.Data;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Collections;
using Farm.Web.Api.Tests.TestInfrastructure;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Collections;

/// <summary>
/// Service tests for <see cref="ModelCollectionService"/>, covering owner/administrator
/// authorization, model-existence validation, and membership mutation semantics. Tests use
/// the real repository over a SQLite in-memory database with a mocked
/// <see cref="IModel3DQueryProvider"/>.
/// </summary>
public class ModelCollectionServiceTests
{
    private static ModelCollectionService CreateService(AppDbContext db, IModel3DQueryProvider? provider)
    {
        var repo = new EfModelCollectionRepository(db);
        return new ModelCollectionService(repo, provider);
    }

    private static Mock<IModel3DQueryProvider> ProviderAllExist()
    {
        var mock = new Mock<IModel3DQueryProvider>();
        _ = mock.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock;
    }

    [Fact]
    public async Task Create_SetsOwnerAndUnshared()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "  Trimmed  ", Description = "d" }, owner, CancellationToken.None);

        Assert.Equal("Trimmed", created.Name);
        Assert.Equal(owner, created.OwnerUserId);
        Assert.False(created.IsShared);
        Assert.Equal(0, created.MemberCount);
    }

    [Fact]
    public async Task Create_WithBlankName_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "   " }, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_OwnerCanRead_NonOwnerDenied()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "Private" }, owner, CancellationToken.None);

        ModelCollectionDto? asOwner = await service.GetCollectionAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.NotNull(asOwner);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.GetCollectionAsync(created.Id, stranger, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Get_AdminCanReadOthers()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "Private" }, owner, CancellationToken.None);

        ModelCollectionDto? asAdmin = await service.GetCollectionAsync(created.Id, Guid.NewGuid(), callerIsAdmin: true, CancellationToken.None);
        Assert.NotNull(asAdmin);
    }

    [Fact]
    public async Task Get_SharedReadableByAnyone()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "Shared" }, owner, CancellationToken.None);
        _ = await service.SetSharedAsync(created.Id, shared: true, owner, callerIsAdmin: false, CancellationToken.None);

        ModelCollectionDto? asStranger = await service.GetCollectionAsync(created.Id, Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None);
        Assert.NotNull(asStranger);
        Assert.True(asStranger!.IsShared);
    }

    [Fact]
    public async Task Get_NonExistent_ReturnsNull()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);

        ModelCollectionDto? result = await service.GetCollectionAsync(Guid.NewGuid(), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task Update_NonOwnerDenied_MissingThrowsNotFound()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "Original" }, owner, CancellationToken.None);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.UpdateCollectionAsync(created.Id, new UpdateModelCollectionDto { Name = "New" }, Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));

        _ = await Assert.ThrowsAsync<CollectionNotFoundException>(() =>
            service.UpdateCollectionAsync(Guid.NewGuid(), new UpdateModelCollectionDto { Name = "New" }, owner, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Update_OwnerRenames()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "Original" }, owner, CancellationToken.None);

        ModelCollectionDto updated = await service.UpdateCollectionAsync(
            created.Id, new UpdateModelCollectionDto { Name = "Renamed", Description = "desc" }, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("desc", updated.Description);
    }

    [Fact]
    public async Task Delete_NonOwnerDenied_OwnerSucceeds()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "ToDelete" }, owner, CancellationToken.None);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.DeleteCollectionAsync(created.Id, Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));

        await service.DeleteCollectionAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);

        ModelCollectionDto? gone = await service.GetCollectionAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Null(gone);
    }

    [Fact]
    public async Task AddMember_ValidatesModelExistence()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        var provider = new Mock<IModel3DQueryProvider>();
        _ = provider.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        ModelCollectionService service = CreateService(db, provider.Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        CollectionModelValidationException ex = await Assert.ThrowsAsync<CollectionModelValidationException>(() =>
            service.AddMemberAsync(created.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None));
        Assert.Single(ex.InvalidModelIds);
    }

    [Fact]
    public async Task AddMember_IsIdempotent()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        _ = await service.AddMemberAsync(created.Id, modelId, owner, callerIsAdmin: false, CancellationToken.None);
        _ = await service.AddMemberAsync(created.Id, modelId, owner, callerIsAdmin: false, CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> members = await service.ListMembersAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Single(members);
    }

    [Fact]
    public async Task AddMember_NullProvider_SkipsValidation()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, provider: null);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        ModelCollectionMembershipDto membership = await service.AddMemberAsync(created.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None);
        Assert.NotEqual(Guid.Empty, membership.ModelId);
    }

    [Fact]
    public async Task RemoveMember_IsIdempotent()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        _ = await service.AddMemberAsync(created.Id, modelId, owner, callerIsAdmin: false, CancellationToken.None);

        await service.RemoveMemberAsync(created.Id, modelId, owner, callerIsAdmin: false, CancellationToken.None);
        // Removing again should not throw.
        await service.RemoveMemberAsync(created.Id, modelId, owner, callerIsAdmin: false, CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> members = await service.ListMembersAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Empty(members);
    }

    [Fact]
    public async Task ReplaceMembers_ReplacesEntireSet()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        _ = await service.AddMemberAsync(created.Id, a, owner, callerIsAdmin: false, CancellationToken.None);
        _ = await service.AddMemberAsync(created.Id, b, owner, callerIsAdmin: false, CancellationToken.None);

        ModelCollectionDto result = await service.ReplaceMembersAsync(created.Id, new[] { b, c }, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Equal(2, result.MemberCount);
        Assert.Contains(b, result.ModelIds);
        Assert.Contains(c, result.ModelIds);
        Assert.DoesNotContain(a, result.ModelIds);
    }

    [Fact]
    public async Task ReplaceMembers_DeduplicatesAndValidates()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Guid valid = Guid.NewGuid();
        Guid invalid = Guid.NewGuid();
        var provider = new Mock<IModel3DQueryProvider>();
        _ = provider.Setup(p => p.ExistsAsync(valid, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _ = provider.Setup(p => p.ExistsAsync(invalid, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        ModelCollectionService service = CreateService(db, provider.Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        _ = await Assert.ThrowsAsync<CollectionModelValidationException>(() =>
            service.ReplaceMembersAsync(created.Id, new[] { valid, invalid }, owner, callerIsAdmin: false, CancellationToken.None));

        // Deduplication: a repeated valid id yields a single membership.
        ModelCollectionDto result = await service.ReplaceMembersAsync(created.Id, new[] { valid, valid }, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Equal(1, result.MemberCount);
    }

    [Fact]
    public async Task List_AdminSeesAll_UserSeesVisible()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        ModelCollectionService service = CreateService(db, ProviderAllExist().Object);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "A-owned" }, userA, CancellationToken.None);
        ModelCollectionDto bShared = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "B-shared" }, userB, CancellationToken.None);
        _ = await service.SetSharedAsync(bShared.Id, shared: true, userB, callerIsAdmin: false, CancellationToken.None);
        _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "B-private" }, userB, CancellationToken.None);

        IReadOnlyList<ModelCollectionDto> visibleToA = await service.ListCollectionsAsync(userA, callerIsAdmin: false, CancellationToken.None);
        Assert.Equal(2, visibleToA.Count); // A-owned + B-shared

        IReadOnlyList<ModelCollectionDto> allForAdmin = await service.ListCollectionsAsync(Guid.NewGuid(), callerIsAdmin: true, CancellationToken.None);
        Assert.Equal(3, allForAdmin.Count);
    }
}
