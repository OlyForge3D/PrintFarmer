# PrintFarmer Base Slicer Worker Container

This document (LEGACY / DEPRECATED) described the original combined base container image (`Dockerfile.base`).

The architecture has moved to a layered model:

| Old               | New Replacement                                     |
| ----------------- | --------------------------------------------------- |
| `Dockerfile.base` | `Dockerfile.slicer-base` (neutral runtime only)     |
| (embedded Orca)   | `Dockerfile.orcaslicer` (adds Orca binary + worker) |
| (future engines)  | Dedicated engine worker Dockerfiles                 |

If you are building new images, DO NOT use `Dockerfile.base`. It now fails fast with a deprecation notice.

## Overview

The new neutral base (`Dockerfile.slicer-base`) provides:

- **Hardened Security**: Non-root user, minimal attack surface
- **Health Endpoints**: Liveness and readiness probes for container orchestration
- **Graceful Shutdown**: Proper SIGTERM handling with configurable timeout
- **Minimal Runtime**: Optimized ASP.NET Core container for slicer workloads

## Container Architecture

### Base Image

- **Build**: `mcr.microsoft.com/dotnet/sdk:9.0`
- **Runtime**: `mcr.microsoft.com/dotnet/aspnet:9.0`
- **User**: Non-root `sliceruser` (UID/GID 1000)

### Security Features

- Non-privileged user execution
- Minimal package installation (curl for health checks only)
- Read-only application directory structure
- Secure default environment variables

## Health Endpoints

### Liveness Probe: `/healthz`

**Purpose**: Kubernetes liveness probe - indicates if the container process is alive and responding.

**Response Format**:

```json
{
  "status": "ok",
  "timestamp": "2024-01-15T10:30:00Z"
}
```

**Usage**:

```bash
curl -f http://localhost:8080/healthz
```

**Health Check Logic**:

- Returns 200 if the process can respond to HTTP requests
- Includes uptime, process ID, and machine name in diagnostics
- Restarts container if check fails (managed by orchestrator)

### Readiness Probe: `/ready`

**Purpose**: Kubernetes readiness probe - indicates if the worker is ready to accept and process jobs.

**Response Format**:

```json
{
  "status": "ready",
  "timestamp": "2024-01-15T10:30:00Z"
}
```

**Usage**:

```bash
curl -f http://localhost:8080/ready
```

**Readiness Criteria**:

- Worker is initialized ✓
- Worker is not shutting down ✓
- Active jobs < maximum concurrent jobs ✓
- All dependencies are available ✓

## Graceful Shutdown

### SIGTERM Handling

The container implements graceful shutdown when receiving SIGTERM:

1. **Signal Reception**: Registers for `SIGTERM` via `IHostApplicationLifetime.ApplicationStopping`
2. **State Update**: Marks worker as shutting down (stops accepting new jobs)
3. **Job Completion**: Waits up to 30 seconds for active jobs to complete
4. **Forced Shutdown**: Proceeds with shutdown after timeout

### Configuration

```csharp
// Configurable shutdown timeout (default: 30 seconds)
var maxWaitTime = TimeSpan.FromSeconds(30);
```

### Testing Graceful Shutdown

```bash
# Start container
docker run -d --name slicer-worker-test printfarmer/slicer-worker-base

# Send SIGTERM and verify graceful shutdown
docker stop --time=35 slicer-worker-test

# Check logs for shutdown behavior
docker logs slicer-worker-test
```

## Usage

docker build -f Dockerfile.base -t printfarmer/slicer-worker-base .

### Building the New Base Image

```bash
docker build -f Dockerfile.slicer-base -t printfarmer/slicer-base .
```

### Building Orca Worker

```bash
docker build -f Dockerfile.orcaslicer -t printfarmer/orcaslicer-worker .
```

### Running the Container

```bash
# Basic run
docker run -p 8080:8080 printfarmer/slicer-base

# With environment overrides
docker run -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  printfarmer/slicer-base
```

### Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: slicer-worker
spec:
  replicas: 3
  selector:
    matchLabels:
      app: slicer-worker
  template:
    metadata:
      labels:
        app: slicer-worker
    spec:
      containers:
        - name: worker
          image: printfarmer/slicer-worker-base:latest
          ports:
            - containerPort: 8080
          livenessProbe:
            httpGet:
              path: /healthz
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 30
            timeoutSeconds: 5
            failureThreshold: 3
          readinessProbe:
            httpGet:
              path: /ready
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
            timeoutSeconds: 5
            failureThreshold: 3
          resources:
            requests:
              memory: "128Mi"
              cpu: "100m"
            limits:
              memory: "512Mi"
              cpu: "500m"
          securityContext:
            runAsNonRoot: true
            runAsUser: 1000
            runAsGroup: 1000
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: false
```

## Extending the Base Image

### Custom Slicer Worker

```dockerfile
FROM printfarmer/slicer-worker-base as base

# Add your slicer-specific files
COPY slicer-configs/ /app/configs/
COPY custom-scripts/ /app/scripts/

# Override entrypoint if needed
ENTRYPOINT ["dotnet", "Farm.Slicer.Worker.dll", "--worker-type=custom"]
```

### Environment Variables

| Variable                  | Description               | Default               |
| ------------------------- | ------------------------- | --------------------- |
| `ASPNETCORE_URLS`         | Listen addresses          | `http://0.0.0.0:8080` |
| `ASPNETCORE_ENVIRONMENT`  | Environment name          | `Production`          |
| `WORKER_MAX_JOBS`         | Max concurrent jobs       | `{CPU_COUNT}`         |
| `WORKER_SHUTDOWN_TIMEOUT` | Graceful shutdown timeout | `30`                  |

## Monitoring and Observability

### Container Metrics

- Process uptime and resource usage
- Active job count and capacity
- Health check response times
- Graceful shutdown statistics

### Log Format

```
[2024-01-15 10:30:00] [INFO] Graceful shutdown service started. Worker ready to handle SIGTERM.
[2024-01-15 10:30:15] [DEBUG] Liveness check - Worker has been running for 00:00:15
[2024-01-15 10:30:15] [DEBUG] Readiness check - Worker ready: True, ActiveJobs: 0/4
[2024-01-15 10:35:00] [INFO] SIGTERM received. Initiating graceful shutdown...
[2024-01-15 10:35:05] [INFO] All active jobs completed. Shutdown can proceed.
```

## Troubleshooting

### Health Check Failures

```bash
# Check liveness endpoint
curl -v http://localhost:8080/healthz

# Check readiness endpoint
curl -v http://localhost:8080/ready

# Inspect container logs
docker logs slicer-worker-container
```

### Common Issues

1. **Port conflicts**: Ensure port 8080 is available
2. **Permission errors**: Verify non-root user has proper permissions
3. **Startup failures**: Check .NET runtime dependencies
4. **Health check timeouts**: Adjust timeout values for slower systems

### Debug Mode

```bash
# Run with debug logging
docker run -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Logging__LogLevel__Default=Debug \
  printfarmer/slicer-worker-base
```

## Security Considerations

- Container runs as non-root user (UID 1000)
- Minimal attack surface with essential packages only
- No write access to application directory
- Secure default configurations
- Regular base image updates recommended

## Performance Characteristics

- **Memory footprint**: ~50-100MB runtime
- **Startup time**: ~2-5 seconds
- **Health check latency**: <50ms typical
- **Shutdown time**: 30 seconds maximum (configurable)

## Version History

| Version | Changes                                                            |
| ------- | ------------------------------------------------------------------ |
| 1.0.0   | Initial implementation with health endpoints and graceful shutdown |
