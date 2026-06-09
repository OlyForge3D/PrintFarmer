using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Controllers;

/// <summary>
/// API endpoints for managing slicer workers.
/// </summary>
[ApiController]
[Route("api/workers")]
[Authorize]
public class WorkersController(
    IWorkerRepository workerRepository,
    ISliceJobRepository jobRepository,
    ILogger<WorkersController> logger) : ControllerBase
{
    private readonly IWorkerRepository _workerRepository = workerRepository;
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly ILogger<WorkersController> _logger = logger;

    /// <summary>
    /// Lists all workers, optionally filtered by service ID.
    /// </summary>
    /// <param name="serviceId">Optional slicer service ID filter.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] string? serviceId, CancellationToken ct)
    {
        IReadOnlyList<Worker> workers;
        if (!string.IsNullOrEmpty(serviceId))
        {
            Worker? worker = await _workerRepository.GetByServiceIdAsync(serviceId);
            workers = worker is not null ? [worker] : [];
        }
        else
        {
            workers = await _workerRepository.GetAllAsync();
        }

        var response = workers.Select(w => new WorkerResponse
        {
            Id = w.Id,
            ServiceId = w.ServiceId,
            Name = w.Name,
            EndpointUrl = w.EndpointUrl,
            Capabilities = ParseCapabilities(w.CapabilitiesJson),
            Status = w.Status,
            FreeSlots = w.FreeSlots,
            TotalSlots = w.TotalSlots,
            ActiveJobs = w.ActiveJobs,
            CompletedJobs = w.CompletedJobs,
            FailedJobs = w.FailedJobs,
            AverageProcessingTimeSeconds = w.AverageProcessingTimeSeconds,
            LastHeartbeat = w.LastHeartbeat,
            RegisteredAt = w.RegisteredAt,
            OnlineAt = w.OnlineAt,
            OfflineAt = w.OfflineAt,
            Version = w.Version,
            IsDisabled = w.IsDisabled,
            DisabledReason = w.DisabledReason,
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Gets a specific worker by ID.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        var response = new WorkerResponse
        {
            Id = worker.Id,
            ServiceId = worker.ServiceId,
            Name = worker.Name,
            EndpointUrl = worker.EndpointUrl,
            Capabilities = JsonSerializer.Deserialize<string[]>(worker.CapabilitiesJson) ?? [],
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
            DisabledReason = worker.DisabledReason,
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets active jobs assigned to a worker.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}/jobs")]
    public async Task<IActionResult> GetJobsAsync(Guid id, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetJobsByWorkerIdAsync(id, ct);
        var response = jobs.Select(j => new WorkerJobResponse
        {
            JobId = j.Id,
            ModelFileName = j.ModelFileName,
            Status = j.Status,
            ProgressPercent = j.ProgressPercent,
            ProgressMessage = j.ProgressMessage,
            StartedAt = j.StartedAt,
            Priority = j.Priority,
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Disables a worker.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="request">Disable request with reason.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/disable")]
    public async Task<IActionResult> DisableAsync(Guid id, [FromBody] DisableWorkerRequest request, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        await _workerRepository.DisableWorkerAsync(id, request.Reason);

        _logger.LogWarning("Worker {WorkerId} disabled: {Reason}", id, request.Reason);
        return NoContent();
    }

    /// <summary>
    /// Enables a previously disabled worker.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/enable")]
    public async Task<IActionResult> EnableAsync(Guid id, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        await _workerRepository.EnableWorkerAsync(id);

        _logger.LogInformation("Worker {WorkerId} enabled", id);
        return NoContent();
    }

    /// <summary>
    /// Updates the total slots for a worker.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="request">Slots update request.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPatch("{id}/slots")]
    public async Task<IActionResult> UpdateSlotsAsync(Guid id, [FromBody] UpdateWorkerSlotsRequest request, CancellationToken ct)
    {
        if (request.TotalSlots < 0)
        {
            return BadRequest(new { error = "TotalSlots must be non-negative." });
        }

        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        await _workerRepository.UpdateTotalSlotsAsync(id, request.TotalSlots);

        _logger.LogInformation("Worker {WorkerId} slots updated to {Slots}", id, request.TotalSlots);
        return NoContent();
    }

    /// <summary>
    /// Resets a worker by clearing its active job count and releasing any stuck jobs
    /// back to the queue. Use this when a worker has ghost jobs blocking its slots.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/reset")]
    [Authorize(Roles = "farm_admin")]
    public async Task<IActionResult> ResetAsync(Guid id, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        // Release any stuck Processing jobs assigned to this worker back to Queued
        IReadOnlyList<SliceJob> stuckJobs = await _jobRepository.GetJobsByWorkerIdAsync(id, ct);
        int releasedJobs = 0;
        foreach (SliceJob job in stuckJobs)
        {
            job.Status = SliceJobStatus.Queued;
            job.WorkerId = null;
            job.ProgressPercent = 0;
            job.ProgressMessage = null;
            releasedJobs++;
        }

        // Reset the worker's active job counter
        bool reset = await _workerRepository.ResetAsync(id);
        if (!reset)
        {
            return NotFound();
        }

        _logger.LogWarning("Worker {WorkerId} reset by admin — released {ReleasedJobs} stuck job(s)", id, releasedJobs);

        return Ok(new { releasedJobs, status = worker.Status });
    }

    /// <summary>
    /// Deletes a worker registration.
    /// </summary>
    /// <param name="id">The worker ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        Worker? worker = await _workerRepository.GetByIdAsync(id);
        if (worker is null)
        {
            return NotFound();
        }

        await _workerRepository.DeleteAsync(id);
        _logger.LogWarning("Worker {WorkerId} deleted", id);
        return NoContent();
    }

    /// <summary>
    /// Parses CapabilitiesJson which may be a flat string[] or a structured object
    /// with supportedFormats, supportedFeatures, and capabilities arrays.
    /// </summary>
    private string[] ParseCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Flatten all string arrays from the object
                return doc.RootElement.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array)
#pragma warning disable IDISP005 // ArrayEnumerator is a struct with no-op Dispose
                    .SelectMany(p => p.Value.EnumerateArray())
#pragma warning restore IDISP005
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            }

            _logger.LogWarning("Unexpected JSON value kind {Kind} in CapabilitiesJson: {Json}", doc.RootElement.ValueKind, json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse CapabilitiesJson: {Json}", json);
        }

        return [];
    }
}
