using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.IntegrationTests
{
    // Minimal test double for ISlicerOrchestrator used by integration tests when full infrastructure is not available.
    public class TestSlicerOrchestrator : ISlicerOrchestrator
    {
        private readonly Dictionary<Guid, SlicingJobStatusResponse> _jobs = new();

        public Task<SlicingJobResponse> SubmitJobAsync(SlicingJobRequest request, CancellationToken cancellationToken = default)
        {
            // Validate engine value - mirror production validation for tests
            if (!Enum.IsDefined(typeof(SlicerEngineType), request.SlicerEngine))
            {
                throw new ArgumentException($"Slicer engine {request.SlicerEngine} is not available", nameof(request));
            }

            var jobId = Guid.NewGuid();
            var response = new SlicingJobResponse
            {
                JobId = jobId,
                Status = SlicingJobStatus.Queued,
                EstimatedCompletionTime = DateTime.UtcNow.AddMinutes(1),
                QueuePosition = 1,
                SlicerWorkerUrl = new Uri("http://test-slicer-worker/local")
            };

            _jobs[jobId] = new SlicingJobStatusResponse
            {
                JobId = jobId,
                Status = SlicingJobStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };

            return Task.FromResult(response);
        }

        public Task<SlicingJobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            _jobs.TryGetValue(jobId, out var r);
            return Task.FromResult((SlicingJobStatusResponse?)r);
        }

        public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = SlicingJobStatus.Cancelled;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<List<SlicerEngineInfo>> GetAvailableEnginesAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<SlicerEngineInfo>
            {
                new SlicerEngineInfo { Engine = SlicerEngineType.OrcaSlicer, IsHealthy = true, Version = "test", SupportedExtensions = new [] { ".stl" } }
            };
            return Task.FromResult(list);
        }

        public Task<Dictionary<SlicerEngineType, SlicerQueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
        {
            var d = new Dictionary<SlicerEngineType, SlicerQueueStats>
            {
                [SlicerEngineType.OrcaSlicer] = new SlicerQueueStats { Engine = SlicerEngineType.OrcaSlicer, QueuedJobs = 0, ActiveWorkers = 0, AverageProcessingTimeSeconds = 0, LastUpdated = DateTime.UtcNow }
            };
            return Task.FromResult(d);
        }

        public Task<List<SlicingJobStatusResponse>> GetUserJobsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default)
        {
            var result = _jobs.Values.Where(j => j.Metadata == null || true).Take(limit ?? 50).ToList();
            return Task.FromResult(result);
        }

        public Task<SlicerOrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            SlicerOrchestratorHealth h = new() { IsHealthy = true, FileStorageHealthy = true, JobQueueHealthy = true };
            h.Engines[SlicerEngineType.OrcaSlicer] = new SlicerEngineInfo { Engine = SlicerEngineType.OrcaSlicer, IsHealthy = true, Version = "test" };
            return Task.FromResult(h);
        }
    }
}
