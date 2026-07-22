using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Collections;
using Farm.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Collections;

/// <summary>
/// Default implementation of <see cref="IModelCollectionService"/>. Authorization (owner/admin) and
/// model-existence validation live here so controllers stay thin and the rules are unit-testable.
/// </summary>
/// <remarks>
/// <see cref="IModel3DQueryProvider"/> is optional: when the slicer module is not loaded it is null
/// and model-existence validation is skipped, mirroring the tag boundary precedent so the API-only
/// host continues to function.
/// </remarks>
public class ModelCollectionService(
    IModelCollectionRepository repository,
    ILogger<ModelCollectionService> logger,
    IModel3DQueryProvider? modelQuery = null) : IModelCollectionService
{
    private readonly IModelCollectionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ILogger<ModelCollectionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IModel3DQueryProvider? _modelQuery = modelQuery;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionDto>> ListAsync(CollectionCaller caller, CancellationToken ct)
    {
        IReadOnlyList<ModelCollection> collections = await _repository.ListVisibleToAsync(caller.UserId, caller.IsAdmin, ct);
        return collections.Select(c => MapToDto(c, c.Memberships.Count)).ToList();
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> GetAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct)
    {
        ModelCollection collection = await LoadWithMembershipsOrThrowAsync(collectionId, ct);
        EnsureCanRead(caller, collection);
        return MapToDto(collection, collection.Memberships.Count);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> CreateAsync(CollectionCaller caller, CreateModelCollectionDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);
        string name = NormalizeName(dto.Name);

        DateTime now = DateTime.UtcNow;
        var collection = new ModelCollection
        {
            Id = Guid.NewGuid(),
            OwnerUserId = caller.UserId,
            Name = name,
            Description = dto.Description,
            Visibility = dto.Visibility,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.AddAsync(collection, ct);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} created collection {CollectionId}", caller.UserId, collection.Id);
        return MapToDto(collection, 0);
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionDto> UpdateAsync(CollectionCaller caller, Guid collectionId, UpdateModelCollectionDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        collection.Name = NormalizeName(dto.Name);
        collection.Description = dto.Description;
        collection.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);

        int count = await _repository.CountMembershipsAsync(collectionId, ct);
        return MapToDto(collection, count);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct)
    {
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        await _repository.RemoveAsync(collection, ct);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} deleted collection {CollectionId}", caller.UserId, collectionId);
    }

    /// <inheritdoc/>
    public Task<ModelCollectionDto> ShareAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct)
        => SetVisibilityAsync(caller, collectionId, ModelCollectionVisibility.Shared, ct);

    /// <inheritdoc/>
    public Task<ModelCollectionDto> UnshareAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct)
        => SetVisibilityAsync(caller, collectionId, ModelCollectionVisibility.Private, ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionMembershipDto>> ListMembersAsync(CollectionCaller caller, Guid collectionId, CancellationToken ct)
    {
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanRead(caller, collection);

        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collectionId, ct);
        return memberships.Select(MapToDto).ToList();
    }

    /// <inheritdoc/>
    public async Task<ModelCollectionMembershipDto> AddMemberAsync(CollectionCaller caller, Guid collectionId, Guid modelId, CancellationToken ct)
    {
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        ModelCollectionMembership? existing = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (existing is not null)
        {
            // Idempotent add: return the current membership unchanged.
            return MapToDto(existing);
        }

        await EnsureModelExistsAsync(modelId, ct);

        var membership = new ModelCollectionMembership
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            ModelId = modelId,
            AddedAt = DateTime.UtcNow
        };

        await _repository.AddMembershipAsync(membership, ct);
        collection.UpdatedAt = membership.AddedAt;
        await _repository.SaveChangesAsync(ct);

        return MapToDto(membership);
    }

    /// <inheritdoc/>
    public async Task RemoveMemberAsync(CollectionCaller caller, Guid collectionId, Guid modelId, CancellationToken ct)
    {
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        ModelCollectionMembership? membership = await _repository.GetMembershipAsync(collectionId, modelId, ct);
        if (membership is null)
        {
            // Idempotent removal keeps membership changes merge-friendly for sync.
            return;
        }

        await _repository.RemoveMembershipAsync(membership, ct);
        collection.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelCollectionMembershipDto>> ReplaceMembersAsync(CollectionCaller caller, Guid collectionId, IReadOnlyCollection<Guid> modelIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ModelCollection collection = await LoadWithMembershipsOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        var desired = modelIds.Distinct().ToList();
        foreach (Guid modelId in desired)
        {
            await EnsureModelExistsAsync(modelId, ct);
        }

        var desiredSet = new HashSet<Guid>(desired);
        var currentSet = new HashSet<Guid>(collection.Memberships.Select(m => m.ModelId));

        List<ModelCollectionMembership> toRemove = collection.Memberships
            .Where(m => !desiredSet.Contains(m.ModelId))
            .ToList();
        if (toRemove.Count > 0)
        {
            await _repository.RemoveMembershipsAsync(toRemove, ct);
        }

        DateTime now = DateTime.UtcNow;
        foreach (Guid modelId in desired.Where(id => !currentSet.Contains(id)))
        {
            await _repository.AddMembershipAsync(
                new ModelCollectionMembership
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collectionId,
                    ModelId = modelId,
                    AddedAt = now
                },
                ct);
        }

        collection.UpdatedAt = now;
        await _repository.SaveChangesAsync(ct);

        IReadOnlyList<ModelCollectionMembership> memberships = await _repository.ListMembershipsAsync(collectionId, ct);
        return memberships.Select(MapToDto).ToList();
    }

    private async Task<ModelCollectionDto> SetVisibilityAsync(CollectionCaller caller, Guid collectionId, ModelCollectionVisibility visibility, CancellationToken ct)
    {
        ModelCollection collection = await LoadOrThrowAsync(collectionId, ct);
        EnsureCanMutate(caller, collection);

        if (collection.Visibility != visibility)
        {
            collection.Visibility = visibility;
            collection.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(ct);
        }

        int count = await _repository.CountMembershipsAsync(collectionId, ct);
        return MapToDto(collection, count);
    }

    private async Task<ModelCollection> LoadOrThrowAsync(Guid collectionId, CancellationToken ct)
    {
        return await _repository.GetByIdAsync(collectionId, ct)
            ?? throw new CollectionNotFoundException(collectionId);
    }

    private async Task<ModelCollection> LoadWithMembershipsOrThrowAsync(Guid collectionId, CancellationToken ct)
    {
        return await _repository.GetByIdWithMembershipsAsync(collectionId, ct)
            ?? throw new CollectionNotFoundException(collectionId);
    }

    private async Task EnsureModelExistsAsync(Guid modelId, CancellationToken ct)
    {
        // When the model query abstraction is unavailable (slicer module disabled) we cannot verify
        // existence, so we skip validation and allow the membership, matching the tag boundary.
        if (_modelQuery is null)
        {
            return;
        }

        if (!await _modelQuery.ExistsAsync(modelId, ct))
        {
            throw new CollectionModelNotFoundException(modelId);
        }
    }

    private static void EnsureCanMutate(CollectionCaller caller, ModelCollection collection)
    {
        if (caller.IsAdmin || collection.OwnerUserId == caller.UserId)
        {
            return;
        }

        throw new CollectionAccessDeniedException();
    }

    private static void EnsureCanRead(CollectionCaller caller, ModelCollection collection)
    {
        if (caller.IsAdmin
            || collection.OwnerUserId == caller.UserId
            || collection.Visibility == ModelCollectionVisibility.Shared)
        {
            return;
        }

        throw new CollectionAccessDeniedException();
    }

    private static string NormalizeName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return string.IsNullOrEmpty(trimmed)
            ? throw new ArgumentException("Collection name is required.", nameof(name))
            : trimmed;
    }

    private static ModelCollectionDto MapToDto(ModelCollection collection, int memberCount) => new()
    {
        Id = collection.Id,
        OwnerUserId = collection.OwnerUserId,
        Name = collection.Name,
        Description = collection.Description,
        Visibility = collection.Visibility,
        MemberCount = memberCount,
        CreatedAt = collection.CreatedAt,
        UpdatedAt = collection.UpdatedAt
    };

    private static ModelCollectionMembershipDto MapToDto(ModelCollectionMembership membership) => new()
    {
        Id = membership.Id,
        CollectionId = membership.CollectionId,
        ModelId = membership.ModelId,
        AddedAt = membership.AddedAt
    };
}
