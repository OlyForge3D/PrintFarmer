# Storage Path Configuration for Multi-Deployment Architecture

## Overview

PrintFarmer's storage path management supports both **Docker** and **Kubernetes** deployments with a unified, environment-agnostic configuration system. This ensures files are stored on external volumes in production deployments, preventing data loss on container restarts and enabling seamless load balancing across multiple instances.

## Architecture

### Storage Path Service

The `IStoragePathService` (located in `Farm.Web.Api.Services.StorageManagement`) provides centralized configuration for all file storage paths:

- **Gcode Files**: Harvested gcode files and extracted thumbnails
- **Model Uploads**: User-uploaded 3D model files  
- **Slicer Profiles**: Slicing configuration profiles

### Configuration Priority

Each storage path follows this priority order:

1. **Environment Variables** (highest priority - for Docker/K8s)
   - `GCODE_STORAGE_PATH`
   - `MODEL_UPLOAD_PATH`
   - `SLICER_PROFILES_PATH`

2. **Configuration Section** (from appsettings.json or config)
   - `STORAGE_PATHS:GCODE`
   - `STORAGE_PATHS:UPLOADS`
   - `STORAGE_PATHS:PROFILES`

3. **Default Paths** (local development only)
   - `{ContentRootPath}/gcode-library`
   - `{ContentRootPath}/uploads`
   - `{ContentRootPath}/profiles`

## Deployment Scenarios

### Local Development

**Path Resolution:**
```
Uses default paths relative to ContentRootPath:
- /src/api/gcode-library
- /src/api/uploads
- /src/api/profiles
```

**Configuration:** No environment variables needed; uses defaults.

### Docker Deployment (Single Container)

**Path Resolution:**
```
Environment variables from docker-compose.yml:
- /app/gcode (mounted volume: printfarmer-gcode-storage)
- /app/uploads (mounted volume: printfarmer-model-uploads)
- /app/profiles (mounted volume: printfarmer-slicer-profiles)
```

**docker-compose.yml Configuration:**
```yaml
api:
  environment:
    - GCODE_STORAGE_PATH=/app/gcode
    - MODEL_UPLOAD_PATH=/app/uploads
    - SLICER_PROFILES_PATH=/app/profiles
  volumes:
    - printfarmer-gcode-storage:/app/gcode
    - printfarmer-model-uploads:/app/uploads
    - printfarmer-slicer-profiles:/app/profiles
```

**Data Persistence:**
- All volumes are Docker-managed named volumes
- Survives container restarts and updates
- Accessible via `docker volume ls` and `docker volume inspect <name>`

### Docker Deployment (Multiple Load-Balanced Servers)

**Path Resolution:** Same as single container above.

**Key Advantages:**
- All servers write to same shared volume
- Load balancer distributes requests without routing complexity
- Files are always accessible regardless of which server processes the request
- No file sync needed between servers

**Architecture:**
```
┌─────────────────────────────────────┐
│      Nginx Load Balancer            │
└────────────┬────────────────────────┘
             │
    ┌────────┴──────────┐
    │                   │
┌───▼───┐           ┌───▼───┐
│ API 1 │           │ API 2 │
└───┬───┘           └───┬───┘
    │                   │
    └─────────┬─────────┘
              │
    ┌─────────▼──────────┐
    │  Docker Volume     │
    │ (printfarmer-      │
    │  gcode-storage)    │
    └────────────────────┘
```

**Example Load Balancer Config (nginx.conf):**
```nginx
upstream api {
    server api-server-1:5245;
    server api-server-2:5245;
    server api-server-3:5245;
}

server {
    listen 80;
    location /api {
        proxy_pass http://api;
    }
}
```

### Kubernetes Deployment

**Path Resolution:**
```
Environment variables from Kubernetes Deployment manifest:
- /data/gcode (mounted PVC: printfarmer-gcode-pvc)
- /data/uploads (mounted PVC: printfarmer-uploads-pvc)
- /data/profiles (mounted PVC: printfarmer-profiles-pvc)
```

**Kubernetes Deployment Manifest:**
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: printfarmer-api
spec:
  replicas: 3
  template:
    spec:
      containers:
      - name: api
        image: printfarmer-api:latest
        env:
        - name: GCODE_STORAGE_PATH
          value: /data/gcode
        - name: MODEL_UPLOAD_PATH
          value: /data/uploads
        - name: SLICER_PROFILES_PATH
          value: /data/profiles
        volumeMounts:
        - name: gcode-storage
          mountPath: /data/gcode
        - name: model-uploads
          mountPath: /data/uploads
        - name: slicer-profiles
          mountPath: /data/profiles
      volumes:
      - name: gcode-storage
        persistentVolumeClaim:
          claimName: printfarmer-gcode-pvc
      - name: model-uploads
        persistentVolumeClaim:
          claimName: printfarmer-uploads-pvc
      - name: slicer-profiles
        persistentVolumeClaim:
          claimName: printfarmer-profiles-pvc
```

**Create Persistent Volumes:**
```bash
# Create storage classes if not using default
kubectl apply -f - <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: printfarmer-gcode-pvc
spec:
  accessModes:
    - ReadWriteMany  # Required for multiple pods
  resources:
    requests:
      storage: 100Gi
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: printfarmer-uploads-pvc
spec:
  accessModes:
    - ReadWriteMany
  resources:
    requests:
      storage: 50Gi
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: printfarmer-profiles-pvc
spec:
  accessModes:
    - ReadWriteMany
  resources:
    requests:
      storage: 10Gi
EOF
```

**Key Requirements:**
- Use `ReadWriteMany` access mode for shared storage
- Use NFS, GlusterFS, or managed file storage (e.g., AWS EFS, Azure Files)
- Service mesh handles load balancing (Kubernetes built-in)

## Service Integration

### GcodeHarvestService

The harvest service uses `IStoragePathService` to determine storage directories:

```csharp
public partial class GcodeHarvestService(
    // ... other dependencies ...
    IStoragePathService storagePathService) : IGcodeHarvestService
{
    public async Task<GcodeHarvestResultDto> ImportSelectedFilesAsync(...)
    {
        // Uses centralized storage service
        string storageDir = _storagePathService.GetGcodeStorageDirectory();
        string thumbnailDir = _storagePathService.GetThumbnailDirectory();
        
        // Files saved to external volume in production
        // Files saved to local development paths in dev
    }
}
```

### Initialization

The storage service can be initialized during application startup:

```csharp
// In Program.cs or startup code
using (var scope = app.Services.CreateScope())
{
    var storageService = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
    await storageService.EnsureDirectoriesExistAsync();
    _logger.LogInformation("Storage directories initialized");
}
```

## Migration from Old System

The old system used hardcoded paths:
```csharp
// OLD - Not recommended
string storageDir = Path.Combine(environment.ContentRootPath, "wwwroot", "gcode-library");
```

To migrate existing code:

1. **Inject IStoragePathService** into the service
2. **Replace hardcoded paths** with service calls:
   ```csharp
   // NEW - Deployment-agnostic
   string storageDir = _storagePathService.GetGcodeStorageDirectory();
   ```

## Troubleshooting

### Files Not Found After Container Restart

**Problem:** Files stored in local container filesystem, not on external volume.

**Solution:** Ensure environment variables are set:
```bash
# Check running container
docker exec <container-id> env | grep GCODE_STORAGE_PATH

# Should output: GCODE_STORAGE_PATH=/app/gcode
```

### Load Balancer Returns 404 for Files

**Problem:** Different containers have different storage paths or no shared volume.

**Solution:**
1. Verify all containers mount the same volume
2. Verify environment variables are identical on all containers
3. Check file permissions: `docker exec <container> ls -la /app/gcode`

### Kubernetes Pod Data Loss

**Problem:** PVC not properly configured or lost on pod restart.

**Solution:**
1. Verify PVC is created: `kubectl get pvc`
2. Verify pods are mounting PVC: `kubectl describe pod <pod-name>`
3. Check PVC status: `kubectl describe pvc <pvc-name>`

### Logging Storage Path Resolution

Enable detailed logging to see which paths are being used:

**appsettings.json:**
```json
{
  "Logging": {
    "LogLevel": {
      "Farm.Web.Api.Services.StorageManagement": "Debug"
    }
  }
}
```

**Output Example:**
```
info: Farm.Web.Api.Services.StorageManagement.StoragePathService[0]
      Using GCODE_STORAGE_PATH from environment: /app/gcode
```

## Best Practices

1. **Always Set Environment Variables in Production**
   - Docker: Set in docker-compose.yml
   - Kubernetes: Set in Deployment manifest
   - Don't rely on default paths in production

2. **Use Consistent Mount Points**
   - Docker: `/app/gcode`, `/app/uploads`, `/app/profiles`
   - Kubernetes: `/data/gcode`, `/data/uploads`, `/data/profiles`
   - Don't mix conventions

3. **Enable Persistent Storage**
   - Docker: Use named volumes (already configured)
   - Kubernetes: Use PersistentVolumeClaims
   - Never use ephemeral storage for file uploads

4. **Monitor Storage Capacity**
   - Set appropriate volume sizes
   - Monitor disk usage in production
   - Plan for growth

5. **Backup Configuration**
   - Back up database AND file storage
   - Test restore procedures regularly
   - Use external storage with built-in backups (AWS EFS, etc.)

## Related Documentation

- See `src/api/Services/StorageManagement/IStoragePathService.cs` for implementation
- See `docker-compose.yml` for Docker volume configuration
- See `Dockerfile.multistage` for container environment setup
