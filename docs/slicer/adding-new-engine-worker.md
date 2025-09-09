# Adding a New Slicer Engine Worker

This guide walks through creating a new dedicated engine worker (e.g., Cura, SuperSlicer) following the current per‑engine pattern.

## High-Level Flow
1. API enqueues slice job (engine-specific queue name)
2. Redis delivers job to engine worker
3. Worker downloads model/assets, invokes engine binary, streams progress
4. Worker uploads resulting G-code back to API / storage service
5. Worker reports status + metrics (success/failure, duration)

## Naming & Conventions
- Project folder: `src/<engine>slice-worker/` (e.g., `src/cura-worker/`)
- Assembly/Product name: `Farm.Worker.<Engine>` (PascalCase)
- Docker image tag: `printfarmer/<engine>-worker`
- Redis queue name: `slicing:<engine>:jobs`
- Logger category prefix: `Slicing.<Engine>`

## Required Artifacts
| Artifact | Purpose |
|----------|---------|
| `<engine>-worker.csproj` | Worker project file (.NET 9, ASP.NET Core minimal host) |
| `Program.cs` | Host builder, DI wiring, hosted services registration |
| `Services/<Engine>SlicingPipelineService.cs` | Orchestrates job lifecycle (download → run → upload) |
| `Services/QueueConsumerService.cs` | Dequeues Redis jobs, invokes pipeline |
| `Services/<Engine>BinaryLocator.cs` | Discovers or validates engine binary path |
| `Health/ReadinessHealthCheck.cs` | Ensures dependencies (Redis, binary) are ready |
| `Health/LivenessHealthCheck.cs` | Simple process heartbeat |
| `Dockerfile.<engine>` | Image layering on `Dockerfile.slicer-base` |
| `appsettings.json` | Minimal config (Redis, logging) |
| `README.md` (optional) | Engine specific notes |

## Environment Variables (Pattern)
| Variable | Purpose | Example |
|----------|---------|---------|
| `ASPNETCORE_URLS` | Kestrel binding | `http://+:8080` |
| `ConnectionStrings__Redis` | Redis endpoint | `redis:6379` |
| `Worker__StorageEndpoint` | API/storage base URL | `http://api:5245` |
| `Worker__<Engine>BinaryPath` | Explicit path override | `/opt/engine/bin/app` |
| `Worker__WorkingDirectory` | Temp workspace | `/app/temp` |
| `Worker__MaxConcurrentJobs` | Concurrency cap | `1` |

## DI / Host Skeleton (Program.cs excerpt)
```csharp
var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddHttpClient();
services.AddSingleton<JobQueueConsumer>();
services.AddSingleton<EngineBinaryLocator>();
services.AddSingleton<EngineSlicingPipelineService>();
services.AddHostedService<QueueConsumerHostedService>();
services.AddHealthChecks()
    .AddCheck<LivenessHealthCheck>("liveness")
    .AddCheck<ReadinessHealthCheck>("readiness");

var app = builder.Build();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/ready");
app.Run();
```

## Queue & Payload Contract
Use the existing unified `SlicingJob` / `SlicingResult` DTOs from the shared library if applicable. If extending, ensure:
- Backwards compatible additions (additive properties) where feasible
- Non-engine-specific fields remain in shared contracts
- Engine-specific metadata grouped under an `engine` or `<engine>Name` suffix property

## Progress Reporting
Recommended callbacks:
- Queue accepted → status: `Queued`
- Engine process start → status: `Starting`
- Periodic layer/percent updates → status: `Running` (include `progress` 0–100, `currentLayer`, if available)
- Successful completion → status: `Completed`
- Failure (with reason) → status: `Failed`
- Cancellation → status: `Canceled`

## Error Handling Patterns
| Scenario | Action |
|----------|--------|
| Binary missing | Mark readiness unhealthy; retry periodically |
| Redis transient failure | Log warning; exponential backoff; continue |
| Job payload corrupt | Mark job Failed; include validation errors |
| Upload failure (transient) | Retry with capped attempts (e.g., 3) |
| Upload failure (permanent) | Failed with reason + preserve local artifact for debug |

## Dockerfile Pattern
```Dockerfile
# syntax=docker/dockerfile:1
FROM printfarmer/slicer-base:latest as base

# Install engine binary
ADD https://example.com/engine/Engine.AppImage /opt/engine/Engine.AppImage
RUN chmod +x /opt/engine/Engine.AppImage \
    && ln -s /opt/engine/Engine.AppImage /usr/local/bin/engine

# Final stage
FROM base AS final
WORKDIR /app
COPY ./bin/Release/net9.0/publish/ .
ENV ASPNETCORE_URLS=http://+:8080
USER sliceruser
ENTRYPOINT ["dotnet", "Farm.Worker.Engine.dll"]
```

## docker-compose Addition (Example)
```yaml
  cura-worker:
    image: printfarmer/cura-worker:dev
    build:
      context: .
      dockerfile: Dockerfile.cura
    environment:
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Redis: redis:6379
      Worker__StorageEndpoint: http://api:5245
    depends_on:
      - redis
    restart: unless-stopped
```

## Metrics & Observability (Recommended)
- Emit structured logs with jobId, engine, durationMs
- Add counters: `slicing_jobs_total`, `slicing_failures_total`
- Histogram: `slicing_job_duration_seconds`
- Gauge: `slicing_jobs_active`

## Graceful Shutdown
1. Stop dequeuing new jobs
2. Allow in-flight job(s) to finish (bounded by timeout)
3. Flush final status updates
4. Exit non-zero only if unrecoverable fatal state

## Readiness Checklist
- [ ] Binary present & executable
- [ ] Redis connectivity verified
- [ ] Working directory writable
- [ ] Configuration parsed (log on load)
- [ ] Queue subscription active

## Common Pitfalls
| Pitfall | Mitigation |
|---------|------------|
| Hard-coded paths | Use config + detection fallback |
| Silent engine failures | Capture stdout/stderr; log truncated tail on failure |
| Infinite hangs | Add max wall-clock duration per job |
| Disk bloat | Periodic cleanup of temp dirs older than N hours |

## Adding Engine-Specific Options
Prefer feature flags or a namespaced config section: `"Worker": { "Engine": { ... } }`.
Document new options in the engine worker README and keep defaults safe.

## Validation Before Commit
- Build succeeds: `dotnet build`
- Publish works: `dotnet publish -c Release`
- Docker image builds locally
- Health endpoints respond
- Simulated job end-to-end (local script)

## Removal of Legacy Base
`Dockerfile.base` has been deleted; do not reintroduce a generic monolithic worker. Each engine should remain isolated.

## Future Enhancements (Global)
- Pluggable engine registry loaded at startup (metadata only)
- Shared sidecar for artifact virus scanning
- Centralized metrics exporter (push-based)

---
Questions? Open an issue or start a discussion titled "New Engine Worker: <EngineName>".
