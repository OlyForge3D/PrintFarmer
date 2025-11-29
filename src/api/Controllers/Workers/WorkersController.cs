using System.Text.Json;
using Farm.Infrastructure.Contracts.Workers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers.Workers;

/// <summary>
/// Controller for managing worker nodes in the distributed slicing system
/// </summary>
[ApiController]
[Route("api/workers")]
[Tags("Workers")]
[Authorize] // All endpoints require authentication
public class WorkersController : ControllerBase
{
    private readonly IWorkerRepository _workerRepository;
    private readonly ISliceJobRepository _jobRepository;
    private readonly ILogger<WorkersController> _logger;

    public WorkersController(
        IWorkerRepository workerRepository,
        ISliceJobRepository jobRepository,
        ILogger<WorkersController> logger)
    {
        _workerRepository = workerRepository ?? throw new ArgumentNullException(nameof(workerRepository));
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get all workers
    /// </summary>
    /// <param name="limit">Maximum number of workers to return</param>
    /// <param name="offset">Number of workers to skip</param>
    /// <returns>List of workers</returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<WorkerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllWorkersAsync([FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        IReadOnlyList<Worker> workers = await _workerRepository.GetAllAsync(limit, offset);

        List<WorkerResponse> response = workers.Select(MapToResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Get worker by ID
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <returns>Worker details</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(WorkerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkerByIdAsync(Guid id)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        WorkerResponse response = MapToResponse(worker);
        return Ok(response);
    }

    /// <summary>
    /// Get workers by status
    /// </summary>
    /// <param name="status">Worker status (Online, Offline, Busy, Error, Draining)</param>
    /// <param name="limit">Maximum number of workers to return</param>
    /// <returns>List of workers with specified status</returns>
    [HttpGet("by-status/{status}")]
    [ProducesResponseType(typeof(List<WorkerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkersByStatusAsync(string status, [FromQuery] int limit = 100)
    {
        IReadOnlyList<Worker> workers = await _workerRepository.GetByStatusAsync(status, limit, 0);

        List<WorkerResponse> response = workers.Select(MapToResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Get available workers (online with free slots)
    /// </summary>
    /// <param name="limit">Maximum number of workers to return</param>
    /// <returns>List of available workers</returns>
    [HttpGet("available")]
    [ProducesResponseType(typeof(List<WorkerResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableWorkersAsync([FromQuery] int limit = 100)
    {
        IReadOnlyList<Worker> workers = await _workerRepository.GetAvailableWorkersAsync(limit);

        List<WorkerResponse> response = workers.Select(MapToResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Get active jobs assigned to a specific worker
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <returns>List of active jobs for the worker</returns>
    [HttpGet("{id}/jobs")]
    [ProducesResponseType(typeof(List<WorkerJobResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkerJobsAsync(Guid id)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetJobsByWorkerIdAsync(id);

        List<WorkerJobResponse> response = jobs.Select(job => new WorkerJobResponse
        {
            JobId = job.Id,
            ModelFileName = job.ModelFileName,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.ProgressMessage,
            StartedAt = job.StartedAt,
            Priority = job.Priority
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Disable a worker (admin operation)
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <param name="request">Disable reason</param>
    /// <returns>No content on success</returns>
    [HttpPost("{id}/disable")]
    [Authorize(Policy = "farm_admin")] // Admin-only: disable worker
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DisableWorkerAsync(Guid id, [FromBody] DisableWorkerRequest request)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest("Reason is required");
        }

        await _workerRepository.DisableWorkerAsync(id, request.Reason);
        await _workerRepository.SaveChangesAsync();

        _logger.LogInformation("Worker {WorkerId} ({WorkerName}) disabled: {Reason}", id, worker.Name, request.Reason);

        return NoContent();
    }

    /// <summary>
    /// Enable a worker (admin operation)
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <returns>No content on success</returns>
    [HttpPost("{id}/enable")]
    [Authorize(Policy = "farm_admin")] // Admin-only: enable worker
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EnableWorkerAsync(Guid id)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        await _workerRepository.EnableWorkerAsync(id);
        await _workerRepository.SaveChangesAsync();

        _logger.LogInformation("Worker {WorkerId} ({WorkerName}) enabled", id, worker.Name);

        return NoContent();
    }

    /// <summary>
    /// Delete a worker (admin operation)
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = "farm_admin")] // Admin-only: delete worker
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWorkerAsync(Guid id)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        await _workerRepository.DeleteAsync(id);
        await _workerRepository.SaveChangesAsync();

        _logger.LogInformation("Worker {WorkerId} ({WorkerName}) deleted", id, worker.Name);

        return NoContent();
    }

    /// <summary>
    /// Update worker total slots (admin operation)
    /// </summary>
    /// <param name="id">Worker ID</param>
    /// <param name="request">Request containing new total slots value</param>
    /// <returns>Updated worker details</returns>
    [HttpPut("{id}/slots")]
    [Authorize(Policy = "farm_admin")] // Admin-only: update slots
    [ProducesResponseType(typeof(WorkerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateWorkerSlotsAsync(Guid id, [FromBody] UpdateWorkerSlotsRequest request)
    {
        if (request.TotalSlots < 1)
        {
            return BadRequest("Total slots must be at least 1");
        }

        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker == null)
        {
            return NotFound($"Worker {id} not found");
        }

        string workerName = worker.Name;
        await _workerRepository.UpdateTotalSlotsAsync(id, request.TotalSlots);
        await _workerRepository.SaveChangesAsync();

        // Fetch updated worker to get recalculated FreeSlots
        Worker? updatedWorker = await _workerRepository.GetByIdAsync(id);
        if (updatedWorker == null)
        {
            return NotFound($"Worker {id} not found after update");
        }

        _logger.LogInformation("Worker {WorkerId} ({WorkerName}) total slots updated to {TotalSlots}", id, workerName, request.TotalSlots);

        return Ok(MapToResponse(updatedWorker));
    }

    private static WorkerResponse MapToResponse(Worker worker)
    {
        string[]? capabilities = null;
        try
        {
            capabilities = JsonSerializer.Deserialize<string[]>(worker.CapabilitiesJson);
        }
        catch
        {
            // Ignore parsing errors
        }

        return new WorkerResponse
        {
            Id = worker.Id,
            ServiceId = worker.ServiceId,
            Name = worker.Name,
            EndpointUrl = worker.EndpointUrl,
            Capabilities = capabilities ?? Array.Empty<string>(),
            Status = worker.Status,
            FreeSlots = worker.FreeSlots,
            TotalSlots = worker.TotalSlots,
            ActiveJobs = worker.ActiveJobs,
            CompletedJobs = worker.CompletedJobs,
            FailedJobs = worker.FailedJobs,
            AverageProcessingTimeSeconds = worker.AverageProcessingTimeSeconds,
            LastHeartbeat = worker.LastHeartbeat,
            RegisteredAt = worker.RegisteredAt,
            OnlineAt = worker.OnlineAt,
            OfflineAt = worker.OfflineAt,
            Version = worker.Version,
            IsDisabled = worker.IsDisabled,
            DisabledReason = worker.DisabledReason
        };
    }
}
