using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Repositories.Workers;
using Farm.Web.Api.Services.JobDispatch;
using Farm.Web.Api.Services.Slicing;
using Farm.Infrastructure.Telemetry;
using Xunit;
using System.Collections.Generic;
using System.Text.Json;

namespace Farm.Web.Api.Tests.Slicing;

public class JobDispatcherRetryTests
{
    private class StubSliceJobRepository : ISliceJobRepository
    {
        public List<SliceJob> Jobs { get; } = new();
        public Task AddAsync(SliceJob job, CancellationToken ct = default) { Jobs.Add(job); return Task.CompletedTask; }
        public Task<SliceJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<SliceJob?>(Jobs.Find(j => j.Id == id));
        public Task<IReadOnlyList<SliceJob>> GetByUserIdAsync(Guid userId, int? limit = null, int? offset = null, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<SliceJob>)Jobs); 
        public Task<IReadOnlyList<SliceJob>> GetByStatusAsync(string status, int? limit = null, CancellationToken ct = default) => Task.FromResult((IReadOnlyList<SliceJob>)Jobs.FindAll(j => j.Status == status));
        public Task<IReadOnlyList<SliceJob>> GetQueuedJobsAsync(int? limit = null, CancellationToken ct = default)
        {
            var queued = Jobs.FindAll(j => j.Status == SliceJobStatus.Queued);
            if (limit.HasValue) queued = queued.GetRange(0, Math.Min(limit.Value, queued.Count));
            return Task.FromResult((IReadOnlyList<SliceJob>)queued);
        }
        public Task UpdateStatusAsync(Guid id, string status, string? progressMessage = null, int? progressPercent = null, CancellationToken ct = default) { var job = Jobs.Find(j => j.Id == id); if (job != null) job.Status = status; return Task.CompletedTask; }
        public Task MarkStartedAsync(Guid id, Guid workerId, CancellationToken ct = default) { var job = Jobs.Find(j => j.Id == id); if (job != null) { job.Status = SliceJobStatus.Processing; job.WorkerId = workerId; job.StartedAt = DateTime.UtcNow; } return Task.CompletedTask; }
        public Task MarkCompletedAsync(Guid id, string resultFileUrl, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkCompletedWithArtifactsAsync(Guid jobId, string resultFileUrl, IEnumerable<Guid> artifactIds, int? estimatedPrintTimeSeconds = null, decimal? filamentUsedGrams = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateProgressAsync(Guid jobId, int progressPercent, string progressMessage, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SliceJob?> ClaimNextJobAsync(Guid workerId, string[]? capabilities, int leaseDurationSeconds, CancellationToken ct = default)
        {
            var job = Jobs.Find(j => j.Status == SliceJobStatus.Queued);
            if (job != null) { job.Status = SliceJobStatus.Processing; job.WorkerId = workerId; job.ClaimedAt = DateTime.UtcNow; job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSeconds); }
            return Task.FromResult(job);
        }
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private class StubWorkerRepository : IWorkerRepository
    {
        public List<Worker> Workers { get; } = new();
        public Task AddAsync(Worker w) { Workers.Add(w); return Task.CompletedTask; }
        public Task<Worker?> GetByIdAsync(Guid id) => Task.FromResult<Worker?>(Workers.Find(w => w.Id == id));
        public Task<Worker?> GetByServiceIdAsync(string serviceId) => Task.FromResult<Worker?>(null);
        public Task<IReadOnlyList<Worker>> GetAllAsync(int limit = 100, int offset = 0) => Task.FromResult((IReadOnlyList<Worker>)Workers);
        public Task<IReadOnlyList<Worker>> GetByStatusAsync(string status, int limit = 100, int offset = 0) => Task.FromResult((IReadOnlyList<Worker>)Workers.FindAll(w => w.Status == status));
        public Task<IReadOnlyList<Worker>> GetAvailableWorkersAsync(int limit = 100)
        {
            var available = Workers.FindAll(w => w.Status == WorkerStatus.Online && w.FreeSlots > 0 && !w.IsDisabled);
            return Task.FromResult((IReadOnlyList<Worker>)available);
        }
        public Task<IReadOnlyList<Worker>> GetWorkersByCapabilitiesAsync(string[] requiredCapabilities, int limit = 100)
        {
            var available = Workers.FindAll(w => w.Status == WorkerStatus.Online && w.FreeSlots > 0 && !w.IsDisabled);
            var matching = new List<Worker>();
            foreach (var w in available)
            {
                var caps = JsonSerializer.Deserialize<string[]>(w.CapabilitiesJson) ?? Array.Empty<string>();
                bool ok = true;
                foreach (var req in requiredCapabilities)
                {
                    if (!Array.Exists(caps, c => string.Equals(c, req, StringComparison.OrdinalIgnoreCase))) { ok = false; break; }
                }
                if (ok) matching.Add(w);
            }
            return Task.FromResult((IReadOnlyList<Worker>)matching);
        }
        public Task<IReadOnlyList<Worker>> GetStaleWorkersAsync(TimeSpan heartbeatTimeout) => Task.FromResult((IReadOnlyList<Worker>)new List<Worker>());
        public Task UpdateStatusAsync(Guid id, string status) => Task.CompletedTask;
        public Task UpdateHeartbeatAsync(Guid id, int freeSlots, int totalSlots) => Task.CompletedTask;
        public Task IncrementActiveJobsAsync(Guid id) => Task.CompletedTask;
        public Task DecrementActiveJobsAsync(Guid id, bool success, double processingTimeSeconds) => Task.CompletedTask;
        public Task DisableWorkerAsync(Guid id, string reason) => Task.CompletedTask;
        public Task EnableWorkerAsync(Guid id) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task SaveChangesAsync() => Task.CompletedTask;
    }
    private class StubSliceJobEventService : ISliceJobEventService
    {
        public Task NotifyJobQueuedAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyJobStartedAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyJobProgressAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyJobCompletedAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyJobFailedAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyJobCancelledAsync(SliceJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private class StubLogger : IUnifiedLoggingService
    {
        public void LogDebug(string message, string? correlationId = null, object? metadata = null) { }
        public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
        public void LogInformation(string message, string? correlationId = null, object? metadata = null) { }
        public void LogWarning(string message, string? correlationId = null, object? metadata = null) { }
        public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
        public void LogError(string message, string? correlationId = null, object? metadata = null) { }
        public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
        public void LogCritical(string message, string? correlationId = null, object? metadata = null) { }
        public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
        public void LogWithContext(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null) { }
    }
    private class FlakyHandler : HttpMessageHandler
    {
        private int _attempts = 0;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempts++;
            if (_attempts < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Transient failure")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
    private class FlakyHttpClientFactory : IHttpClientFactory
    {
        private readonly FlakyHandler _handler = new();
        public HttpClient CreateClient(string name) => new(_handler);
    }

    [Fact]
    public async Task DispatchJobAsync_RetriesAndSucceedsOnThirdAttempt()
    {
        var jobRepo = new StubSliceJobRepository();
        var workerRepo = new StubWorkerRepository();
        var evtService = new StubSliceJobEventService();
        var logger = new StubLogger();
        var httpFactory = new FlakyHttpClientFactory();
            var retryOptions = new Farm.Web.Api.Services.JobDispatch.RetryOptions();
            var dispatcher = new JobDispatcherService(jobRepo, workerRepo, evtService, logger, httpFactory, retryOptions);

        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            Name = "FlakyWorker",
            Status = WorkerStatus.Online,
            FreeSlots = 1,
            TotalSlots = 1,
            CapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer" }),
            EndpointUrl = "http://localhost:5000"
        };
        workerRepo.Workers.Add(worker);

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { "orcaslicer" })
        };
        await jobRepo.AddAsync(job);

        var result = await dispatcher.DispatchJobAsync(job.Id, CancellationToken.None);
        Assert.True(result); // Should succeed on third attempt
        Assert.Equal(SliceJobStatus.Processing, job.Status);
        Assert.Equal(worker.Id, job.WorkerId);
    }
}
