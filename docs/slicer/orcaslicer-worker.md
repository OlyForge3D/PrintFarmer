# OrcaSlicer Worker Container

This service provides distributed slicing using OrcaSlicer, built as a dedicated container layered on a neutral slicer base image.

## Architecture

Layers:

1. `slicer-base` (Dockerfile.slicer-base): Common runtime deps, non-root user, health infra.
2. `Dockerfile.orcaslicer`: Adds OrcaSlicer AppImage + published dedicated Orca worker project.

## Key Files

| File                     | Purpose                                              |
| ------------------------ | ---------------------------------------------------- |
| `Dockerfile.slicer-base` | Reusable base (no slicer binaries)                   |
| `Dockerfile.orcaslicer`  | Orca worker image (installs OrcaSlicer + worker app) |
| `src/orcaslicer-worker/` | Worker implementation (Orca pipeline)                |
| `docker-compose.yml`     | Service definition `orcaslicer-worker`               |

## Environment Variables

Shared variables are defined centrally in `docs/slicer/worker-environment.md` (avoid duplication). Below are Orca-specific or notable overrides.

| Variable                   | Description                              | Default                         |
| -------------------------- | ---------------------------------------- | -------------------------------- |
| `ASPNETCORE_URLS`          | Internal binding (always port 8080)      | `http://+:8080`                  |
| (Host port mapping)        | External host port via compose           | 8081 -> 8080 (example)           |
| `Worker__OrcaSlicerPath`   | Orca binary path override                | `/usr/local/bin/orcaslicer`      |
| `Worker__WorkingDirectory` | Temp working dir for jobs                | `/app/temp`                      |
| `Worker__StorageEndpoint`  | API endpoint for artifact uploads        | `http://api:8080` (compose net)  |
| `Worker__MaxConcurrentJobs`| Maximum concurrent slicing jobs          | `1`                              |
| `ConnectionStrings__Redis` | Redis connection (job queue, pub/sub)    | `redis:6379` in compose / local  |

### Worker Registration Variables (New - Phase 3)

The worker now registers itself with the central slicer registry API on startup:

| Variable                            | Description                              | Default                |
| ----------------------------------- | ---------------------------------------- | ---------------------- |
| `SlicerRegistry__ApiBaseUrl`        | Base URL of the API registry endpoint    | `http://api:5245`      |
| `SlicerRegistry__ServiceName`       | Name to register under                   | `orcaslicer-worker`    |
| `SlicerRegistry__Version`           | Version string for this worker           | `1.0.0`                |
| `SlicerRegistry__Host`              | Worker's public URL                      | `http://orcaslicer-worker:8080` |
| `SlicerRegistry__HeartbeatIntervalSeconds` | Heartbeat frequency (seconds)     | `30`                   |
| `SlicerRegistry__ApiKey`            | Optional API key for authentication      | (empty)                |

## Health Endpoints

| Path       | Purpose                    |
| ---------- | -------------------------- |
| `/healthz` | Liveness (fast)            |
| `/ready`   | Readiness (includes Redis) |

## Build & Run (Standalone)

```bash
# Build base and Orca worker
docker build -f Dockerfile.slicer-base -t printfarmer/slicer-base .
docker build -f Dockerfile.orcaslicer -t printfarmer/orcaslicer-worker .

# Run (example)
docker run --rm -p 8081:8080 \
  -e ConnectionStrings__Redis=host.docker.internal:6379 \
  -e Worker__StorageEndpoint=http://host.docker.internal:5245 \
  printfarmer/orcaslicer-worker
```

## Worker Registration Flow

**New in Phase 3 (Oct 2025):** The worker now registers itself with the central slicer registry on startup:

1. **Startup**: Worker waits 5 seconds for initialization, then calls `POST /api/slicers/register`
2. **Registration**: API returns a `serviceId` and `apiKey` that the worker stores in memory
3. **Heartbeat**: Every 30 seconds (configurable), worker calls `POST /api/slicers/{id}/heartbeat` with:
   - Current status (`Online`, `Busy`, or `Draining`)
   - Free capacity slots (calculated from `MaxConcurrentJobs - ActiveJobs`)
4. **Shutdown**: On SIGTERM, worker calls `POST /api/slicers/{id}/deregister` before exiting

This enables the UI to:
- Display available slicing workers in real-time
- Show capacity and queue depth per worker
- Route jobs to workers with available capacity

## Future Enhancements

- SBOM & provenance attestation per image
- Multi-arch (amd64 + arm64) build matrix
- Cached AppImage acquisition via build args / ARG override
- Worker UI embedding for advanced slicer configuration

## Migration Notes

Older configurations (pre-Sept 2025) used a generic `Dockerfile.base` and a shared `slicer-worker` project. Both have been removed. Update any CI pipelines still referencing `Dockerfile.base` or `src/slicer-worker` to use:
 - `Dockerfile.orcaslicer`
 - `src/orcaslicer-worker/`

> Migration Note (Sept 2025): The legacy generic worker has been retired in favor of per-engine isolation. Avoid reintroducing a monolithic worker pattern.
