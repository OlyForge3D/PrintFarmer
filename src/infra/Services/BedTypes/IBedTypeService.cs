namespace Farm.Infrastructure.Services.BedTypes;

/// <summary>
/// Business logic for managing bed surface types.
/// </summary>
public interface IBedTypeService
{
    /// <summary>
    /// Lists all bed types with printer counts.
    /// </summary>
    Task<IReadOnlyList<BedTypeDto>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets a bed type by ID.
    /// </summary>
    Task<BedTypeDto?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates a new bed type. Name must be unique.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a bed type with the same name already exists.</exception>
    Task<BedTypeDto> CreateAsync(CreateBedTypeDto dto, CancellationToken ct);

    /// <summary>
    /// Updates a bed type's name, description, and color.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the bed type does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the new name conflicts with an existing bed type.</exception>
    Task<BedTypeDto> UpdateAsync(Guid id, UpdateBedTypeDto dto, CancellationToken ct);

    /// <summary>
    /// Deletes a bed type. System bed types cannot be deleted.
    /// Printers with this bed type get BedTypeId set to null.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the bed type does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when trying to delete a system bed type.</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);
}
