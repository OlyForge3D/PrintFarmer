using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Sync;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Sync;

/// <summary>
/// Default <see cref="ILibrarySyncService"/>. Reuses the collection repository and the #844
/// journal so that apply mutations and their journal entries commit under a single unit of
/// work, preserving exactly-once journaling. Authorization mirrors the collection service's
/// owner-or-administrator policy; model references are validated through
/// <see cref="IModel3DQueryProvider"/> when the slicer module is loaded (graceful degradation
/// otherwise, matching the tag/collection precedent).
/// </summary>
public class LibrarySyncService(
    IModelCollectionRepository repository,
    ILibrarySyncJournal journal,
    IModel3DQueryProvider? model3DQuery = null) : ILibrarySyncService
{
    private readonly IModelCollectionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ILibrarySyncJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IModel3DQueryProvider? _model3DQuery = model3DQuery;

    /// <summary>Default page size when the caller does not request one.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Maximum page size a caller may request; larger requests are clamped.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Maximum number of operations accepted in a single apply batch.</summary>
    public const int MaxBatchSize = 500;

    /// <inheritdoc/>
    public async Task<LibrarySyncPullResultDto> PullAsync(string? cursor, int? limit, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        long afterRevision = SyncCursor.Decode(cursor);
        int pageSize = NormalizeLimit(limit);

        // Fetch one extra row to detect whether more changes remain beyond this page.
        IReadOnlyList<LibrarySyncChange> rows = await _journal.GetVisibleChangesSinceAsync(
            afterRevision, callerUserId, callerIsAdmin, pageSize + 1, ct);

        bool hasMore = rows.Count > pageSize;
        List<LibrarySyncChange> page = hasMore ? rows.Take(pageSize).ToList() : rows.ToList();

        long serverRevision = await _journal.GetLatestRevisionAsync(ct);

        // A stable resume cursor: the last returned revision, or the prior position when the
        // page is empty, so clients can poll forward without regressing.
        long lastRevision = page.Count > 0 ? page[^1].Revision : afterRevision;
        string? nextCursor = lastRevision > 0 ? SyncCursor.Encode(lastRevision) : null;

        return new LibrarySyncPullResultDto
        {
            Changes = page.Select(LibrarySyncChangeDto.FromDomain).ToList(),
            NextCursor = nextCursor,
            HasMore = hasMore,
            ServerRevision = serverRevision
        };
    }

    /// <inheritdoc/>
    public async Task<ApplySyncResultDto> ApplyAsync(ApplySyncRequestDto request, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ApplySyncOperationDto> operations = request.Operations ?? [];
        if (operations.Count == 0)
        {
            return new ApplySyncResultDto
            {
                Applied = [],
                ServerRevision = await _journal.GetLatestRevisionAsync(ct)
            };
        }

        if (operations.Count > MaxBatchSize)
        {
            throw new ArgumentException($"A sync batch may contain at most {MaxBatchSize} operations", nameof(request));
        }

        var applied = new List<AppliedSyncOperationDto>(operations.Count);
        var conflicts = new List<SyncConflictDto>();
        var touchedCollections = new List<ModelCollection>();

        foreach (ApplySyncOperationDto op in operations)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(op);

            switch (op.EntityType)
            {
                case SyncEntityType.ModelCollection:
                    await ApplyCollectionOperationAsync(op, callerUserId, callerIsAdmin, applied, conflicts, touchedCollections, ct);
                    break;
                case SyncEntityType.ModelCollectionMembership:
                    await ApplyMembershipOperationAsync(op, callerUserId, callerIsAdmin, applied, conflicts, ct);
                    break;
                default:
                    throw new ArgumentException($"Unsupported sync entity type: {op.EntityType}", nameof(request));
            }
        }

        if (conflicts.Count > 0)
        {
            // Nothing has been persisted; abandoning the unit of work rolls the batch back.
            long head = await _journal.GetLatestRevisionAsync(ct);
            throw new SyncConflictException(conflicts, head);
        }

        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw await BuildConcurrencyConflictAsync(ex, ct);
        }

        return new ApplySyncResultDto
        {
            Applied = applied,
            ServerRevision = await _journal.GetLatestRevisionAsync(ct)
        };
    }

    private async Task ApplyCollectionOperationAsync(
        ApplySyncOperationDto op,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        List<ModelCollection> touchedCollections,
        CancellationToken ct)
    {
        switch (op.Operation)
        {
            case SyncOperation.Create:
                await ApplyCollectionCreateAsync(op, callerUserId, callerIsAdmin, applied, conflicts, touchedCollections, ct);
                break;
            case SyncOperation.Update:
                await ApplyCollectionUpdateAsync(op, callerUserId, callerIsAdmin, applied, conflicts, touchedCollections, ct);
                break;
            case SyncOperation.Delete:
                await ApplyCollectionDeleteAsync(op, callerUserId, callerIsAdmin, applied, conflicts, touchedCollections, ct);
                break;
            default:
                throw new ArgumentException($"Unsupported collection operation: {op.Operation}", nameof(op));
        }
    }

    private async Task ApplyCollectionCreateAsync(
        ApplySyncOperationDto op,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        List<ModelCollection> touchedCollections,
        CancellationToken ct)
    {
        if (op.EntityId == Guid.Empty)
        {
            throw new ArgumentException("Collection create requires a non-empty entity id", nameof(op));
        }

        string name = (op.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name is required", nameof(op));
        }

        ModelCollection? existing = await _repository.GetByIdAsync(op.EntityId, includeMemberships: false, ct);
        if (existing is not null)
        {
            EnsureCanWrite(existing, callerUserId, callerIsAdmin);

            conflicts.Add(new SyncConflictDto
            {
                EntityType = SyncEntityType.ModelCollection,
                EntityId = op.EntityId,
                Reason = "A collection with this id already exists",
                Server = CollectionVersion(existing),
                Submitted = new SyncConflictVersionDto { Name = name, Description = op.Description, IsShared = op.IsShared }
            });
            return;
        }

        DateTime now = DateTime.UtcNow;
        var collection = new ModelCollection
        {
            Id = op.EntityId,
            Name = name,
            Description = op.Description,
            OwnerUserId = callerUserId,
            IsShared = op.IsShared ?? false,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1,
            ConcurrencyToken = Guid.NewGuid()
        };

        await _repository.AddAsync(collection, ct);
        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Create, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);
        touchedCollections.Add(collection);

        applied.Add(new AppliedSyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Create,
            EntityId = collection.Id,
            Revision = collection.Revision,
            Merged = false
        });
    }

    private async Task ApplyCollectionUpdateAsync(
        ApplySyncOperationDto op,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        List<ModelCollection> touchedCollections,
        CancellationToken ct)
    {
        RequireConcurrencyToken(op);

        ModelCollection? collection = await _repository.GetByIdAsync(op.EntityId, includeMemberships: false, ct);
        if (collection is null)
        {
            conflicts.Add(MissingCollectionConflict(op));
            return;
        }

        EnsureCanWrite(collection, callerUserId, callerIsAdmin);

        if (TryAddConcurrencyConflict(op, collection, conflicts))
        {
            return;
        }

        string name = (op.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name is required", nameof(op));
        }

        collection.Name = name;
        collection.Description = op.Description;
        if (op.IsShared.HasValue)
        {
            collection.IsShared = op.IsShared.Value;
        }

        collection.UpdatedAt = DateTime.UtcNow;
        BumpCollection(collection);
        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Update, collection.OwnerUserId, VisibilityOf(collection), callerUserId, collection.UpdatedAt);
        touchedCollections.Add(collection);

        applied.Add(new AppliedSyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Update,
            EntityId = collection.Id,
            Revision = collection.Revision,
            Merged = false
        });
    }

    private async Task ApplyCollectionDeleteAsync(
        ApplySyncOperationDto op,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        List<ModelCollection> touchedCollections,
        CancellationToken ct)
    {
        ModelCollection? collection = await _repository.GetByIdAsync(op.EntityId, includeMemberships: false, ct);
        if (collection is null)
        {
            // Deleting something already gone is an idempotent no-op (repeated request / converged).
            applied.Add(new AppliedSyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollection,
                Operation = SyncOperation.Delete,
                EntityId = op.EntityId,
                Revision = 0,
                Merged = true
            });
            return;
        }

        EnsureCanWrite(collection, callerUserId, callerIsAdmin);
        RequireConcurrencyToken(op);

        if (TryAddConcurrencyConflict(op, collection, conflicts))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        SyncVisibility visibility = VisibilityOf(collection);
        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collection.Id, ct);
        foreach (ModelCollectionMembership membership in memberships)
        {
            _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Delete, collection.OwnerUserId, visibility, callerUserId, now);
        }

        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Delete, collection.OwnerUserId, visibility, callerUserId, now);
        _repository.Remove(collection);
        touchedCollections.Add(collection);

        applied.Add(new AppliedSyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollection,
            Operation = SyncOperation.Delete,
            EntityId = collection.Id,
            Revision = collection.Revision,
            Merged = false
        });
    }

    private async Task ApplyMembershipOperationAsync(
        ApplySyncOperationDto op,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        CancellationToken ct)
    {
        Guid collectionId = op.CollectionId ?? Guid.Empty;
        Guid modelId = op.ModelId ?? Guid.Empty;
        if (collectionId == Guid.Empty || modelId == Guid.Empty)
        {
            throw new ArgumentException("Membership operations require both a collection id and a model id", nameof(op));
        }

        switch (op.Operation)
        {
            case SyncOperation.Create:
                await ApplyMembershipAddAsync(op, collectionId, modelId, callerUserId, callerIsAdmin, applied, conflicts, ct);
                break;
            case SyncOperation.Delete:
                await ApplyMembershipRemoveAsync(collectionId, modelId, callerUserId, callerIsAdmin, applied, ct);
                break;
            default:
                throw new ArgumentException($"Unsupported membership operation: {op.Operation}", nameof(op));
        }
    }

    private async Task ApplyMembershipAddAsync(
        ApplySyncOperationDto op,
        Guid collectionId,
        Guid modelId,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        List<SyncConflictDto> conflicts,
        CancellationToken ct)
    {
        ModelCollection? collection = await _repository.GetByIdAsync(collectionId, includeMemberships: false, ct);
        if (collection is null)
        {
            conflicts.Add(new SyncConflictDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                EntityId = op.EntityId,
                Reason = "The target collection no longer exists",
                Server = new SyncConflictVersionDto { Exists = false },
                Submitted = new SyncConflictVersionDto { Exists = true }
            });
            return;
        }

        EnsureCanWrite(collection, callerUserId, callerIsAdmin);
        await ValidateModelExistsAsync(modelId, ct);

        ModelCollectionMembership? existing = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (existing is not null)
        {
            // Adding a member that already exists is a genuinely independent, idempotent change:
            // auto-merge without a duplicate journal entry (exactly-once).
            applied.Add(new AppliedSyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Create,
                EntityId = existing.Id,
                Revision = existing.Revision,
                Merged = true
            });
            return;
        }

        DateTime now = DateTime.UtcNow;
        var membership = new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            ModelId = modelId,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        };

        await _repository.AddMembershipAsync(membership, ct);
        _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Create, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);

        applied.Add(new AppliedSyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Create,
            EntityId = membership.Id,
            Revision = membership.Revision,
            Merged = false
        });
    }

    private async Task ApplyMembershipRemoveAsync(
        Guid collectionId,
        Guid modelId,
        Guid callerUserId,
        bool callerIsAdmin,
        List<AppliedSyncOperationDto> applied,
        CancellationToken ct)
    {
        ModelCollection? collection = await _repository.GetByIdAsync(collectionId, includeMemberships: false, ct);
        if (collection is null)
        {
            // Collection gone => membership gone: idempotent no-op.
            applied.Add(new AppliedSyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Delete,
                EntityId = Guid.Empty,
                Revision = 0,
                Merged = true
            });
            return;
        }

        EnsureCanWrite(collection, callerUserId, callerIsAdmin);

        ModelCollectionMembership? existing = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (existing is null)
        {
            // Removing a member that is not present is an idempotent no-op (repeated request).
            applied.Add(new AppliedSyncOperationDto
            {
                EntityType = SyncEntityType.ModelCollectionMembership,
                Operation = SyncOperation.Delete,
                EntityId = Guid.Empty,
                Revision = 0,
                Merged = true
            });
            return;
        }

        DateTime now = DateTime.UtcNow;
        _repository.RemoveMembership(existing);
        _journal.Record(SyncEntityType.ModelCollectionMembership, existing.Id, SyncOperation.Delete, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);

        applied.Add(new AppliedSyncOperationDto
        {
            EntityType = SyncEntityType.ModelCollectionMembership,
            Operation = SyncOperation.Delete,
            EntityId = existing.Id,
            Revision = existing.Revision,
            Merged = false
        });
    }

    private async Task<SyncConflictException> BuildConcurrencyConflictAsync(DbUpdateConcurrencyException ex, CancellationToken ct)
    {
        var conflictList = new List<SyncConflictDto>();
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in ex.Entries)
        {
            if (entry.Entity is not ModelCollection submitted)
            {
                continue;
            }

            Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues? dbValues = await entry.GetDatabaseValuesAsync(ct);
            SyncConflictVersionDto server = dbValues is null
                ? new SyncConflictVersionDto { Exists = false }
                : new SyncConflictVersionDto
                {
                    Exists = true,
                    Revision = dbValues.GetValue<long>(nameof(ModelCollection.Revision)),
                    ConcurrencyToken = dbValues.GetValue<Guid>(nameof(ModelCollection.ConcurrencyToken)),
                    Name = dbValues.GetValue<string?>(nameof(ModelCollection.Name)),
                    Description = dbValues.GetValue<string?>(nameof(ModelCollection.Description)),
                    IsShared = dbValues.GetValue<bool>(nameof(ModelCollection.IsShared))
                };

            conflictList.Add(new SyncConflictDto
            {
                EntityType = SyncEntityType.ModelCollection,
                EntityId = submitted.Id,
                Reason = "The collection was modified by a concurrent writer",
                Server = server,
                Submitted = CollectionVersion(submitted)
            });
        }

        long head = await _journal.GetLatestRevisionAsync(ct);
        return new SyncConflictException(conflictList, head);
    }

    private static int NormalizeLimit(int? limit)
    {
        if (limit is null || limit.Value <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(limit.Value, MaxPageSize);
    }

    private static void RequireConcurrencyToken(ApplySyncOperationDto op)
    {
        if (op.BaseRevision is null && op.ConcurrencyToken is null)
        {
            throw new ArgumentException("A base revision or concurrency token is required for collection update/delete", nameof(op));
        }
    }

    private static bool TryAddConcurrencyConflict(ApplySyncOperationDto op, ModelCollection collection, List<SyncConflictDto> conflicts)
    {
        bool revisionMismatch = op.BaseRevision.HasValue && op.BaseRevision.Value != collection.Revision;
        bool tokenMismatch = op.ConcurrencyToken.HasValue && op.ConcurrencyToken.Value != collection.ConcurrencyToken;
        if (!revisionMismatch && !tokenMismatch)
        {
            return false;
        }

        conflicts.Add(new SyncConflictDto
        {
            EntityType = SyncEntityType.ModelCollection,
            EntityId = collection.Id,
            Reason = "The submitted base revision or concurrency token is stale",
            Server = CollectionVersion(collection),
            Submitted = new SyncConflictVersionDto
            {
                Revision = op.BaseRevision,
                ConcurrencyToken = op.ConcurrencyToken,
                Name = op.Name,
                Description = op.Description,
                IsShared = op.IsShared
            }
        });
        return true;
    }

    private static SyncConflictDto MissingCollectionConflict(ApplySyncOperationDto op)
    {
        return new SyncConflictDto
        {
            EntityType = SyncEntityType.ModelCollection,
            EntityId = op.EntityId,
            Reason = "The collection no longer exists",
            Server = new SyncConflictVersionDto { Exists = false },
            Submitted = new SyncConflictVersionDto
            {
                Exists = true,
                Revision = op.BaseRevision,
                ConcurrencyToken = op.ConcurrencyToken,
                Name = op.Name,
                Description = op.Description,
                IsShared = op.IsShared
            }
        };
    }

    private static SyncConflictVersionDto CollectionVersion(ModelCollection collection)
    {
        return new SyncConflictVersionDto
        {
            Exists = true,
            Revision = collection.Revision,
            ConcurrencyToken = collection.ConcurrencyToken,
            Name = collection.Name,
            Description = collection.Description,
            IsShared = collection.IsShared
        };
    }

    private async Task ValidateModelExistsAsync(Guid modelId, CancellationToken ct)
    {
        // When the slicer module is absent the provider is null; validation degrades gracefully.
        if (_model3DQuery is null)
        {
            return;
        }

        if (!await _model3DQuery.ExistsAsync(modelId, ct))
        {
            throw new CollectionModelValidationException([modelId]);
        }
    }

    private static void EnsureCanWrite(ModelCollection collection, Guid callerUserId, bool callerIsAdmin)
    {
        if (callerIsAdmin || collection.OwnerUserId == callerUserId)
        {
            return;
        }

        throw new CollectionAccessDeniedException();
    }

    private static SyncVisibility VisibilityOf(ModelCollection collection)
        => collection.IsShared ? SyncVisibility.Shared : SyncVisibility.Private;

    private static void BumpCollection(ModelCollection collection)
    {
        collection.Revision++;
        collection.ConcurrencyToken = Guid.NewGuid();
    }
}
