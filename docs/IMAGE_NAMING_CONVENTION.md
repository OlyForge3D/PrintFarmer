# PrintFarmer Image Naming Convention

## Overview

All Docker images built by PrintFarmer use the `printfarmer-` prefix for consistent branding and easy identification of custom-built components.

## Image Naming Standard

**Pattern:** `printfarmer-<service-name>`

All PrintFarmer-specific Docker images follow this naming convention:

| Service | Image Name | Purpose |
|---------|-----------|----------|
| Base Image | `printfarmer-slicer-base` | Shared base image for slicer workers |
| API | `printfarmer-api` | ASP.NET Core API backend |
| Frontend | `printfarmer-frontend` | React TypeScript UI (Nginx) |
| OrcaSlicer Worker | `printfarmer-orcaslicer-worker` | Distributed OrcaSlicer processing |
| PrusaSlicer Worker | `printfarmer-prusaslicer-worker` | Distributed PrusaSlicer processing |

## External Dependencies

The following external images are used as-is without the prefix:

- `redis:7-alpine` - Job queue and caching
- `postgres:15-alpine` - Database (microservices mode)
- `nginx:alpine` - Web server (if used separately)

## Implementation

### Docker Compose Files

All compose files specify the image name explicitly:

```yaml
services:
  api:
    build:
      context: .
      dockerfile: Dockerfile.api
    image: printfarmer-api  # Explicit image name
```

**Files updated:**
- `docker-compose.yml` (monolithic deployment)
- `docker-compose.microservices.yml` (microservices deployment)

### Dockerfiles

Worker images reference the base image with the new prefix:

```dockerfile
FROM printfarmer-slicer-base AS final
```

**Files updated:**
- `Dockerfile.orcaslicer`
- `Dockerfile.prusaslicer`

### Build Scripts

The deployment script builds the base image with the new name:

```bash
docker build -f Dockerfile.slicer-base -t printfarmer-slicer-base:latest .
```

**Files updated:**
- `scripts/deploy-docker.sh`

## Migration from Old Names

If you have existing images from before this naming convention was implemented:

### Check for Old Images

```bash
docker images | grep -E 'slicer-base|orcaslicer-worker|prusaslicer-worker' | grep -v printfarmer
```

### Remove Old Images (Optional)

```bash
# Remove old images to avoid confusion
docker rmi slicer-base:latest orcaslicer-worker:latest prusaslicer-worker:latest
```

### Rebuild with New Names

```bash
# Use the deploy script (automatically uses new names)
./scripts/deploy-docker.sh

# Or build manually
docker build -f Dockerfile.slicer-base -t printfarmer-slicer-base:latest .
docker compose build
```

## Benefits

1. **Clear Identification**: Easy to identify PrintFarmer images vs. external dependencies
2. **Consistency**: All custom images follow the same naming pattern
3. **Docker Hub Ready**: Namespace prevents conflicts if publishing to registries
4. **Professional**: Consistent branding for the project

## Related Documentation

- [Deployment Overview](../DEPLOYMENT_OVERVIEW.md) - General deployment architecture
- [Docker Deployment](../DOCKER_DEPLOYMENT.md) - Docker-specific deployment guide
- [Local Development](../LOCAL_DEVELOPMENT.md) - Running locally without Docker
