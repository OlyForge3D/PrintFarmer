using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Cameras;

/// <summary>
/// Repository interface for standalone camera data access operations.
/// Provides CRUD and query operations for cameras not attached to printers.
/// </summary>
public interface ICameraRepository
{
    /// <summary>
    /// Gets all cameras.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Camera>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all enabled cameras.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Camera>> GetEnabledAsync(CancellationToken ct);

    /// <summary>
    /// Finds a camera by ID.
    /// </summary>
    /// <param name="id">The camera ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Camera?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a camera by name.
    /// </summary>
    /// <param name="name">The camera name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Camera?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Checks if a camera with the given name exists.
    /// </summary>
    /// <param name="name">The camera name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsByNameAsync(string name, CancellationToken ct);

    /// <summary>
    /// Adds a new camera to the repository.
    /// </summary>
    /// <param name="camera">The camera to add.</param>
    void Add(Camera camera);

    /// <summary>
    /// Removes a camera from the repository.
    /// </summary>
    /// <param name="camera">The camera to remove.</param>
    void Remove(Camera camera);
}
