# Quick Fix: Rebuild Frontend Container with New Nginx Config

## The Issue
You're still seeing health check failures because the running Docker containers were built with the **old Nginx configuration** that transforms JSON to plain text.

## Why Rebuild is Needed
The Nginx config file (`deploy/nginx/nginx.conf`) is **copied into the Docker image at build time**:
```dockerfile
COPY deploy/nginx/nginx.conf /etc/nginx/nginx.conf
```

Changing the file on disk doesn't affect already-built containers.

## Solution: Rebuild & Redeploy

### Option 1: Quick Rebuild (Recommended)
```bash
# SSH to your server
ssh pi@10.0.0.75
cd /home/pi/pfarm

# Pull latest changes
git pull origin dev/jpapiez/logging-db-consolidation

# Rebuild and restart just the frontend/web container
# (Monolith deployment):
docker compose build web --no-cache
docker compose up -d web

# (Microservices deployment):
docker compose -f docker-compose.microservices.yml build frontend --no-cache
docker compose -f docker-compose.microservices.yml up -d frontend
```

### Option 2: Full Redeploy (Clean Slate)
```bash
# Use the deploy script
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh
```

### Option 3: Just Rebuild Without Tear-Down
```bash
# Deploy script with existing config
./scripts/deploy-docker.sh
# It will rebuild images and restart containers
```

## Verify the Fix

After rebuilding, test the health endpoints:

```bash
# Should now return proper JSON
curl -i http://10.0.0.75:8080/healthz
# Expected:
# Content-Type: application/json; charset=utf-8
# {"status":"ok"}

curl http://10.0.0.75:8080/health | jq
# Expected:
# {
#   "status": "Healthy",
#   "results": {...}
# }
```

## Why This Happens

Docker images are **immutable** - once built, they don't change. The Nginx config is copied during the build process:

1. **Build time**: `COPY deploy/nginx/nginx.conf /etc/nginx/` → Config baked into image
2. **Run time**: Container uses the baked-in config
3. **File change**: Updating the file on disk doesn't affect running containers

This is actually a **good thing** for reproducibility, but it means you must rebuild after config changes.

## Best Practice Going Forward

When you change any files that get copied into Docker images:
- Nginx configs
- Application code
- Static assets
- Dockerfiles

You **must rebuild** the affected container images:
```bash
docker compose build [service-name]  # Rebuild specific service
docker compose up -d [service-name]  # Restart with new image
```

## No Data Loss

Rebuilding containers **does NOT delete**:
- ✅ Database data (stored in volumes)
- ✅ Uploaded files (stored in volumes)
- ✅ Configuration (stored in volumes or .env files)

It only updates the application code and configs.
