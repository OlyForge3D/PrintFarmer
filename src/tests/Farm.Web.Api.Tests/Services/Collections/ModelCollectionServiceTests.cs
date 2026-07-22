using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Collections;

public sealed class ModelCollectionServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly EfModelCollectionRepository _repository;
    private readonly Mock<IModel3DQueryProvider> _modelQueryMock;
    private readonly ModelCollectionService _service;

    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private static CollectionCaller Owner => new(OwnerId, false);
    private static CollectionCaller Other => new(OtherUserId, false);
    private static CollectionCaller Admin => new(Guid.NewGuid(), true);

    public ModelCollectionServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"CollectionsTest_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);
        _repository = new EfModelCollectionRepository(_context);
        _modelQueryMock = new Mock<IModel3DQueryProvider>();
        _modelQueryMock.Setup(q => q.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _service = new ModelCollectionService(_repository, NullLogger<ModelCollectionService>.Instance, _modelQueryMock.Object);
    }

    public void Dispose() => _context.Dispose();

    private async Task<ModelCollectionDto> CreateOwnedAsync(ModelCollectionVisibility visibility = ModelCollectionVisibility.Private)
    {
        ModelCollectionDto created = await _service.CreateAsync(Owner, new CreateModelCollectionDto { Name = "Fleet", Visibility = visibility }, CancellationToken.None);
        return created;
    }

    [Fact]
    public async Task CreateAsync_SetsOwnerAndDefaults()
    {
        ModelCollectionDto dto = await _service.CreateAsync(Owner, new CreateModelCollectionDto { Name = "  Boats  ", Description = "d" }, CancellationToken.None);

        Assert.Equal(OwnerId, dto.OwnerUserId);
        Assert.Equal("Boats", dto.Name);
        Assert.Equal(ModelCollectionVisibility.Private, dto.Visibility);
        Assert.Equal(0, dto.MemberCount);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateAsync(Owner, new CreateModelCollectionDto { Name = "   " }, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_Owner_CanReadPrivate()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        ModelCollectionDto fetched = await _service.GetAsync(Owner, created.Id, CancellationToken.None);

        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetAsync_OtherUser_PrivateCollection_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.GetAsync(Other, created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_OtherUser_SharedCollection_Succeeds()
    {
        ModelCollectionDto created = await CreateOwnedAsync(ModelCollectionVisibility.Shared);

        ModelCollectionDto fetched = await _service.GetAsync(Other, created.Id, CancellationToken.None);

        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetAsync_Admin_CanReadPrivate()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        ModelCollectionDto fetched = await _service.GetAsync(Admin, created.Id, CancellationToken.None);

        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetAsync_Missing_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<CollectionNotFoundException>(() =>
            _service.GetAsync(Owner, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_ReturnsOwnedAndShared_ExcludesOthersPrivate()
    {
        ModelCollectionDto owned = await CreateOwnedAsync();
        ModelCollectionDto othersShared = await _service.CreateAsync(Other, new CreateModelCollectionDto { Name = "Shared", Visibility = ModelCollectionVisibility.Shared }, CancellationToken.None);
        ModelCollectionDto othersPrivate = await _service.CreateAsync(Other, new CreateModelCollectionDto { Name = "Secret" }, CancellationToken.None);

        IReadOnlyList<ModelCollectionDto> list = await _service.ListAsync(Owner, CancellationToken.None);

        Assert.Contains(list, c => c.Id == owned.Id);
        Assert.Contains(list, c => c.Id == othersShared.Id);
        Assert.DoesNotContain(list, c => c.Id == othersPrivate.Id);
    }

    [Fact]
    public async Task ListAsync_Admin_ReturnsAll()
    {
        ModelCollectionDto owned = await CreateOwnedAsync();
        ModelCollectionDto othersPrivate = await _service.CreateAsync(Other, new CreateModelCollectionDto { Name = "Secret" }, CancellationToken.None);

        IReadOnlyList<ModelCollectionDto> list = await _service.ListAsync(Admin, CancellationToken.None);

        Assert.Contains(list, c => c.Id == owned.Id);
        Assert.Contains(list, c => c.Id == othersPrivate.Id);
    }

    [Fact]
    public async Task UpdateAsync_NonOwner_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.UpdateAsync(Other, created.Id, new UpdateModelCollectionDto { Name = "Nope" }, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_Owner_UpdatesNameAndDescription()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        ModelCollectionDto updated = await _service.UpdateAsync(Owner, created.Id, new UpdateModelCollectionDto { Name = "Renamed", Description = "x" }, CancellationToken.None);

        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("x", updated.Description);
    }

    [Fact]
    public async Task DeleteAsync_NonOwner_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.DeleteAsync(Other, created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_Owner_RemovesCollection()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await _service.DeleteAsync(Owner, created.Id, CancellationToken.None);

        await Assert.ThrowsAsync<CollectionNotFoundException>(() =>
            _service.GetAsync(Owner, created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ShareAsync_SetsVisibilityShared()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        ModelCollectionDto shared = await _service.ShareAsync(Owner, created.Id, CancellationToken.None);

        Assert.Equal(ModelCollectionVisibility.Shared, shared.Visibility);
    }

    [Fact]
    public async Task UnshareAsync_SetsVisibilityPrivate()
    {
        ModelCollectionDto created = await CreateOwnedAsync(ModelCollectionVisibility.Shared);

        ModelCollectionDto updated = await _service.UnshareAsync(Owner, created.Id, CancellationToken.None);

        Assert.Equal(ModelCollectionVisibility.Private, updated.Visibility);
    }

    [Fact]
    public async Task ShareAsync_NonOwner_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.ShareAsync(Other, created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AddMemberAsync_ValidModel_AddsAndBumpsCount()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid modelId = Guid.NewGuid();

        ModelCollectionMembershipDto membership = await _service.AddMemberAsync(Owner, created.Id, modelId, CancellationToken.None);

        Assert.Equal(modelId, membership.ModelId);
        ModelCollectionDto reread = await _service.GetAsync(Owner, created.Id, CancellationToken.None);
        Assert.Equal(1, reread.MemberCount);
    }

    [Fact]
    public async Task AddMemberAsync_Idempotent_DoesNotDuplicate()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid modelId = Guid.NewGuid();

        _ = await _service.AddMemberAsync(Owner, created.Id, modelId, CancellationToken.None);
        _ = await _service.AddMemberAsync(Owner, created.Id, modelId, CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> members = await _service.ListMembersAsync(Owner, created.Id, CancellationToken.None);
        Assert.Single(members);
    }

    [Fact]
    public async Task AddMemberAsync_UnknownModel_ThrowsModelNotFound()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid modelId = Guid.NewGuid();
        _modelQueryMock.Setup(q => q.ExistsAsync(modelId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<CollectionModelNotFoundException>(() =>
            _service.AddMemberAsync(Owner, created.Id, modelId, CancellationToken.None));
    }

    [Fact]
    public async Task AddMemberAsync_NonOwner_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.AddMemberAsync(Other, created.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task AddMemberAsync_NullQueryProvider_SkipsValidation()
    {
        var degraded = new ModelCollectionService(_repository, NullLogger<ModelCollectionService>.Instance, modelQuery: null);
        ModelCollectionDto created = await CreateOwnedAsync();

        ModelCollectionMembershipDto membership = await degraded.AddMemberAsync(Owner, created.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, membership.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovesMembership()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid modelId = Guid.NewGuid();
        _ = await _service.AddMemberAsync(Owner, created.Id, modelId, CancellationToken.None);

        await _service.RemoveMemberAsync(Owner, created.Id, modelId, CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> members = await _service.ListMembersAsync(Owner, created.Id, CancellationToken.None);
        Assert.Empty(members);
    }

    [Fact]
    public async Task RemoveMemberAsync_AbsentMember_IsNoOp()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await _service.RemoveMemberAsync(Owner, created.Id, Guid.NewGuid(), CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> members = await _service.ListMembersAsync(Owner, created.Id, CancellationToken.None);
        Assert.Empty(members);
    }

    [Fact]
    public async Task ReplaceMembersAsync_ComputesDiff()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid keep = Guid.NewGuid();
        Guid remove = Guid.NewGuid();
        Guid add = Guid.NewGuid();
        _ = await _service.AddMemberAsync(Owner, created.Id, keep, CancellationToken.None);
        _ = await _service.AddMemberAsync(Owner, created.Id, remove, CancellationToken.None);

        IReadOnlyList<ModelCollectionMembershipDto> result = await _service.ReplaceMembersAsync(Owner, created.Id, new[] { keep, add }, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, m => m.ModelId == keep);
        Assert.Contains(result, m => m.ModelId == add);
        Assert.DoesNotContain(result, m => m.ModelId == remove);
    }

    [Fact]
    public async Task ReplaceMembersAsync_UnknownModel_Throws()
    {
        ModelCollectionDto created = await CreateOwnedAsync();
        Guid bad = Guid.NewGuid();
        _modelQueryMock.Setup(q => q.ExistsAsync(bad, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<CollectionModelNotFoundException>(() =>
            _service.ReplaceMembersAsync(Owner, created.Id, new[] { bad }, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceMembersAsync_NonOwner_ThrowsAccessDenied()
    {
        ModelCollectionDto created = await CreateOwnedAsync();

        await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            _service.ReplaceMembersAsync(Other, created.Id, Array.Empty<Guid>(), CancellationToken.None));
    }
}
