namespace Farm.Infrastructure.Services.PrinterGroups;

/// <summary>
/// Business logic for managing printer groups.
/// </summary>
public interface IPrinterGroupService
{
    /// <summary>
    /// Lists all printer groups with printer counts.
    /// </summary>
    Task<IReadOnlyList<PrinterGroupDto>> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets a printer group by ID with its printers.
    /// </summary>
    Task<PrinterGroupDetailDto?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Creates a new printer group. Name must be unique.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a group with the same name already exists.</exception>
    Task<PrinterGroupDto> CreateAsync(CreatePrinterGroupDto dto, CancellationToken ct);

    /// <summary>
    /// Updates a printer group's name and description.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the new name conflicts with an existing group.</exception>
    Task<PrinterGroupDto> UpdateAsync(Guid id, UpdatePrinterGroupDto dto, CancellationToken ct);

    /// <summary>
    /// Deletes a printer group. Printers in the group get PrinterGroupId set to null.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the group does not exist.</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Adds a printer to a group. Removes the printer from its current group (if any).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the group or printer does not exist.</exception>
    Task AddPrinterAsync(Guid groupId, Guid printerId, CancellationToken ct);

    /// <summary>
    /// Removes a printer from a group (sets PrinterGroupId to null).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown when the group or printer does not exist.</exception>
    Task RemovePrinterAsync(Guid groupId, Guid printerId, CancellationToken ct);
}
