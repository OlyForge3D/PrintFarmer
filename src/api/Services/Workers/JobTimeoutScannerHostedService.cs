using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Repositories.Slicing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Workers
{
    /// <summary>
    /// Background service that periodically scans for stuck / timed-out slice jobs and triggers retry/requeue or failure according to policy.
    /// </summary>
    public class JobTimeoutScannerHostedService : BackgroundService
    {
        private readonly ILogger<JobTimeoutScannerHostedService> _logger;
        private readonly IServiceProvider _sp;
        private readonly JobDispatchRetrySettings _retrySettings;
        private readonly TimeSpan _scanInterval;
        private readonly IWorkerCircuitBreakerService? _circuitBreaker;
        // Metrics are resolved per-scan from the scope; do not hold a disposable reference here.

        public JobTimeoutScannerHostedService(
            ILogger<JobTimeoutScannerHostedService> logger,
            IServiceProvider sp,
            IOptions<JobDispatchRetrySettings> retryOptions,
            IWorkerCircuitBreakerService? circuitBreaker = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sp = sp ?? throw new ArgumentNullException(nameof(sp));
            _retrySettings = retryOptions?.Value ?? new JobDispatchRetrySettings { MaxAttempts = 3, BaseDelayMs = 250, Multiplier = 2.0 };
            _scanInterval = TimeSpan.FromSeconds(30);
            _circuitBreaker = circuitBreaker;
            // no-op: metrics resolved per-scan from scoped provider when available
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("JobTimeoutScannerHostedService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while scanning for stuck slice jobs");
                }

                await Task.Delay(_scanInterval, stoppingToken);
            }

            _logger.LogInformation("JobTimeoutScannerHostedService stopping");
        }

        private async Task ScanOnceAsync(CancellationToken ct)
        {
            // Check circuit breaker states and transition to half-open if cooldown elapsed
            _circuitBreaker?.CheckCircuits();

            using var scope = _sp.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
            var workerRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Workers.IWorkerRepository>();

            // Determine stuck jobs: jobs with expired leases OR processing longer than 15 minutes
            int longRunningSeconds = 60 * 15; // 15 minutes
            var stuck = await repo.GetStuckJobsAsync(longRunningSeconds, limit: 100, ct: ct);

            if (stuck == null || stuck.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Found {Count} stuck slice jobs to evaluate", stuck.Count);

            foreach (var job in stuck.ToList())
            {
                try
                {
                    // Record failure in circuit breaker if worker is known
                    if (job.WorkerId.HasValue && job.WorkerId.Value != Guid.Empty && _circuitBreaker != null)
                    {
                        await _circuitBreaker.RecordJobFailureAsync(job.WorkerId.Value, workerRepo, ct);
                    }

                    // If lease expired, increment retry and requeue or fail depending on retry count
                    await repo.IncrementRetryAndRequeueAsync(job.Id, _retrySettings.MaxAttempts, ct);
                    var metrics = scope.ServiceProvider.GetService<Farm.Web.Api.Services.Slicing.SliceJobMetrics>();
                    metrics?.RecordJobRetry();
                    metrics?.RecordJobTimedOut();
                    if (job.RetryCount + 1 > _retrySettings.MaxAttempts)
                    {
                        _logger.LogWarning("Job {JobId} exceeded max retries and was marked Failed", job.Id);
                    }
                    else
                    {
                        _logger.LogInformation("Job {JobId} lease expired, requeued for retry (attempt {Attempt})", job.Id, job.RetryCount + 1);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed handling stuck job {JobId}", job.Id);
                }
            }
        }

        /// <summary>
        /// Public helper to let tests run a single scan iteration.
        /// </summary>
        public async Task ProcessStuckJobsOnceAsync(CancellationToken ct = default)
        {
            await ScanOnceAsync(ct);
        }
    }

    // Small settings class mirroring the appsettings section used earlier
    public class JobDispatchRetrySettings
    {
        public int MaxAttempts { get; set; } = 3;
        public int BaseDelayMs { get; set; } = 250;
        public double Multiplier { get; set; } = 2.0;
    }
}
