# Slicer Microservices Architecture and Distributed Processing

## Summary
Design and implement a distributed microservices architecture for 3D slicing operations, separating each slicer (OrcaSlicer, PrusaSlicer, etc.) into independent, scalable microservices that can be deployed across multiple physical machines to optimize CPU utilization and improve overall system scalability.

## Background
PrintFarmer currently handles slicing operations within the main application architecture, which creates several challenges:
- **CPU bottlenecks** during intensive slicing operations affecting overall system performance
- **Resource contention** between slicing tasks and other application services
- **Limited scalability** for concurrent slicing operations
- **Single point of failure** if slicing operations crash or hang
- **No resource isolation** between different slicer types
- **Deployment inflexibility** - cannot scale slicing capacity independently

Slicing operations are computationally intensive and can benefit significantly from:
- **Dedicated CPU resources** on specialized hardware
- **Horizontal scaling** by adding more slicer nodes
- **Load balancing** across multiple slicer instances
- **Resource isolation** to prevent interference with core services
- **Independent deployment** and version management

## Requirements

### 1. Microservices Architecture Design
- **Independent slicer services** for each supported slicer type:
  - OrcaSlicer microservice
  - PrusaSlicer microservice
  - SuperSlicer microservice (future)
  - Cura microservice (future)
- **Container-based deployment** with Docker for portability
- **API-first design** with RESTful endpoints and gRPC for high-performance communication
- **Service discovery** for dynamic slicer node registration
- **Health monitoring** and automatic failure recovery
- **Configuration management** for slicer profiles and settings

### 2. Distributed Job Queue System
- **Message queue integration** (Redis Streams, RabbitMQ, or Apache Kafka)
- **Job scheduling** with priority-based processing
- **Load balancing** across available slicer nodes
- **Job state tracking** (queued, processing, completed, failed)
- **Result delivery** with file storage and notification system
- **Retry mechanisms** for failed jobs with exponential backoff
- **Dead letter queues** for permanently failed jobs

### 3. Resource Management and Scaling
- **CPU and memory limits** per slicer container
- **Horizontal pod autoscaling** based on queue depth and CPU utilization
- **Node affinity** for deploying on high-CPU machines
- **Resource requests/limits** to prevent resource starvation
- **Graceful shutdown** handling for in-progress slicing jobs
- **Job preemption** for high-priority tasks

### 4. File Management and Storage
- **Distributed file storage** for input STL files and output G-code
- **Temporary file cleanup** after job completion
- **File compression** for efficient storage and transfer
- **Secure file access** with signed URLs or tokens
- **Storage quotas** and cleanup policies
- **Multi-region storage** for global deployment

### 5. Communication Protocols
- **Asynchronous job submission** via REST API
- **Real-time progress updates** via SignalR/WebSockets
- **gRPC communication** for high-performance inter-service communication
- **Event-driven architecture** for job lifecycle events
- **API versioning** for backward compatibility
- **Circuit breaker patterns** for resilient communication

### 6. Monitoring and Observability
- **Per-service metrics** (queue depth, processing time, success/failure rates)
- **Resource utilization monitoring** (CPU, memory, disk I/O)
- **Distributed tracing** for end-to-end job tracking
- **Performance profiling** for slicer optimization
- **Business metrics** (jobs per hour, average processing time)
- **Alerting** for service failures and performance degradation

## Technical Implementation

### 1. Slicer Service Architecture

#### OrcaSlicer Microservice
```dockerfile
# docker/orcaslicer.dockerfile
FROM ubuntu:22.04

# Install OrcaSlicer and dependencies
RUN apt-get update && apt-get install -y \
    wget \
    xvfb \
    libgtk-3-0 \
    libglu1-mesa \
    && rm -rf /var/lib/apt/lists/*

# Download and install OrcaSlicer
RUN wget -O orcaslicer.AppImage https://github.com/SoftFever/OrcaSlicer/releases/download/v1.8.0/OrcaSlicer_V1.8.0_Linux_x86_64.AppImage \
    && chmod +x orcaslicer.AppImage \
    && ./orcaslicer.AppImage --appimage-extract \
    && mv squashfs-root /opt/orcaslicer

# Copy slicer service application
COPY SlicerService.OrcaSlicer/ /app/
WORKDIR /app

# Install .NET runtime
RUN wget -O packages-microsoft-prod.deb https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y dotnet-runtime-9.0

EXPOSE 8080
ENTRYPOINT ["dotnet", "SlicerService.OrcaSlicer.dll"]
```

#### Slicer Service Implementation
```csharp
// SlicerService.OrcaSlicer/Controllers/SlicingController.cs
[ApiController]
[Route("api/v1/[controller]")]
public class SlicingController : ControllerBase
{
    private readonly ISlicingService _slicingService;
    private readonly IJobQueue _jobQueue;
    private readonly ILogger<SlicingController> _logger;

    public SlicingController(
        ISlicingService slicingService,
        IJobQueue jobQueue,
        ILogger<SlicingController> logger)
    {
        _slicingService = slicingService;
        _jobQueue = jobQueue;
        _logger = logger;
    }

    [HttpPost("jobs")]
    public async Task<ActionResult<SlicingJobResponse>> SubmitSlicingJob(
        [FromBody] SlicingJobRequest request)
    {
        try
        {
            var jobId = Guid.NewGuid();
            
            var job = new SlicingJob
            {
                Id = jobId,
                UserId = request.UserId,
                PrinterId = request.PrinterId,
                ModelFileUrl = request.ModelFileUrl,
                SlicerProfile = request.SlicerProfile,
                Priority = request.Priority,
                CreatedAt = DateTime.UtcNow,
                Status = SlicingJobStatus.Queued
            };

            await _jobQueue.EnqueueAsync(job);

            return Ok(new SlicingJobResponse
            {
                JobId = jobId,
                Status = SlicingJobStatus.Queued,
                EstimatedCompletionTime = await _slicingService.EstimateCompletionTimeAsync(job)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit slicing job");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("jobs/{jobId}")]
    public async Task<ActionResult<SlicingJobStatus>> GetJobStatus(Guid jobId)
    {
        var job = await _jobQueue.GetJobAsync(jobId);
        if (job == null)
        {
            return NotFound();
        }

        return Ok(new SlicingJobStatusResponse
        {
            JobId = jobId,
            Status = job.Status,
            Progress = job.Progress,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ErrorMessage = job.ErrorMessage,
            ResultFileUrl = job.ResultFileUrl
        });
    }

    [HttpGet("health")]
    public async Task<ActionResult<HealthCheckResponse>> HealthCheck()
    {
        var health = await _slicingService.CheckHealthAsync();
        
        return Ok(new HealthCheckResponse
        {
            Status = health.IsHealthy ? "healthy" : "unhealthy",
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            ActiveJobs = health.ActiveJobs,
            QueueDepth = health.QueueDepth,
            AvailableMemory = health.AvailableMemory,
            CpuUsage = health.CpuUsage,
            LastJobCompletedAt = health.LastJobCompletedAt
        });
    }
}

// SlicerService.OrcaSlicer/Services/OrcaSlicingService.cs
public class OrcaSlicingService : ISlicingService
{
    private readonly ILogger<OrcaSlicingService> _logger;
    private readonly IFileStorage _fileStorage;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _concurrencySemaphore;

    public OrcaSlicingService(
        ILogger<OrcaSlicingService> logger,
        IFileStorage fileStorage,
        IConfiguration configuration)
    {
        _logger = logger;
        _fileStorage = fileStorage;
        _configuration = configuration;
        
        var maxConcurrentJobs = configuration.GetValue<int>("MaxConcurrentJobs", Environment.ProcessorCount);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
    }

    public async Task<SlicingResult> SliceAsync(SlicingJob job, CancellationToken cancellationToken)
    {
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        
        try
        {
            _logger.LogInformation("Starting slicing job {JobId}", job.Id);
            
            // Download input file
            var inputFile = await _fileStorage.DownloadFileAsync(job.ModelFileUrl, cancellationToken);
            
            // Create temporary directories
            var tempDir = Path.Combine(Path.GetTempPath(), job.Id.ToString());
            Directory.CreateDirectory(tempDir);
            
            try
            {
                var stlPath = Path.Combine(tempDir, "model.stl");
                var gcodeDir = Path.Combine(tempDir, "output");
                var configPath = Path.Combine(tempDir, "config.ini");
                
                // Save input file
                await File.WriteAllBytesAsync(stlPath, inputFile, cancellationToken);
                
                // Generate slicer configuration
                await GenerateSlicerConfigAsync(job.SlicerProfile, configPath);
                
                // Run OrcaSlicer
                var result = await RunOrcaSlicerAsync(stlPath, gcodeDir, configPath, 
                    job, cancellationToken);
                
                if (result.Success)
                {
                    // Upload result file
                    var gcodeFile = Directory.GetFiles(gcodeDir, "*.gcode").FirstOrDefault();
                    if (gcodeFile != null)
                    {
                        var gcodeBytes = await File.ReadAllBytesAsync(gcodeFile, cancellationToken);
                        result.ResultFileUrl = await _fileStorage.UploadFileAsync(
                            $"gcode/{job.Id}.gcode", gcodeBytes, cancellationToken);
                    }
                }
                
                return result;
            }
            finally
            {
                // Cleanup temporary files
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    private async Task<SlicingResult> RunOrcaSlicerAsync(
        string stlPath, 
        string outputDir, 
        string configPath,
        SlicingJob job,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/opt/orcaslicer/usr/bin/orcaslicer",
            Arguments = $"--load-config \"{configPath}\" --export-gcode --output \"{outputDir}\" \"{stlPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["DISPLAY"] = ":99" } // Xvfb display
        };

        using var process = new Process { StartInfo = startInfo };
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        
        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                outputBuilder.AppendLine(args.Data);
                
                // Parse progress from output
                if (TryParseProgress(args.Data, out var progress))
                {
                    job.Progress = progress;
                    _logger.LogDebug("Job {JobId} progress: {Progress}%", job.Id, progress);
                }
            }
        };
        
        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                errorBuilder.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }

        var success = process.ExitCode == 0;
        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        _logger.LogInformation("OrcaSlicer process completed. Success: {Success}, ExitCode: {ExitCode}", 
            success, process.ExitCode);

        if (!success)
        {
            _logger.LogError("OrcaSlicer failed for job {JobId}. Error: {Error}", job.Id, error);
        }

        return new SlicingResult
        {
            Success = success,
            Output = output,
            Error = error,
            ProcessingTimeSeconds = (DateTime.UtcNow - job.StartedAt.Value).TotalSeconds
        };
    }

    private bool TryParseProgress(string output, out int progress)
    {
        progress = 0;
        
        // Parse progress from OrcaSlicer output
        // Example: "Processing layer 150/300 (50%)"
        var match = Regex.Match(output, @"(\d+)%");
        if (match.Success)
        {
            return int.TryParse(match.Groups[1].Value, out progress);
        }
        
        return false;
    }
}
```

### 2. Job Queue Implementation

#### Redis-based Job Queue
```csharp
// SlicerService.Core/Services/RedisJobQueue.cs
public class RedisJobQueue : IJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _database;
    private readonly ILogger<RedisJobQueue> _logger;
    private readonly string _queueKey = "slicer:jobs:queue";
    private readonly string _processingKey = "slicer:jobs:processing";
    private readonly string _completedKey = "slicer:jobs:completed";

    public RedisJobQueue(IConnectionMultiplexer redis, ILogger<RedisJobQueue> logger)
    {
        _redis = redis;
        _database = redis.GetDatabase();
        _logger = logger;
    }

    public async Task EnqueueAsync(SlicingJob job)
    {
        var jobJson = JsonSerializer.Serialize(job);
        
        // Add to priority queue (higher priority = lower score)
        var score = GetPriorityScore(job.Priority);
        await _database.SortedSetAddAsync(_queueKey, jobJson, score);
        
        // Store job details
        await _database.HashSetAsync($"job:{job.Id}", new HashEntry[]
        {
            new("id", job.Id.ToString()),
            new("status", job.Status.ToString()),
            new("created_at", job.CreatedAt.ToString("O")),
            new("data", jobJson)
        });

        _logger.LogInformation("Enqueued job {JobId} with priority {Priority}", job.Id, job.Priority);
    }

    public async Task<SlicingJob?> DequeueAsync(string workerId, TimeSpan timeout)
    {
        var result = await _database.ScriptEvaluateAsync(@"
            local job = redis.call('ZPOPMIN', KEYS[1])
            if job[1] then
                redis.call('ZADD', KEYS[2], job[2], job[1])
                redis.call('HSET', 'worker:' .. ARGV[1], 'current_job', job[1], 'started_at', ARGV[2])
                return job[1]
            else
                return nil
            end
        ", new RedisKey[] { _queueKey, _processingKey }, new RedisValue[] { workerId, DateTime.UtcNow.ToString("O") });

        if (result.IsNull)
        {
            return null;
        }

        var jobJson = result.ToString();
        var job = JsonSerializer.Deserialize<SlicingJob>(jobJson);
        
        if (job != null)
        {
            job.Status = SlicingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            job.WorkerId = workerId;
            
            await UpdateJobStatusAsync(job);
        }

        return job;
    }

    public async Task CompleteJobAsync(SlicingJob job, SlicingResult result)
    {
        job.Status = result.Success ? SlicingJobStatus.Completed : SlicingJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ErrorMessage = result.Error;
        job.ResultFileUrl = result.ResultFileUrl;

        // Move from processing to completed
        var jobJson = JsonSerializer.Serialize(job);
        
        await _database.ScriptEvaluateAsync(@"
            redis.call('ZREM', KEYS[1], ARGV[1])
            redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
        ", new RedisKey[] { _processingKey, _completedKey }, 
           new RedisValue[] { jobJson, DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        await UpdateJobStatusAsync(job);

        _logger.LogInformation("Completed job {JobId} with status {Status}", job.Id, job.Status);
    }

    public async Task<SlicingJob?> GetJobAsync(Guid jobId)
    {
        var jobData = await _database.HashGetAsync($"job:{jobId}", "data");
        if (!jobData.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SlicingJob>(jobData);
    }

    public async Task<long> GetQueueDepthAsync()
    {
        return await _database.SortedSetLengthAsync(_queueKey);
    }

    public async Task<long> GetProcessingCountAsync()
    {
        return await _database.SortedSetLengthAsync(_processingKey);
    }

    private double GetPriorityScore(SlicingJobPriority priority)
    {
        return priority switch
        {
            SlicingJobPriority.Low => 3.0,
            SlicingJobPriority.Normal => 2.0,
            SlicingJobPriority.High => 1.0,
            SlicingJobPriority.Critical => 0.0,
            _ => 2.0
        };
    }

    private async Task UpdateJobStatusAsync(SlicingJob job)
    {
        var jobJson = JsonSerializer.Serialize(job);
        await _database.HashSetAsync($"job:{job.Id}", new HashEntry[]
        {
            new("status", job.Status.ToString()),
            new("progress", job.Progress),
            new("started_at", job.StartedAt?.ToString("O") ?? ""),
            new("completed_at", job.CompletedAt?.ToString("O") ?? ""),
            new("worker_id", job.WorkerId ?? ""),
            new("error_message", job.ErrorMessage ?? ""),
            new("result_file_url", job.ResultFileUrl ?? ""),
            new("data", jobJson)
        });
    }
}
```

### 3. Kubernetes Deployment Configuration

#### Slicer Service Deployment
```yaml
# k8s/orcaslicer-deployment.yml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orcaslicer-service
  namespace: printfarmer-production
  labels:
    app: orcaslicer-service
    version: v1
spec:
  replicas: 3
  selector:
    matchLabels:
      app: orcaslicer-service
  template:
    metadata:
      labels:
        app: orcaslicer-service
        version: v1
    spec:
      affinity:
        nodeAffinity:
          requiredDuringSchedulingIgnoredDuringExecution:
            nodeSelectorTerms:
            - matchExpressions:
              - key: node-type
                operator: In
                values:
                - high-cpu
              - key: slicer-enabled
                operator: In
                values:
                - "true"
        podAntiAffinity:
          preferredDuringSchedulingIgnoredDuringExecution:
          - weight: 100
            podAffinityTerm:
              labelSelector:
                matchExpressions:
                - key: app
                  operator: In
                  values:
                  - orcaslicer-service
              topologyKey: kubernetes.io/hostname
      containers:
      - name: orcaslicer
        image: ghcr.io/jpapiez/printfarmer-orcaslicer:latest
        ports:
        - containerPort: 8080
          name: http
        - containerPort: 9090
          name: metrics
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: REDIS_CONNECTION
          value: "redis-cluster:6379"
        - name: MAX_CONCURRENT_JOBS
          value: "2"
        - name: FILE_STORAGE_TYPE
          value: "S3"
        - name: AWS_S3_BUCKET
          value: "printfarmer-files"
        - name: WORKER_ID
          valueFrom:
            fieldRef:
              fieldPath: metadata.name
        resources:
          requests:
            memory: "2Gi"
            cpu: "1000m"
            ephemeral-storage: "10Gi"
          limits:
            memory: "4Gi"
            cpu: "3000m"
            ephemeral-storage: "20Gi"
        livenessProbe:
          httpGet:
            path: /health
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 30
          timeoutSeconds: 10
          failureThreshold: 3
        readinessProbe:
          httpGet:
            path: /health
            port: 8080
          initialDelaySeconds: 10
          periodSeconds: 10
          timeoutSeconds: 5
          failureThreshold: 3
        volumeMounts:
        - name: tmp-storage
          mountPath: /tmp
        - name: slicer-profiles
          mountPath: /app/profiles
          readOnly: true
      volumes:
      - name: tmp-storage
        emptyDir:
          sizeLimit: 20Gi
      - name: slicer-profiles
        configMap:
          name: orcaslicer-profiles
      tolerations:
      - key: "slicer-workload"
        operator: "Equal"
        value: "true"
        effect: "NoSchedule"

---
# Horizontal Pod Autoscaler
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: orcaslicer-service-hpa
  namespace: printfarmer-production
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: orcaslicer-service
  minReplicas: 2
  maxReplicas: 20
  metrics:
  - type: Resource
    resource:
      name: cpu
      target:
        type: Utilization
        averageUtilization: 80
  - type: Resource
    resource:
      name: memory
      target:
        type: Utilization
        averageUtilization: 85
  - type: External
    external:
      metric:
        name: redis_queue_depth
        selector:
          matchLabels:
            queue: orcaslicer
      target:
        type: AverageValue
        averageValue: "5"
  behavior:
    scaleUp:
      stabilizationWindowSeconds: 300
      policies:
      - type: Percent
        value: 100
        periodSeconds: 60
      - type: Pods
        value: 2
        periodSeconds: 60
    scaleDown:
      stabilizationWindowSeconds: 300
      policies:
      - type: Percent
        value: 50
        periodSeconds: 60

---
# Service
apiVersion: v1
kind: Service
metadata:
  name: orcaslicer-service
  namespace: printfarmer-production
  labels:
    app: orcaslicer-service
spec:
  selector:
    app: orcaslicer-service
  ports:
  - name: http
    port: 80
    targetPort: 8080
  - name: metrics
    port: 9090
    targetPort: 9090
  type: ClusterIP

---
# Service Monitor for Prometheus
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: orcaslicer-service
  namespace: printfarmer-production
spec:
  selector:
    matchLabels:
      app: orcaslicer-service
  endpoints:
  - port: metrics
    path: /metrics
    interval: 30s
```

### 4. API Gateway Integration

#### Slicer API Gateway Routes
```yaml
# k8s/api-gateway-slicer-routes.yml
apiVersion: networking.istio.io/v1beta1
kind: VirtualService
metadata:
  name: slicer-services-vs
  namespace: printfarmer-production
spec:
  hosts:
  - api.printfarmer.com
  gateways:
  - printfarmer-gateway
  http:
  # OrcaSlicer routes
  - match:
    - uri:
        prefix: /api/v1/slicing/orca
    route:
    - destination:
        host: orcaslicer-service
        port:
          number: 80
    headers:
      request:
        add:
          x-slicer-type: "orcaslicer"
    timeout: 300s
    retries:
      attempts: 2
      perTryTimeout: 150s
  
  # PrusaSlicer routes
  - match:
    - uri:
        prefix: /api/v1/slicing/prusa
    route:
    - destination:
        host: prusaslicer-service
        port:
          number: 80
    headers:
      request:
        add:
          x-slicer-type: "prusaslicer"
    timeout: 300s
    retries:
      attempts: 2
      perTryTimeout: 150s
  
  # Generic slicing endpoint with automatic routing
  - match:
    - uri:
        prefix: /api/v1/slicing/auto
    route:
    - destination:
        host: slicer-router-service
        port:
          number: 80
    timeout: 300s

---
# Rate limiting for slicing endpoints
apiVersion: networking.istio.io/v1beta1
kind: DestinationRule
metadata:
  name: slicer-services-dr
  namespace: printfarmer-production
spec:
  host: "*.printfarmer-production.svc.cluster.local"
  trafficPolicy:
    connectionPool:
      tcp:
        maxConnections: 50
      http:
        http1MaxPendingRequests: 20
        http2MaxRequests: 100
        maxRequestsPerConnection: 5
        maxRetries: 2
        h2UpgradePolicy: UPGRADE
    circuitBreaker:
      consecutiveGatewayErrors: 3
      interval: 30s
      baseEjectionTime: 30s
      maxEjectionPercent: 50
      minHealthPercent: 30
```

### 5. Monitoring and Observability

#### Prometheus Metrics Configuration
```csharp
// SlicerService.Core/Metrics/SlicerMetrics.cs
public class SlicerMetrics
{
    private readonly Counter _jobsTotal = Metrics
        .CreateCounter("slicer_jobs_total", "Total number of slicing jobs", new[] { "slicer_type", "status" });
    
    private readonly Histogram _jobDuration = Metrics
        .CreateHistogram("slicer_job_duration_seconds", "Duration of slicing jobs", new[] { "slicer_type" });
    
    private readonly Gauge _activeJobs = Metrics
        .CreateGauge("slicer_active_jobs", "Number of currently active slicing jobs", new[] { "slicer_type", "worker_id" });
    
    private readonly Gauge _queueDepth = Metrics
        .CreateGauge("slicer_queue_depth", "Number of jobs in the queue", new[] { "slicer_type" });
    
    private readonly Counter _bytesProcessed = Metrics
        .CreateCounter("slicer_bytes_processed_total", "Total bytes of STL files processed", new[] { "slicer_type" });
    
    private readonly Histogram _fileSize = Metrics
        .CreateHistogram("slicer_file_size_bytes", "Size of processed STL files", new[] { "slicer_type" });

    public void RecordJobStarted(string slicerType, string workerId)
    {
        _jobsTotal.WithLabels(slicerType, "started").Inc();
        _activeJobs.WithLabels(slicerType, workerId).Inc();
    }

    public void RecordJobCompleted(string slicerType, string workerId, TimeSpan duration, bool success)
    {
        _jobsTotal.WithLabels(slicerType, success ? "completed" : "failed").Inc();
        _activeJobs.WithLabels(slicerType, workerId).Dec();
        _jobDuration.WithLabels(slicerType).Observe(duration.TotalSeconds);
    }

    public void UpdateQueueDepth(string slicerType, long depth)
    {
        _queueDepth.WithLabels(slicerType).Set(depth);
    }

    public void RecordFileProcessed(string slicerType, long fileSizeBytes)
    {
        _bytesProcessed.WithLabels(slicerType).Inc(fileSizeBytes);
        _fileSize.WithLabels(slicerType).Observe(fileSizeBytes);
    }
}
```

### 6. Load Balancer and Service Discovery

#### Slicer Router Service
```csharp
// SlicerService.Router/Services/SlicerRouterService.cs
public class SlicerRouterService : ISlicerRouterService
{
    private readonly IServiceDiscovery _serviceDiscovery;
    private readonly ILoadBalancer _loadBalancer;
    private readonly ILogger<SlicerRouterService> _logger;
    private readonly IMemoryCache _cache;

    public async Task<SlicingJobResponse> RouteSlicingJobAsync(AutoSlicingRequest request)
    {
        // Determine optimal slicer based on:
        // 1. File characteristics (size, complexity)
        // 2. Printer compatibility
        // 3. Current queue depths
        // 4. Historical performance

        var slicerType = await DetermineOptimalSlicerAsync(request);
        var availableServices = await _serviceDiscovery.GetHealthyServicesAsync(slicerType);
        
        if (!availableServices.Any())
        {
            throw new NoAvailableSlicersException($"No healthy {slicerType} services available");
        }

        // Load balance across available instances
        var selectedService = _loadBalancer.SelectService(availableServices, request);
        
        // Forward request to selected slicer service
        using var httpClient = new HttpClient();
        var response = await httpClient.PostAsJsonAsync(
            $"{selectedService.BaseUrl}/api/v1/slicing/jobs", 
            request.ToSlicingJobRequest());

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SlicingJobResponse>();

        // Track routing decision for future optimization
        await TrackRoutingDecisionAsync(request, slicerType, selectedService, result);

        return result;
    }

    private async Task<string> DetermineOptimalSlicerAsync(AutoSlicingRequest request)
    {
        // Get file metadata
        var fileInfo = await AnalyzeFileAsync(request.ModelFileUrl);
        
        // Get current queue depths
        var queueDepths = await GetQueueDepthsAsync();
        
        // Calculate slicer scores based on multiple factors
        var scores = new Dictionary<string, double>();
        
        foreach (var slicerType in new[] { "orcaslicer", "prusaslicer" })
        {
            var score = 0.0;
            
            // Factor 1: Printer compatibility (40% weight)
            score += 0.4 * CalculateCompatibilityScore(slicerType, request.PrinterModel);
            
            // Factor 2: File complexity handling (30% weight)
            score += 0.3 * CalculateComplexityScore(slicerType, fileInfo);
            
            // Factor 3: Current load (20% weight)
            score += 0.2 * CalculateLoadScore(slicerType, queueDepths[slicerType]);
            
            // Factor 4: Historical performance (10% weight)
            score += 0.1 * await CalculatePerformanceScoreAsync(slicerType, fileInfo);
            
            scores[slicerType] = score;
        }

        // Select slicer with highest score
        var optimalSlicer = scores.OrderByDescending(kvp => kvp.Value).First().Key;
        
        _logger.LogInformation("Selected {SlicerType} for job routing. Scores: {Scores}", 
            optimalSlicer, string.Join(", ", scores.Select(s => $"{s.Key}:{s.Value:F2}")));

        return optimalSlicer;
    }

    private double CalculateCompatibilityScore(string slicerType, string printerModel)
    {
        // Return compatibility score based on printer model and slicer capabilities
        return slicerType.ToLower() switch
        {
            "orcaslicer" when printerModel.Contains("Bambu", StringComparison.OrdinalIgnoreCase) => 1.0,
            "orcaslicer" when printerModel.Contains("Prusa", StringComparison.OrdinalIgnoreCase) => 0.8,
            "prusaslicer" when printerModel.Contains("Prusa", StringComparison.OrdinalIgnoreCase) => 1.0,
            "prusaslicer" when printerModel.Contains("Bambu", StringComparison.OrdinalIgnoreCase) => 0.7,
            _ => 0.5
        };
    }
}
```

## Acceptance Criteria

### 1. Microservices Architecture
- [ ] Each slicer (OrcaSlicer, PrusaSlicer) runs as independent microservice
- [ ] Services can be deployed independently on different physical machines
- [ ] Container-based deployment with proper resource isolation
- [ ] API-first design with RESTful endpoints and health checks
- [ ] Service discovery enables dynamic scaling and failover
- [ ] Configuration management supports different slicer profiles

### 2. Distributed Job Processing
- [ ] Job queue supports priority-based processing with Redis/RabbitMQ
- [ ] Load balancing distributes jobs across available slicer nodes
- [ ] Job state tracking provides real-time progress updates
- [ ] Retry mechanisms handle failed jobs with exponential backoff
- [ ] Dead letter queues capture permanently failed jobs
- [ ] Job completion delivers results via secure file storage

### 3. Scalability and Performance
- [ ] Horizontal scaling adds/removes slicer nodes based on demand
- [ ] CPU-intensive slicing operations run on dedicated high-CPU nodes
- [ ] Autoscaling responds to queue depth within 2 minutes
- [ ] Resource limits prevent individual jobs from consuming excessive resources
- [ ] Concurrent job processing limited per node based on CPU cores
- [ ] Performance metrics track jobs/hour and average processing time

### 4. Resource Management
- [ ] CPU and memory limits enforced per container
- [ ] Node affinity deploys slicers on appropriate hardware
- [ ] Graceful shutdown handling preserves in-progress jobs
- [ ] Temporary file cleanup prevents disk space exhaustion
- [ ] Storage quotas and retention policies manage file growth
- [ ] Resource requests ensure guaranteed compute capacity

### 5. Communication and Integration
- [ ] Asynchronous job submission via REST API
- [ ] Real-time progress updates via SignalR/WebSockets
- [ ] gRPC communication for high-performance inter-service calls
- [ ] Circuit breaker patterns provide resilient communication
- [ ] API versioning ensures backward compatibility
- [ ] Event-driven architecture enables loose coupling

### 6. Monitoring and Observability
- [ ] Prometheus metrics track job success rates, duration, and throughput
- [ ] Distributed tracing provides end-to-end job visibility
- [ ] Resource utilization monitoring for CPU, memory, and storage
- [ ] Business metrics dashboard shows processing capacity and efficiency
- [ ] Alerting detects service failures and performance degradation
- [ ] Log aggregation provides centralized troubleshooting

## Testing Requirements

### Load Testing
- [ ] **Concurrent job processing** with 50+ simultaneous slicing operations
- [ ] **Queue depth handling** with 1000+ queued jobs
- [ ] **Auto-scaling validation** under varying load patterns
- [ ] **Resource exhaustion testing** with large STL files (>500MB)
- [ ] **Sustained load testing** for 24-hour continuous operation
- [ ] **Mixed workload testing** with different slicer types and priorities

### Integration Testing
- [ ] **Multi-service communication** between API gateway and slicer services
- [ ] **Job lifecycle testing** from submission to completion
- [ ] **Failover scenarios** when slicer nodes become unavailable
- [ ] **File storage integration** for input STL and output G-code files
- [ ] **Monitoring integration** with metrics collection and alerting
- [ ] **Service discovery testing** with dynamic node registration

### Performance Testing
- [ ] **Slicing performance benchmarks** for different file sizes and complexities
- [ ] **Resource utilization profiling** under various load conditions
- [ ] **Network latency testing** between distributed components
- [ ] **Database performance** for job state tracking and history
- [ ] **Storage I/O testing** for large file processing
- [ ] **Memory leak detection** during long-running operations

### Fault Tolerance Testing
- [ ] **Node failure simulation** with job recovery
- [ ] **Network partition testing** between queue and workers
- [ ] **Slicer process crashes** with automatic restart
- [ ] **Storage failures** with backup and recovery
- [ ] **Queue system failures** with persistent job storage
- [ ] **Cascading failure prevention** with circuit breakers

## Implementation Phases

### Phase 1: Core Microservices Architecture (4 weeks)
- OrcaSlicer microservice implementation with Docker containerization
- Basic job queue with Redis Streams
- REST API endpoints for job submission and status
- Container deployment with Kubernetes
- Basic health checks and monitoring

### Phase 2: Job Processing and Queue Management (3 weeks)
- Priority-based job scheduling
- Load balancing across slicer instances
- Job state tracking and persistence
- Retry mechanisms and error handling
- File storage integration (S3/MinIO)

### Phase 3: Auto-scaling and Resource Management (3 weeks)
- Horizontal Pod Autoscaler configuration
- Resource limits and node affinity
- CPU and memory optimization
- Graceful shutdown handling
- Performance tuning and optimization

### Phase 4: Advanced Features (3 weeks)
- PrusaSlicer microservice implementation
- Slicer router with intelligent job distribution
- Advanced monitoring and metrics
- Distributed tracing implementation
- Performance profiling and optimization

### Phase 5: Production Deployment (2 weeks)
- Multi-region deployment configuration
- Production monitoring and alerting
- Load testing and performance validation
- Documentation and operational procedures
- Staff training and knowledge transfer

### Phase 6: Testing and Validation (2 weeks)
- Comprehensive integration testing
- Performance benchmarking
- Fault tolerance validation
- Security assessment
- Production readiness review

## Success Metrics

### Scalability Metrics
- **Concurrent job processing** >50 simultaneous slicing operations
- **Queue throughput** >100 jobs processed per hour per node
- **Auto-scaling efficiency** responds within 2 minutes to load changes
- **Resource utilization** maintains 70-80% CPU usage on slicer nodes
- **Peak capacity scaling** supports 10x normal load without degradation

### Performance Metrics
- **Job processing time** <5 minutes for typical models (<50MB)
- **Queue latency** <30 seconds from submission to processing start
- **API response times** <200ms for job submission and status queries
- **File transfer performance** >10MB/s for STL upload and G-code download
- **System availability** >99.9% uptime for slicing services

### Operational Metrics
- **Deployment flexibility** independent scaling of each slicer type
- **Resource isolation** no interference between different slicing workloads
- **Fault tolerance** automatic recovery from individual node failures
- **Cost efficiency** >30% reduction in infrastructure costs through optimization
- **Maintenance overhead** <2 hours per week for operational tasks

## Dependencies

### External Dependencies
- Kubernetes cluster with high-CPU node pools
- Redis cluster for job queue and caching
- Object storage service (AWS S3, MinIO, etc.)
- Container registry for Docker images
- Load balancer with health checking capabilities
- Monitoring and alerting infrastructure (Prometheus, Grafana)

### Internal Dependencies
- High Availability and Scalability Architecture (#53)
- Production Monitoring and Observability Infrastructure (#50)
- DevOps Automation and CI/CD Pipeline Implementation (#52)
- Security Hardening and HTTPS Configuration (#49)
- Database Security and Automated Backup System (#51)

## Risk Mitigation

### Technical Risks
- **Complex distributed system** through comprehensive testing and monitoring
- **Resource management complexity** through automated orchestration
- **Performance bottlenecks** through profiling and optimization
- **Service dependencies** through circuit breakers and fallback mechanisms
- **Data consistency** through proper job state management

### Operational Risks
- **Increased system complexity** through documentation and automation
- **Staff learning curve** through training and gradual rollout
- **Cost management** through resource optimization and monitoring
- **Deployment complexity** through Infrastructure as Code and CI/CD
- **Troubleshooting difficulty** through observability and logging

---

## Related Issues
- High Availability and Scalability Architecture Implementation (#53)
- Complete End-to-End Integration with Slicing Functionality (#38)
- Production Monitoring and Observability Infrastructure (#50)
- DevOps Automation and CI/CD Pipeline Implementation (#52)
- Security Hardening and HTTPS Configuration (#49)

## References
- [Kubernetes Horizontal Pod Autoscaler](https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/)
- [Redis Streams for Job Queues](https://redis.io/topics/streams-intro)
- [Microservices Patterns](https://microservices.io/patterns/)
- [Docker Multi-Stage Builds](https://docs.docker.com/develop/dev-best-practices/dockerfile_best-practices/)
- [Prometheus Monitoring Best Practices](https://prometheus.io/docs/practices/naming/)