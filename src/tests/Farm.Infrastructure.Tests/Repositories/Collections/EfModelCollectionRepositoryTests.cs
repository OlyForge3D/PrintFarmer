using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Collections;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.Collections;

/// <summary>
/// Repository tests for <see cref="EfModelCollectionRepository"/> backed by a SQLite
/// in-memory database, exercising relational behaviors (cascade delete, unique index,
/// ordering).
/// </summary>
public class EfModelCollectionRepositoryTests
{
    private static ModelCollection NewCollection(Guid ownerId, string name = "Coll", bool shared = false)
    {
        DateTime now = DateTime.UtcNow;
        return new ModelCollection
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerUserId = ownerId,
            IsShared = shared,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    [Fact]
    public async Task AddAndGetById_ReturnsCollection()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        ModelCollection collection = NewCollection(owner, "My Models");

        await repo.AddAsync(collection, CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        ModelCollection? fetched = await repo.GetByIdAsync(collection.Id, includeMemberships: false, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("My Models", fetched!.Name);
        Assert.Equal(owner, fetched.OwnerUserId);
    }

    [Fact]
    public async Task ListByOwner_ReturnsOnlyOwnedOrderedByName()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        await repo.AddAsync(NewCollection(owner, "Zebra"), CancellationToken.None);
        await repo.AddAsync(NewCollection(owner, "Alpha"), CancellationToken.None);
        await repo.AddAsync(NewCollection(other, "Beta"), CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<ModelCollection> owned = await repo.ListByOwnerAsync(owner, CancellationToken.None);

        Assert.Equal(2, owned.Count);
        Assert.Equal("Alpha", owned[0].Name);
        Assert.Equal("Zebra", owned[1].Name);
    }

    [Fact]
    public async Task ListVisible_ReturnsOwnedPlusShared()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        await repo.AddAsync(NewCollection(owner, "Owned"), CancellationToken.None);
        await repo.AddAsync(NewCollection(other, "OthersShared", shared: true), CancellationToken.None);
        await repo.AddAsync(NewCollection(other, "OthersPrivate"), CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<ModelCollection> visible = await repo.ListVisibleAsync(owner, CancellationToken.None);

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, c => c.Name == "Owned");
        Assert.Contains(visible, c => c.Name == "OthersShared");
        Assert.DoesNotContain(visible, c => c.Name == "OthersPrivate");
    }

    [Fact]
    public async Task RemoveCollection_CascadesToMemberships()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        ModelCollection collection = NewCollection(owner);
        await repo.AddAsync(collection, CancellationToken.None);
        await repo.AddMembershipAsync(new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            ModelId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        repo.Remove(collection);
        await repo.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<ModelCollectionMembership> memberships = await repo.ListMembershipsAsync(collection.Id, CancellationToken.None);
        Assert.Empty(memberships);
    }

    [Fact]
    public async Task ListMemberships_OrdersByCreatedAt()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        ModelCollection collection = NewCollection(owner);
        await repo.AddAsync(collection, CancellationToken.None);

        DateTime baseTime = DateTime.UtcNow;
        Guid firstModel = Guid.NewGuid();
        Guid secondModel = Guid.NewGuid();
        await repo.AddMembershipAsync(new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            ModelId = secondModel,
            CreatedAt = baseTime.AddMinutes(5),
            UpdatedAt = baseTime.AddMinutes(5)
        }, CancellationToken.None);
        await repo.AddMembershipAsync(new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            ModelId = firstModel,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        }, CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<ModelCollectionMembership> memberships = await repo.ListMembershipsAsync(collection.Id, CancellationToken.None);

        Assert.Equal(2, memberships.Count);
        Assert.Equal(firstModel, memberships[0].ModelId);
        Assert.Equal(secondModel, memberships[1].ModelId);
    }

    [Fact]
    public async Task GetMembership_ReturnsMatchOrNull()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        var repo = new EfModelCollectionRepository(db);
        Guid owner = Guid.NewGuid();
        ModelCollection collection = NewCollection(owner);
        Guid modelId = Guid.NewGuid();
        await repo.AddAsync(collection, CancellationToken.None);
        await repo.AddMembershipAsync(new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            ModelId = modelId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, CancellationToken.None);
        await repo.SaveChangesAsync(CancellationToken.None);

        ModelCollectionMembership? found = await repo.GetMembershipAsync(collection.Id, modelId, CancellationToken.None);
        ModelCollectionMembership? missing = await repo.GetMembershipAsync(collection.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Null(missing);
    }
}
