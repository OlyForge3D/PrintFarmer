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
| `ConnectionStrings__Redis` | Redis connection (job queue, pub/sub)    | `redis:6379` in compose / local  |

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

## Future Enhancements

- SBOM & provenance attestation per image
- Multi-arch (amd64 + arm64) build matrix
- Cached AppImage acquisition via build args / ARG override

## Migration Notes

Older configurations (pre-Sept 2025) used a generic `Dockerfile.base` and a shared `slicer-worker` project. Both have been removed. Update any CI pipelines still referencing `Dockerfile.base` or `src/slicer-worker` to use:
 - `Dockerfile.orcaslicer`
 - `src/orcaslicer-worker/`

> Migration Note (Sept 2025): The legacy generic worker has been retired in favor of per-engine isolation. Avoid reintroducing a monolithic worker pattern.
