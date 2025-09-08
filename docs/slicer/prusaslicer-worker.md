# PrusaSlicer Worker (Dedicated Engine Service)

Status: EXPERIMENTAL (mirrors OrcaSlicer worker pattern). Will replace any legacy Prusa logic previously embedded in the generic `slicer-worker`.

## Purpose

Provides an isolated microservice that performs STL → G-code slicing using PrusaSlicer. This separation:

- Eliminates cross‑engine conditional code
- Allows independent scaling & updates of PrusaSlicer runtime
- Enables per‑engine dependency pinning and security scanning
- Simplifies future addition of more engines (e.g., Cura, Bamboo Studio) by cloning the pattern

## Image Composition

```
Dockerfile.prusaslicer
  ├─ Stage: build (dotnet/sdk:9.0)  -> publishes Farm.PrusaSlicer.Worker
  └─ Stage: final (slicer-base) + installs PrusaSlicer AppImage -> /usr/local/bin/prusa-slicer
```

Now re-layered on shared `Dockerfile.slicer-base` (GTK/offscreen libs, xvfb, non-root user) eliminating duplication.

## Key Paths

- Binary: `/usr/local/bin/prusa-slicer`
- Working directory: `/app/temp`
- Health endpoints:
  - Liveness: `GET /healthz`
  - Readiness: `GET /ready`

## Environment Variables

| Variable                   | Default                     | Description                                |
| -------------------------- | --------------------------- | ------------------------------------------ |
| `Worker__PrusaSlicerPath`  | /usr/local/bin/prusa-slicer | Path to slicer binary                      |
| `Worker__WorkingDirectory` | /app/temp                   | Per-job temp work tree                     |
| `Worker__StorageEndpoint`  | http://api:5245             | API endpoint for (future) upload callbacks |

## Adding to docker-compose

Example service block (microservices file):

```yaml
prusaslicer-worker:
  build:
    context: .
    dockerfile: Dockerfile.prusaslicer
  image: prusaslicer-worker
  restart: unless-stopped
  environment:
    ASPNETCORE_ENVIRONMENT: Production
  depends_on:
    - api
```

## Verification Script

Implemented: `scripts/verify-prusaslicer-worker.sh`

- Starts ephemeral container
- Waits for `/healthz`
- Verifies `/usr/local/bin/prusa-slicer` executable
- Prints first line of `--help` (non-fatal if warnings)

## Migration Notes

Legacy generic worker is now deprecated. Remaining Prusa logic residing there should be removed once integration tests are updated to target this service directly. After test parity:

1. Delete `slicer-worker` project
2. Remove any cross-engine abstractions no longer necessary
3. Introduce unified orchestration (API queue dispatches by engine type)

## Next Steps

- [x] Add verification script
- [x] Refactor to reuse `Dockerfile.slicer-base`
- [ ] Wire CI job to build & verify image
- [ ] Update integration tests to spawn prusaslicer-worker container
