# PrusaSlicer Worker Container

Dedicated worker service providing distributed slicing via PrusaSlicer, layered on the neutral `slicer-base` image.

## Architecture Layers

1. `slicer-base` (Dockerfile.slicer-base): Common runtime deps, non-root user, health infra
2. `Dockerfile.prusaslicer`: Installs PrusaSlicer AppImage + publishes Prusa worker app

## Key Files

| File                      | Purpose                                             |
| ------------------------- | --------------------------------------------------- |
| `Dockerfile.slicer-base`  | Reusable base (no slicer binaries)                  |
| `Dockerfile.prusaslicer`  | Prusa worker image (adds PrusaSlicer + worker app)  |
| `src/prusaslicer-worker/` | Worker implementation (Prusa pipeline)              |
| `docker-compose.yml`      | Service definition `prusaslicer-worker`             |

## Environment Variables

Shared worker variables are defined in `docs/slicer/worker-environment.md`. Only Prusa-specific and notable mappings are shown here.

| Variable                   | Description                           | Default / Example                 |
| -------------------------- | ------------------------------------- | --------------------------------- |
| `ASPNETCORE_URLS`          | Internal binding (always 8080)        | `http://+:8080`                   |
| (Host port mapping)        | External port via compose             | 8082 -> 8080 (example)            |
| `Worker__PrusaSlicerPath`  | Prusa binary path override            | `/usr/local/bin/prusa-slicer`     |
| `Worker__WorkingDirectory` | Temp working dir for jobs             | `/app/temp`                       |
| `Worker__StorageEndpoint`  | API endpoint for artifact uploads     | `http://api:5245` (compose net)   |
| `ConnectionStrings__Redis` | Redis connection (queue + pub/sub)    | `redis:6379` (compose)            |

## Health Endpoints

| Path       | Purpose                    |
| ---------- | -------------------------- |
| `/healthz` | Liveness (fast)            |
| `/ready`   | Readiness (includes Redis) |

## Build & Run (Standalone)

```bash
# Build base and Prusa worker
docker build -f Dockerfile.slicer-base -t printfarmer/slicer-base .
docker build -f Dockerfile.prusaslicer -t printfarmer/prusaslicer-worker .

# Run (example)
docker run --rm -p 8082:8080 \
  -e ConnectionStrings__Redis=host.docker.internal:6379 \
  -e Worker__StorageEndpoint=http://host.docker.internal:5245 \
  printfarmer/prusaslicer-worker
```

## Verification Script

`scripts/verify-prusaslicer-worker.sh`:
- Launches ephemeral container
- Waits for `/healthz`
- Confirms binary executable
- Outputs first line of `--help` (non-fatal warnings allowed)

## Migration Notes

Older configurations (pre-Sept 2025) relied on a generic `slicer-worker` and (now deleted) `Dockerfile.base`. Those have been removed. Update any CI pipelines referencing them to use:
 - `Dockerfile.prusaslicer`
 - `src/prusaslicer-worker/`

> Per-engine isolation is the permanent model. Avoid reintroducing a monolithic worker.

## Future Enhancements

- SBOM & provenance attestation
- Multi-arch builds (amd64 + arm64)
- Cached AppImage acquisition via build arg
- Structured metrics (jobs, durations, failures)
