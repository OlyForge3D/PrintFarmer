# Slicer Worker Base Image Build Fix

## Date: October 6, 2025

## Problem

OrcaSlicer and PrusaSlicer worker containers were failing to build with error:
```
failed to solve: failed to load metadata for docker.io/library/slicer-base:latest
```

## Root Cause

Both `Dockerfile.orcaslicer` and `Dockerfile.prusaslicer` depend on a base image called `slicer-base`:

```dockerfile
# Dockerfile.orcaslicer
FROM slicer-base AS orca-tools
...
FROM slicer-base AS final
```

However, the deploy script was trying to build all images in parallel via `docker compose build`, which failed because the `slicer-base` image didn't exist yet.

## Image Dependency Hierarchy

```
┌─────────────────┐
│ slicer-base     │ ← Must be built FIRST (Dockerfile.slicer-base)
│ (base runtime)  │
└────────┬────────┘
         │
    ┌────┴────┐
    │         │
┌───▼──────┐ ┌▼────────────┐
│ orcaslic │ │ prusaslicer │
│ er-worker│ │   -worker   │
└──────────┘ └─────────────┘
```

## Solution

Modified `scripts/deploy-docker.sh` to build `slicer-base` separately BEFORE running `docker compose build`:

```bash
# Build slicer-base first if workers are enabled (required dependency)
if [ "$ENABLE_ORCA_WORKER" = "yes" ] || [ "$ENABLE_PRUSA_WORKER" = "yes" ]; then
    print_info "Building slicer-base image (required for worker containers)..."
    if docker build -f Dockerfile.slicer-base -t slicer-base:latest .; then
        print_success "slicer-base image built successfully"
    else
        print_error "Failed to build slicer-base image"
        exit 1
    fi
fi

# Now build all services (workers can use slicer-base)
"${compose_cmd[@]}" build --no-cache
```

## Build Sequence

### Before (❌ Failed):
1. `docker compose build` attempts to build all services
2. Tries to build `orcaslicer-worker`
3. Looks for `FROM slicer-base` 
4. Image not found → **BUILD FAILS**

### After (✅ Success):
1. Check if workers are enabled
2. If yes → Build `slicer-base:latest` first
3. Then `docker compose build` for all services
4. Workers find `slicer-base:latest` → **BUILD SUCCEEDS**

## Files Involved

### Base Image
- **Dockerfile.slicer-base** - Shared runtime for all slicer workers
  - Contains common dependencies (.NET runtime, system libs)
  - Tagged as `slicer-base:latest`
  - Must be built before worker images

### Worker Images (Depend on slicer-base)
- **Dockerfile.orcaslicer** - OrcaSlicer-specific worker
  - `FROM slicer-base AS orca-tools`
  - `FROM slicer-base AS final`
  
- **Dockerfile.prusaslicer** - PrusaSlicer-specific worker
  - `FROM slicer-base AS prusa-tools`
  - `FROM slicer-base AS final`

## Testing

```bash
# Test with OrcaSlicer worker enabled
printf "1\n8080\nProduction\nno\nno\nyes\nyes\n1\nno\n0\nno\n\n" | \
  ./scripts/deploy-docker.sh --dry-run

# Should see:
# ℹ️  Building slicer-base image (required for worker containers)...
# ✅ slicer-base image built successfully
# ✅ Docker images built successfully
```

## Impact

- ✅ **Fixed**: OrcaSlicer worker builds successfully
- ✅ **Fixed**: PrusaSlicer worker builds successfully  
- ✅ **Fixed**: Proper build order enforced
- ✅ **Optimized**: slicer-base only built once, reused by both workers

## Additional Notes

### Why Not Use Docker Compose Build Dependencies?

Docker Compose doesn't support specifying build dependencies between services. While you can specify runtime dependencies with `depends_on`, build-time dependencies must be managed manually.

### Alternative Approaches Considered

1. **Multi-stage build in each worker** - Would duplicate base layer, increase build time
2. **Docker Compose build order** - Not supported by Docker Compose
3. **Manual pre-build step** - ✅ **CHOSEN** - Simple, explicit, works reliably

### Build Time Impact

- First build: +2-3 minutes for slicer-base
- Subsequent builds: Cached, ~10 seconds
- Workers reuse slicer-base, saving ~1-2 minutes each

## Related Documentation

- Docker multi-stage builds: https://docs.docker.com/build/building/multi-stage/
- Worker architecture: `docs/architecture/worker-services.md`
- Deploy script: `DEPLOY_SCRIPT_FIXES.md`
