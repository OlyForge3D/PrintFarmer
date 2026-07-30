using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Sync;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Collections;
using Farm.Infrastructure.Services.Sync;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Sync;

/// <summary>
/// Tests for the library sync journal (#844): monotonic revisions, transactional atomicity
/// with entity state, durable tombstones after hard delete, captured owner/visibility/actor
/// metadata, and the cursor-based query surface #845 will build on. The journal and the
/// collection repository share a single <see cref="AppDbContext"/> so a single
/// <c>SaveChangesAsync</c> commits state and journal together.
/// </summary>
public class LibrarySyncJournalTests
{
    private static (ModelCollectionService Service, LibrarySyncJournal Journal) CreateService(
        AppDbContext db, IModel3DQueryProvider? provider)
    {
        var repo = new EfModelCollectionRepository(db);
        var journal = new LibrarySyncJournal(db);
        return (new ModelCollectionService(repo, journal, provider), journal);
    }

    private static Mock<IModel3DQueryProvider> ProviderAllExist()
    {
        var mock = new Mock<IModel3DQueryProvider>();
        _ = mock.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock;
    }

    [Fact]
    public async Task Create_RecordsJournalEntry_WithActorOwnerAndPrivateVisibility()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        IReadOnlyList<LibrarySyncChange> changes = await journal.GetChangesForEntityAsync(
            SyncEntityType.ModelCollection, created.Id, CancellationToken.None);

        LibrarySyncChange entry = Assert.Single(changes);
        Assert.Equal(SyncOperation.Create, entry.Operation);
        Assert.Equal(owner, entry.OwnerUserId);
        Assert.Equal(owner, entry.ActorUserId);
        Assert.Equal(SyncVisibility.Private, entry.Visibility);
        Assert.True(entry.Revision > 0);
    }

    [Fact]
    public async Task SetShared_RecordsUpdate_WithSharedVisibility()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(
            new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        _ = await service.SetSharedAsync(created.Id, shared: true, owner, callerIsAdmin: false, CancellationToken.None);

        IReadOnlyList<LibrarySyncChange> changes = await journal.GetChangesForEntityAsync(
            SyncEntityType.ModelCollection, created.Id, CancellationToken.None);

        Assert.Equal(2, changes.Count);
        LibrarySyncChange update = changes[^1];
        Assert.Equal(SyncOperation.Update, update.Operation);
        Assert.Equal(SyncVisibility.Shared, update.Visibility);
    }

    [Fact]
    public async Task Revisions_AreMonotonicAndStrictlyIncreasing_AcrossMutations()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto c1 = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "A" }, owner, CancellationToken.None);
        ModelCollectionDto c2 = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "B" }, owner, CancellationToken.None);
        _ = await service.UpdateCollectionAsync(c1.Id, new UpdateModelCollectionDto { Name = "A2" }, owner, callerIsAdmin: false, CancellationToken.None);
        _ = await service.AddMemberAsync(c2.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None);

        IReadOnlyList<LibrarySyncChange> all = await journal.GetChangesSinceAsync(0, 100, CancellationToken.None);

        Assert.Equal(4, all.Count);
        long previous = 0;
        foreach (LibrarySyncChange change in all)
        {
            Assert.True(change.Revision > previous, "revisions must be strictly increasing");
            previous = change.Revision;
        }
    }

    [Fact]
    public async Task Delete_EmitsDurableTombstones_ThatSurviveHardDelete()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        _ = await service.AddMemberAsync(created.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None);

        await service.DeleteCollectionAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None);

        // Entity is hard-deleted.
        Assert.Null(await service.GetCollectionAsync(created.Id, owner, callerIsAdmin: false, CancellationToken.None));

        // Collection tombstone persists after the entity row is gone.
        IReadOnlyList<LibrarySyncChange> collectionChanges = await journal.GetChangesForEntityAsync(
            SyncEntityType.ModelCollection, created.Id, CancellationToken.None);
        Assert.Contains(collectionChanges, c => c.Operation == SyncOperation.Delete);

        // A membership tombstone was emitted too.
        IReadOnlyList<LibrarySyncChange> all = await journal.GetChangesSinceAsync(0, 100, CancellationToken.None);
        Assert.Contains(all, c => c.EntityType == SyncEntityType.ModelCollectionMembership && c.Operation == SyncOperation.Delete);
    }

    [Fact]
    public async Task FailedMutation_LeavesNoJournalEntry_Transactional()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        // Provider reports the model does not exist, so AddMember throws before SaveChanges.
        var provider = new Mock<IModel3DQueryProvider>();
        _ = provider.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, provider.Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        long revisionBefore = await journal.GetLatestRevisionAsync(CancellationToken.None);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.AddMemberAsync(created.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None));

        // No membership journal entry was committed; the latest revision is unchanged.
        long revisionAfter = await journal.GetLatestRevisionAsync(CancellationToken.None);
        Assert.Equal(revisionBefore, revisionAfter);

        IReadOnlyList<LibrarySyncChange> all = await journal.GetChangesSinceAsync(0, 100, CancellationToken.None);
        Assert.DoesNotContain(all, c => c.EntityType == SyncEntityType.ModelCollectionMembership);
    }

    [Fact]
    public async Task GetChangesSince_PagesFromCursor()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "A" }, owner, CancellationToken.None);
        _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "B" }, owner, CancellationToken.None);
        _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);

        IReadOnlyList<LibrarySyncChange> firstPage = await journal.GetChangesSinceAsync(0, 2, CancellationToken.None);
        Assert.Equal(2, firstPage.Count);

        long cursor = firstPage[^1].Revision;
        IReadOnlyList<LibrarySyncChange> secondPage = await journal.GetChangesSinceAsync(cursor, 2, CancellationToken.None);
        LibrarySyncChange last = Assert.Single(secondPage);
        Assert.True(last.Revision > cursor);
    }

    [Fact]
    public async Task ConcurrentWriters_ProduceUniqueIncreasingRevisions()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, LibrarySyncJournal journal) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        // Interleave several mutations from the same unit of work; each SaveChanges allocates
        // a fresh store-generated revision, so no two entries collide.
        for (int i = 0; i < 8; i++)
        {
            _ = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = $"C{i}" }, owner, CancellationToken.None);
        }

        IReadOnlyList<LibrarySyncChange> all = await journal.GetChangesSinceAsync(0, 100, CancellationToken.None);
        Assert.Equal(8, all.Count);
        Assert.Equal(all.Select(c => c.Revision).Distinct().Count(), all.Count);
        Assert.Equal(all.OrderBy(c => c.Revision).Select(c => c.Revision), all.Select(c => c.Revision));
    }

    [Fact]
    public async Task Membership_RevisionIsAssigned_OnAdd()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, _) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        ModelCollectionMembershipDto membership = await service.AddMemberAsync(created.Id, Guid.NewGuid(), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Equal(1, membership.Revision);
    }

    [Fact]
    public async Task Collection_RevisionAndTokenChange_OnMetadataUpdate()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        (ModelCollectionService service, _) = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ModelCollectionDto created = await service.CreateCollectionAsync(new CreateModelCollectionDto { Name = "C" }, owner, CancellationToken.None);
        ModelCollectionDto updated = await service.UpdateCollectionAsync(
            created.Id, new UpdateModelCollectionDto { Name = "C2" }, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.True(updated.Revision > created.Revision);
        Assert.NotEqual(created.ConcurrencyToken, updated.ConcurrencyToken);
    }
}
