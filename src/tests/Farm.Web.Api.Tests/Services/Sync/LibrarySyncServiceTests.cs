using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Sync;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Sync;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Sync;

/// <summary>
/// Service tests for <see cref="LibrarySyncService"/> (#845): cursor-based pull with
/// deterministic bounded paging and visibility/owner scoping, and transactional batch apply
/// with optimistic concurrency, structured conflicts, membership auto-merge, exactly-once
/// journaling, and all-or-nothing rollback. Tests run the real repository and journal over a
/// SQLite in-memory database with a mocked <see cref="IModel3DQueryProvider"/>.
/// </summary>
public class LibrarySyncServiceTests
{
    private static LibrarySyncService CreateService(AppDbContext db, IModel3DQueryProvider? provider)
    {
        var repo = new EfModelCollectionRepository(db);
        var journal = new LibrarySyncJournal(db);
        return new LibrarySyncService(repo, journal, provider);
    }

    private static Mock<IModel3DQueryProvider> ProviderAllExist()
    {
        var mock = new Mock<IModel3DQueryProvider>();
        _ = mock.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock;
    }

    private static ApplySyncRequestDto Batch(params ApplySyncOperationDto[] ops)
        => new() { Operations = ops };

    private static ApplySyncOperationDto CreateCollectionOp(Guid id, string name, bool isShared = false, string? description = null)
        => new()
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Create,
            EntityId = id,
            Name = name,
            Description = description,
            IsShared = isShared
        };

    // ---- Helpers to seed a collection directly via an apply so revision/token are realistic ----

    private static async Task<AppliedSyncOperationDto> SeedCollectionAsync(
        LibrarySyncService service, Guid id, Guid owner, string name = "C", bool isShared = false)
    {
        ApplySyncResultDto result = await service.ApplyAsync(
            Batch(CreateCollectionOp(id, name, isShared)), owner, callerIsAdmin: false, CancellationToken.None);
        return result.Applied[0];
    }

    // ======================= PULL =======================

    [Fact]
    public async Task Pull_FromBeginning_ReturnsChangesAscending()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        _ = await SeedCollectionAsync(service, Guid.NewGuid(), owner, "A");
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), owner, "B");

        LibrarySyncPullResultDto page = await service.PullAsync(null, null, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Equal(2, page.Changes.Count);
        Assert.True(page.Changes[0].Revision < page.Changes[1].Revision);
        Assert.False(page.HasMore);
        Assert.Equal(page.Changes[^1].Revision, page.ServerRevision);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Pull_WithLimit_PagesDeterministicallyViaCursor()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            _ = await SeedCollectionAsync(service, Guid.NewGuid(), owner, $"C{i}");
        }

        LibrarySyncPullResultDto first = await service.PullAsync(null, 2, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Equal(2, first.Changes.Count);
        Assert.True(first.HasMore);

        LibrarySyncPullResultDto second = await service.PullAsync(first.NextCursor, 2, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Equal(2, second.Changes.Count);
        Assert.True(second.HasMore);
        Assert.True(second.Changes[0].Revision > first.Changes[^1].Revision);

        LibrarySyncPullResultDto third = await service.PullAsync(second.NextCursor, 2, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Single(third.Changes);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task Pull_AtEndCursor_ReturnsEmptyStableCursor()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), owner);

        LibrarySyncPullResultDto page = await service.PullAsync(null, null, owner, callerIsAdmin: false, CancellationToken.None);
        LibrarySyncPullResultDto atEnd = await service.PullAsync(page.NextCursor, null, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Empty(atEnd.Changes);
        Assert.False(atEnd.HasMore);
        // Cursor holds position so the client can keep polling forward without regressing.
        Assert.Equal(page.NextCursor, atEnd.NextCursor);
    }

    [Fact]
    public async Task Pull_MalformedCursor_ThrowsInvalidSyncCursor()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        _ = await Assert.ThrowsAsync<InvalidSyncCursorException>(() =>
            service.PullAsync("!!!not-a-cursor!!!", null, Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Pull_NonAdmin_SeesOwnAndSharedButNotOthersPrivate()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();

        _ = await SeedCollectionAsync(service, Guid.NewGuid(), alice, "alice-private");
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), bob, "bob-private");
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), bob, "bob-shared", isShared: true);

        LibrarySyncPullResultDto alicePage = await service.PullAsync(null, null, alice, callerIsAdmin: false, CancellationToken.None);

        Assert.All(alicePage.Changes, c =>
            Assert.True(c.OwnerUserId == alice || c.Visibility == SyncVisibility.Shared || c.OwnerUserId is null));
        Assert.DoesNotContain(alicePage.Changes, c => c.OwnerUserId == bob && c.Visibility == SyncVisibility.Private);
        // Alice's own private + Bob's shared = 2 visible changes.
        Assert.Equal(2, alicePage.Changes.Count);
    }

    [Fact]
    public async Task Pull_CrossUserIsolation_ForgedCursorCannotLeakOthersChanges()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();

        _ = await SeedCollectionAsync(service, Guid.NewGuid(), bob, "bob-private");

        // Alice pulls from the very beginning (cursor 0) — the most permissive position.
        LibrarySyncPullResultDto alicePage = await service.PullAsync(null, null, alice, callerIsAdmin: false, CancellationToken.None);

        Assert.Empty(alicePage.Changes);
        // Server revision still reflects the global head even though Alice can see none of it.
        Assert.True(alicePage.ServerRevision > 0);
    }

    [Fact]
    public async Task Pull_Admin_SeesAllUsersChanges()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid alice = Guid.NewGuid();
        Guid bob = Guid.NewGuid();

        _ = await SeedCollectionAsync(service, Guid.NewGuid(), alice, "alice-private");
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), bob, "bob-private");

        LibrarySyncPullResultDto adminPage = await service.PullAsync(null, null, Guid.NewGuid(), callerIsAdmin: true, CancellationToken.None);

        Assert.Equal(2, adminPage.Changes.Count);
    }

    [Fact]
    public async Task Pull_IncludesDeletionTombstones()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();

        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner);
        _ = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Delete,
            EntityId = id,
            BaseRevision = created.Revision
        }), owner, callerIsAdmin: false, CancellationToken.None);

        LibrarySyncPullResultDto page = await service.PullAsync(null, null, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Contains(page.Changes, c => c.EntityId == id && c.Operation == SyncOperation.Delete);
    }

    [Fact]
    public async Task Pull_LimitClampedToMaxPageSize()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, Guid.NewGuid(), owner);

        // A limit above the maximum must not throw and must still return results.
        LibrarySyncPullResultDto page = await service.PullAsync(null, LibrarySyncService.MaxPageSize + 5000, owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Single(page.Changes);
    }

    // ======================= APPLY: collections =======================

    [Fact]
    public async Task Apply_CreateCollection_Succeeds_AndJournals()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();

        ApplySyncResultDto result = await service.ApplyAsync(
            Batch(CreateCollectionOp(id, "New")), owner, callerIsAdmin: false, CancellationToken.None);

        AppliedSyncOperationDto applied = Assert.Single(result.Applied);
        Assert.Equal(id, applied.EntityId);
        Assert.Equal(1, applied.Revision);
        Assert.False(applied.Merged);

        ModelCollection? stored = await db.ModelCollections.FindAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(owner, stored!.OwnerUserId);
        Assert.True(result.ServerRevision > 0);
    }

    [Fact]
    public async Task Apply_CreateCollection_DuplicateId_Conflicts()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner, "Existing");

        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(CreateCollectionOp(id, "Again")), owner, callerIsAdmin: false, CancellationToken.None));

        SyncConflictDto conflict = Assert.Single(ex.Conflicts);
        Assert.Equal(id, conflict.EntityId);
        Assert.True(conflict.Server?.Exists);
    }

    [Fact]
    public async Task Apply_CreateCollection_DuplicateIdOwnedByOtherUser_Rejected()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, userA, "Secret", isShared: false);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.ApplyAsync(Batch(CreateCollectionOp(id, "Hijack")), userB, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_CreateCollection_BlankName_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyAsync(Batch(CreateCollectionOp(Guid.NewGuid(), "   ")), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_UpdateCollection_WithMatchingRevision_BumpsAndJournals()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner, "Before");

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Update,
            EntityId = id,
            BaseRevision = created.Revision,
            Name = "After"
        }), owner, callerIsAdmin: false, CancellationToken.None);

        AppliedSyncOperationDto applied = Assert.Single(result.Applied);
        Assert.Equal(created.Revision + 1, applied.Revision);

        ModelCollection? stored = await db.ModelCollections.FindAsync(id);
        Assert.Equal("After", stored!.Name);
    }

    [Fact]
    public async Task Apply_UpdateCollection_StaleRevision_Returns409WithServerAndSubmitted()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner, "Server");

        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = id,
                BaseRevision = created.Revision + 99, // stale
                Name = "Client"
            }), owner, callerIsAdmin: false, CancellationToken.None));

        SyncConflictDto conflict = Assert.Single(ex.Conflicts);
        Assert.Equal(created.Revision, conflict.Server?.Revision);
        Assert.Equal("Server", conflict.Server?.Name);
        Assert.Equal("Client", conflict.Submitted?.Name);
        Assert.Equal(created.Revision + 99, conflict.Submitted?.Revision);
    }

    [Fact]
    public async Task Apply_UpdateCollection_StaleToken_Conflicts()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner, "Server");

        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = id,
                ConcurrencyToken = Guid.NewGuid(), // wrong token
                Name = "Client"
            }), owner, callerIsAdmin: false, CancellationToken.None));

        _ = Assert.Single(ex.Conflicts);
    }

    [Fact]
    public async Task Apply_UpdateCollection_WithoutBaseRevisionOrToken_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = id,
                Name = "NoConcurrency"
            }), owner, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_UpdateCollection_Missing_Conflicts()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = Guid.NewGuid(),
                BaseRevision = 1,
                Name = "Ghost"
            }), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));

        SyncConflictDto conflict = Assert.Single(ex.Conflicts);
        Assert.False(conflict.Server?.Exists);
    }

    [Fact]
    public async Task Apply_UpdateCollection_NonOwner_Rejected()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = id,
                BaseRevision = created.Revision,
                Name = "Hijack"
            }), stranger, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_UpdateCollection_Admin_CanWriteOthers()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner);

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Update,
            EntityId = id,
            BaseRevision = created.Revision,
            Name = "AdminEdit"
        }), Guid.NewGuid(), callerIsAdmin: true, CancellationToken.None);

        Assert.Equal(created.Revision + 1, result.Applied[0].Revision);
    }

    [Fact]
    public async Task Apply_DeleteCollection_Succeeds_AndTombstonesMemberships()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        AppliedSyncOperationDto created = await SeedCollectionAsync(service, id, owner);

        _ = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            CollectionId = id,
            ModelId = modelId
        }), owner, callerIsAdmin: false, CancellationToken.None);

        _ = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Delete,
            EntityId = id,
            BaseRevision = created.Revision
        }), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.Null(await db.ModelCollections.FindAsync(id));
        // The membership tombstone is keyed by membership id, so assert a delete exists in the stream.
        LibrarySyncPullResultDto page = await service.PullAsync(null, 500, owner, callerIsAdmin: false, CancellationToken.None);
        Assert.Contains(page.Changes, c => c.EntityType == SyncEntityType.ModelCollectionMembership && c.Operation == SyncOperation.Delete);
    }

    [Fact]
    public async Task Apply_DeleteCollection_AlreadyGone_IsIdempotentMerge()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Delete,
            EntityId = Guid.NewGuid(),
            BaseRevision = 1
        }), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.True(result.Applied[0].Merged);
    }

    // ======================= APPLY: memberships =======================

    [Fact]
    public async Task Apply_AddMembership_Succeeds()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            CollectionId = id,
            ModelId = Guid.NewGuid()
        }), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.False(result.Applied[0].Merged);
        Assert.NotEqual(Guid.Empty, result.Applied[0].EntityId);
    }

    [Fact]
    public async Task Apply_AddMembership_Duplicate_AutoMerges_WithoutDuplicateJournal()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        ApplySyncOperationDto add() => new()
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            CollectionId = id,
            ModelId = modelId
        };

        _ = await service.ApplyAsync(Batch(add()), owner, callerIsAdmin: false, CancellationToken.None);
        ApplySyncResultDto second = await service.ApplyAsync(Batch(add()), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.True(second.Applied[0].Merged);

        var journal = new LibrarySyncJournal(db);
        IReadOnlyList<LibrarySyncChange> memberships = (await journal.GetChangesSinceAsync(0, 500, CancellationToken.None))
            .Where(c => c.EntityType == SyncEntityType.ModelCollectionMembership && c.Operation == SyncOperation.Create).ToList();
        // Exactly-once: only the first genuine add journaled.
        _ = Assert.Single(memberships);
    }

    [Fact]
    public async Task Apply_AddMembership_MissingCollection_Conflicts()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Create,
                CollectionId = Guid.NewGuid(),
                ModelId = Guid.NewGuid()
            }), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));

        _ = Assert.Single(ex.Conflicts);
    }

    [Fact]
    public async Task Apply_AddMembership_InvalidModel_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        var provider = new Mock<IModel3DQueryProvider>();
        _ = provider.Setup(p => p.ExistsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        LibrarySyncService service = CreateService(db, provider.Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        _ = await Assert.ThrowsAsync<CollectionModelValidationException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Create,
                CollectionId = id,
                ModelId = Guid.NewGuid()
            }), owner, callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_AddMembership_NonOwner_Rejected()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        _ = await Assert.ThrowsAsync<CollectionAccessDeniedException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Create,
                CollectionId = id,
                ModelId = Guid.NewGuid()
            }), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_RemoveMembership_Succeeds()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);
        _ = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            CollectionId = id,
            ModelId = modelId
        }), owner, callerIsAdmin: false, CancellationToken.None);

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Delete,
            CollectionId = id,
            ModelId = modelId
        }), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.False(result.Applied[0].Merged);
        Assert.Null(await db.ModelCollectionMemberships.FirstOrDefaultAsync(m => m.CollectionId == id && m.ModelId == modelId));
    }

    [Fact]
    public async Task Apply_RemoveMembership_Absent_IsIdempotentMerge()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Delete,
            CollectionId = id,
            ModelId = Guid.NewGuid()
        }), owner, callerIsAdmin: false, CancellationToken.None);

        Assert.True(result.Applied[0].Merged);
    }

    [Fact]
    public async Task Apply_RemoveMembership_CollectionGone_IsIdempotentMerge()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        ApplySyncResultDto result = await service.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Delete,
            CollectionId = Guid.NewGuid(),
            ModelId = Guid.NewGuid()
        }), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None);

        Assert.True(result.Applied[0].Merged);
    }

    [Fact]
    public async Task Apply_MembershipRequiresBothIds_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Create,
                CollectionId = Guid.NewGuid()
                // ModelId missing
            }), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));
    }

    // ======================= APPLY: batch semantics =======================

    [Fact]
    public async Task Apply_EmptyBatch_ReturnsEmptyWithServerRevision()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);

        ApplySyncResultDto result = await service.ApplyAsync(new ApplySyncRequestDto(), Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal(0, result.ServerRevision);
    }

    [Fact]
    public async Task Apply_BatchTooLarge_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        var ops = new List<ApplySyncOperationDto>();
        for (int i = 0; i < LibrarySyncService.MaxBatchSize + 1; i++)
        {
            ops.Add(CreateCollectionOp(Guid.NewGuid(), $"C{i}"));
        }

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyAsync(new ApplySyncRequestDto { Operations = ops }, Guid.NewGuid(), callerIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_BatchWithOneConflict_RollsBackEverything()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid goodId = Guid.NewGuid();
        Guid existingId = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, existingId, owner, "Existing");

        long revisionBefore = (await service.PullAsync(null, 500, owner, callerIsAdmin: false, CancellationToken.None)).ServerRevision;

        // One valid create + one conflicting create (duplicate id) => whole batch must roll back.
        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service.ApplyAsync(Batch(
                CreateCollectionOp(goodId, "Good"),
                CreateCollectionOp(existingId, "Dup")), owner, callerIsAdmin: false, CancellationToken.None));

        _ = Assert.Single(ex.Conflicts);
        // The valid create must NOT have persisted (query the store, bypassing the change tracker).
        Assert.False(await db.ModelCollections.AsNoTracking().AnyAsync(c => c.Id == goodId));
        // Journal head unchanged (nothing committed).
        long revisionAfter = (await service.PullAsync(null, 500, owner, callerIsAdmin: false, CancellationToken.None)).ServerRevision;
        Assert.Equal(revisionBefore, revisionAfter);
    }

    [Fact]
    public async Task Apply_RepeatedIdenticalMembershipAdd_IsIdempotent()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = await SeedCollectionAsync(service, id, owner);

        ApplySyncOperationDto add() => new()
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            CollectionId = id,
            ModelId = modelId
        };

        _ = await service.ApplyAsync(Batch(add()), owner, callerIsAdmin: false, CancellationToken.None);
        _ = await service.ApplyAsync(Batch(add()), owner, callerIsAdmin: false, CancellationToken.None);

        int count = await db.ModelCollectionMemberships.CountAsync(m => m.CollectionId == id && m.ModelId == modelId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Apply_CancellationRequested_Throws()
    {
        using AppDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        LibrarySyncService service = CreateService(db, ProviderAllExist().Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ApplyAsync(Batch(CreateCollectionOp(Guid.NewGuid(), "C")), Guid.NewGuid(), callerIsAdmin: false, cts.Token));
    }

    // ======================= APPLY: concurrent writers =======================

    [Fact]
    public async Task Apply_ConcurrentUpdate_LosesToConcurrencyToken_Returns409()
    {
        // Two contexts over one shared in-memory database simulate two racing writers.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var db1 = new AppDbContext(opts);
        _ = db1.Database.EnsureCreated();
        using var db2 = new AppDbContext(opts);

        Guid owner = Guid.NewGuid();
        Guid id = Guid.NewGuid();

        var service1 = new LibrarySyncService(new EfModelCollectionRepository(db1), new LibrarySyncJournal(db1), ProviderAllExist().Object);
        AppliedSyncOperationDto created = await SeedCollectionAsync(service1, id, owner);

        // Writer 2 loads the same base revision.
        var service2 = new LibrarySyncService(new EfModelCollectionRepository(db2), new LibrarySyncJournal(db2), ProviderAllExist().Object);

        // Writer 1 commits an update, advancing the stored ConcurrencyToken.
        _ = await service1.ApplyAsync(Batch(new ApplySyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Update,
            EntityId = id,
            BaseRevision = created.Revision,
            Name = "Winner"
        }), owner, callerIsAdmin: false, CancellationToken.None);

        // Writer 2 submits against the now-stale base revision; its in-memory read predates
        // writer 1's commit, so the optimistic base-revision guard detects the conflict.
        SyncConflictException ex = await Assert.ThrowsAsync<SyncConflictException>(() =>
            service2.ApplyAsync(Batch(new ApplySyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Update,
                EntityId = id,
                BaseRevision = created.Revision,
                Name = "Loser"
            }), owner, callerIsAdmin: false, CancellationToken.None));

        _ = Assert.Single(ex.Conflicts);

        ModelCollection? stored = await db1.ModelCollections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        Assert.Equal("Winner", stored!.Name);
    }
}
