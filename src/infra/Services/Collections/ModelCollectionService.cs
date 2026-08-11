using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Sync;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Sync;

namespace Farm.Infrastructure.Services.Collections;

/// <summary>
/// Default implementation of <see cref="IModelCollectionService"/>. Authorization
/// (owner-or-administrator) is enforced here so that every entry point shares a single
/// policy. Model references are validated through <see cref="IModel3DQueryProvider"/>;
/// when the slicer module is not loaded the provider is absent and validation degrades
/// gracefully, mirroring the tag repository precedent.
/// </summary>
/// <remarks>
/// Every mutation records a <see cref="ILibrarySyncJournal"/> entry and bumps the affected
/// entity's revision/concurrency metadata within the same unit of work, so the collection
/// state and the sync journal are committed by a single <c>SaveChangesAsync</c> and cannot
/// diverge (#844).
/// </remarks>
public class ModelCollectionService(
    IModelCollectionRepository repository,
    ILibrarySyncJournal journal,
    IModel3DQueryProvider? model3DQuery = null) : IModelCollectionService
{
    private readonly IModelCollectionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ILibrarySyncJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IModel3DQueryProvider? _model3DQuery = model3DQuery;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionDto>> ListCollectionsAsync(Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        IReadOnlyList<ModelCollection> collections = callerIsAdmin
            ? await _repository.ListAllAsync(ct)
            : await _repository.ListVisibleAsync(callerUserId, ct);

        var result = new List<ModelCollectionDto>(collections.Count);
        foreach (ModelCollection collection in collections)
        {
            IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collection.Id, ct);
            result.Add(MapToDto(collection, memberships));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto?> GetCollectionAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection? collection = await _repository.GetByIdAsync(collectionId, includeMemberships: true, ct);
        if (collection is null)
        {
            return null;
        }

        EnsureCanRead(collection, callerUserId, callerIsAdmin);
        IReadOnlyList<ModelCollectionMembership> memberships = OrderMemberships(collection.Memberships);
        return MapToDto(collection, memberships);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> CreateCollectionAsync(CreateModelCollectionDto dto, Guid callerUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        string name = (dto.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name is required", nameof(dto));
        }

        DateTime now = DateTime.UtcNow;
        var collection = new ModelCollection
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = dto.Description,
            OwnerUserId = callerUserId,
            IsShared = false,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1,
            ConcurrencyToken = Guid.NewGuid()
        };

        await _repository.AddAsync(collection, ct);
        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Create, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);
        await _repository.SaveChangesAsync(ct);

        return MapToDto(collection, []);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> UpdateCollectionAsync(Guid collectionId, UpdateModelCollectionDto dto, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        string name = (dto.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name is required", nameof(dto));
        }

        collection.Name = name;
        collection.Description = dto.Description;
        collection.UpdatedAt = DateTime.UtcNow;
        BumpCollection(collection);

        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Update, collection.OwnerUserId, VisibilityOf(collection), callerUserId, collection.UpdatedAt);
        await _repository.SaveChangesAsync(ct);

        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collection.Id, ct);
        return MapToDto(collection, memberships);
    }

    /// <inheritdoc/>
    public async Task DeleteCollectionAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        // Emit durable tombstones for every membership and the collection itself before the
        // hard delete. The journal rows are soft references (no FK), so they persist after the
        // rows are removed and let #845 propagate the deletions.
        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collectionId, ct);
        DateTime now = DateTime.UtcNow;
        SyncVisibility visibility = VisibilityOf(collection);

        foreach (ModelCollectionMembership membership in memberships)
        {
            _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Delete, collection.OwnerUserId, visibility, callerUserId, now);
        }

        _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Delete, collection.OwnerUserId, visibility, callerUserId, now);

        _repository.Remove(collection);
        await _repository.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> SetSharedAsync(Guid collectionId, bool shared, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        if (collection.IsShared != shared)
        {
            collection.IsShared = shared;
            collection.UpdatedAt = DateTime.UtcNow;
            BumpCollection(collection);
            _journal.Record(SyncEntityType.ModelCollection, collection.Id, SyncOperation.Update, collection.OwnerUserId, VisibilityOf(collection), callerUserId, collection.UpdatedAt);
            await _repository.SaveChangesAsync(ct);
        }

        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collection.Id, ct);
        return MapToDto(collection, memberships);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionMembershipDto>> ListMembersAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetOrThrowAsync(collectionId, includeMemberships: false, ct);
        EnsureCanRead(collection, callerUserId, callerIsAdmin);

        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collectionId, ct);
        return memberships.Select(MapMembershipToDto).ToList();
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionMembershipDto> AddMemberAsync(Guid collectionId, Guid modelId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        await ValidateModelsExistAsync([modelId], ct);

        ModelCollectionMembership? existing = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (existing is not null)
        {
            return MapMembershipToDto(existing);
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
        collection.UpdatedAt = now;
        _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Create, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);
        await _repository.SaveChangesAsync(ct);

        return MapMembershipToDto(membership);
    }

    /// <inheritdoc/>
    public async Task RemoveMemberAsync(Guid collectionId, Guid modelId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        ModelCollectionMembership? existing = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (existing is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        _repository.RemoveMembership(existing);
        collection.UpdatedAt = now;
        _journal.Record(SyncEntityType.ModelCollectionMembership, existing.Id, SyncOperation.Delete, collection.OwnerUserId, VisibilityOf(collection), callerUserId, now);
        await _repository.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> ReplaceMembersAsync(Guid collectionId, IEnumerable<Guid> modelIds, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modelIds);

        ModelCollection collection = await GetForWriteAsync(collectionId, callerUserId, callerIsAdmin, ct);

        // Preserve order while de-duplicating the requested set.
        var desired = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (Guid id in modelIds.Where(id => seen.Add(id)))
        {
            desired.Add(id);
        }

        await ValidateModelsExistAsync(desired, ct);

        IReadOnlyList<ModelCollectionMembership> current = await _repository.ListMembershipsAsync(collectionId, ct);
        var currentByModel = current.ToDictionary(m => m.ModelId);
        var desiredSet = new HashSet<Guid>(desired);

        DateTime now = DateTime.UtcNow;
        bool changed = false;
        SyncVisibility visibility = VisibilityOf(collection);

        // Remove memberships that are no longer desired.
        foreach (ModelCollectionMembership membership in current.Where(membership => !desiredSet.Contains(membership.ModelId)))
        {
            _repository.RemoveMembership(membership);
            _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Delete, collection.OwnerUserId, visibility, callerUserId, now);
            changed = true;
        }

        // Add memberships that are newly desired.
        foreach (Guid modelId in desired.Where(modelId => !currentByModel.ContainsKey(modelId)))
        {
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
            _journal.Record(SyncEntityType.ModelCollectionMembership, membership.Id, SyncOperation.Create, collection.OwnerUserId, visibility, callerUserId, now);
            changed = true;
        }

        if (changed)
        {
            collection.UpdatedAt = now;
        }

        // Single SaveChanges commits all removals and additions atomically.
        await _repository.SaveChangesAsync(ct);

        IReadOnlyList<ModelCollectionMembership> updated = await _repository.ListMembershipsAsync(collectionId, ct);
        return MapToDto(collection, updated);
    }

    private async Task<ModelCollection> GetForWriteAsync(Guid collectionId, Guid callerUserId, bool callerIsAdmin, CancellationToken ct)
    {
        ModelCollection collection = await GetOrThrowAsync(collectionId, includeMemberships: false, ct);
        EnsureCanWrite(collection, callerUserId, callerIsAdmin);
        return collection;
    }

    private async Task<ModelCollection> GetOrThrowAsync(Guid collectionId, bool includeMemberships, CancellationToken ct)
    {
        ModelCollection? collection = await _repository.GetByIdAsync(collectionId, includeMemberships, ct);
        return collection ?? throw new CollectionNotFoundException(collectionId);
    }

    private static void EnsureCanRead(ModelCollection collection, Guid callerUserId, bool callerIsAdmin)
    {
        if (callerIsAdmin || collection.OwnerUserId == callerUserId || collection.IsShared)
        {
            return;
        }

        throw new CollectionAccessDeniedException();
    }

    private static void EnsureCanWrite(ModelCollection collection, Guid callerUserId, bool callerIsAdmin)
    {
        if (callerIsAdmin || collection.OwnerUserId == callerUserId)
        {
            return;
        }

        throw new CollectionAccessDeniedException();
    }

    private async Task ValidateModelsExistAsync(List<Guid> modelIds, CancellationToken ct)
    {
        // When the slicer module is not loaded the query provider is absent; validation
        // degrades gracefully (consistent with the tag repository precedent).
        if (_model3DQuery is null || modelIds.Count == 0)
        {
            return;
        }

        var invalid = new List<Guid>();
        foreach (Guid id in modelIds)
        {
            if (!await _model3DQuery.ExistsAsync(id, ct))
            {
                invalid.Add(id);
            }
        }

        if (invalid.Count > 0)
        {
            throw new CollectionModelValidationException(invalid);
        }
    }

    private static List<ModelCollectionMembership> OrderMemberships(IEnumerable<ModelCollectionMembership> memberships)
    {
        return memberships
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();
    }

    private static SyncVisibility VisibilityOf(ModelCollection collection)
        => collection.IsShared ? SyncVisibility.Shared : SyncVisibility.Private;

    private static void BumpCollection(ModelCollection collection)
    {
        collection.Revision++;
        collection.ConcurrencyToken = Guid.NewGuid();
    }

    private static ModelCollectionDto MapToDto(ModelCollection collection, IReadOnlyList<ModelCollectionMembership> memberships)
    {
        return new ModelCollectionDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            OwnerUserId = collection.OwnerUserId,
            IsShared = collection.IsShared,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            MemberCount = memberships.Count,
            ModelIds = memberships.Select(m => m.ModelId).ToList(),
            Revision = collection.Revision,
            ConcurrencyToken = collection.ConcurrencyToken
        };
    }

    private static ModelCollectionMembershipDto MapMembershipToDto(ModelCollectionMembership membership)
    {
        return new ModelCollectionMembershipDto
        {
            Id = membership.Id,
            CollectionId = membership.CollectionId,
            ModelId = membership.ModelId,
            CreatedAt = membership.CreatedAt,
            UpdatedAt = membership.UpdatedAt,
            Revision = membership.Revision
        };
    }
}
