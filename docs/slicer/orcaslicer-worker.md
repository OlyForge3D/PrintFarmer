# OrcaSlicer Worker Container

This service provides distributed slicing using OrcaSlicer, built as a dedicated container layered on a neutral slicer base image.

## Architecture

Layers:

1. `slicer-base` (Dockerfile.slicer-base): Common runtime deps, non-root user, health infra.
2. `Dockerfile.orcaslicer`: Adds OrcaSlicer AppImage + published .NET worker (current implementation uses `src/slicer-worker`).

## Key Files

| File                     | Purpose                                              |
| ------------------------ | ---------------------------------------------------- |
| `Dockerfile.slicer-base` | Reusable base (no slicer binaries)                   |
| `Dockerfile.orcaslicer`  | Orca worker image (installs OrcaSlicer + worker app) |
| `src/slicer-worker/`     | Worker implementation (Orca pipeline)                |
| `docker-compose.yml`     | Service definition `orcaslicer-worker`               |

## Environment Variables

| Variable                   | Description               | Default                     |
| -------------------------- | ------------------------- | --------------------------- |
| `ASPNETCORE_URLS`          | Kestrel binding           | `http://+:8081` (compose)   |
| `Worker__OrcaSlicerPath`   | Orca binary path          | `/usr/local/bin/orcaslicer` |
| `Worker__WorkingDirectory` | Temp working dir for jobs | `/app/temp`                 |
| `Worker__StorageEndpoint`  | API endpoint for uploads  | `http://api:5245`           |
| `ConnectionStrings__Redis` | Redis connection          | `localhost:6379`            |

## Health Endpoints

| Path       | Purpose                    |
| ---------- | -------------------------- |
| `/healthz` | Liveness (fast)            |
| `/ready`   | Readiness (includes Redis) |

## Build & Run (Standalone)

```bash
# Build base and orca worker
docker build -f Dockerfile.slicer-base -t printfarmer/slicer-base .
docker build -f Dockerfile.orcaslicer -t printfarmer/orcaslicer-worker .

# Run (example)
docker run --rm -p 8081:8080 \
  -e ConnectionStrings__Redis=host.docker.internal:6379 \
  -e Worker__StorageEndpoint=http://host.docker.internal:5245 \
  printfarmer/orcaslicer-worker
```

## Future Enhancements

- Introduce dedicated `src/orcaslicer-worker` project (current phase reuses `slicer-worker`).
- Add SBOM & provenance attestation per image.
- Multi-arch (amd64 + arm64) build matrix.
- Cache AppImage download via build args / ARG override.

## Migration Notes

Older configuration used `Dockerfile.base` directly for OrcaSlicer. That file is now replaced functionally by the base + specialized layering. Update any CI pipelines referencing `Dockerfile.base` for Orca to `Dockerfile.orcaslicer`.

> Migration Note (Sept 2025): The legacy `slicer-worker/Services/OrcaSlicingPipelineService.cs` has been removed. Any references should target the dedicated project under `src/orcaslicer-worker/`.
