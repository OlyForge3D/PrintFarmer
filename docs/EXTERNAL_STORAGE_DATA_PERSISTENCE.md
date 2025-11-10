# External Storage & Data Persistence (P0 Requirement)

## Overview

PrintFarmer requires **guaranteed persistence** for critical user data:
- **3D Model Uploads** - STL, 3MF, OBJ, PLY files uploaded by users
- **Generated G-code** - Sliced G-code output files ready for printing  
- **Slicer Profiles** - User-configured print profiles (materials, speeds, temperatures, etc.)

This data **must survive container recreation** and **should only be deleted when explicitly removing the database**.

## Architecture

### Problem with Docker-Managed Volumes

By default, Docker Compose creates **named volumes** that are managed by Docker and stored in Docker's data directory (typically `/var/lib/docker/volumes/`):

```yaml
volumes:
  printfarmer-model-uploads:
  printfarmer-gcode-storage:
  printfarmer-slicer-profiles:
```

**Issues with named volumes:**
- ❌ Difficult to access from the host filesystem
- ❌ Lost if volumes are pruned (`docker volume prune`)
- ❌ Not easily backed up without special tooling
- ❌ Can't be easily shared with other applications
- ❌ Location depends on Docker data directory configuration

### Solution: External Host Bind Mounts

The deploy-docker script now supports **binding Docker container paths to external host directories**:

```yaml
volumes:
  - /var/lib/printfarmer/models:/app/uploads
  - /var/lib/printfarmer/gcode:/app/gcode
  - /var/lib/printfarmer/slicer-profiles:/app/profiles
```

**Benefits:**
- ✅ Direct access from host filesystem
- ✅ Easy backup/restore
- ✅ Data persists across container recreation, rebuild, and updates
- ✅ Data lifecycle tied to explicit filesystem cleanup, not Docker cleanup operations
- ✅ Can use network mounts (NFS, SMB) for remote storage

## Configuration

### Interactive Deployment

During interactive deployment, you'll be prompted:

```
💾 External Storage Configuration (P0 Data Persistence)

Model Uploads & G-Code Files Storage
These critical data files should persist independently from container lifecycles.

Use external host directories for model uploads and G-code? (Recommended: YES for production) [Y/n]: y

External storage enabled - data will persist on host filesystem

Host directory for 3D model uploads: [/var/lib/printfarmer/models]: /srv/printfarmer/models
Creating models directory: /srv/printfarmer/models
  Directory created successfully

Host directory for generated G-code: [/var/lib/printfarmer/gcode]: /srv/printfarmer/gcode
Creating G-code directory: /srv/printfarmer/gcode
  Directory created successfully

Host directory for slicer profiles (optional): [/var/lib/printfarmer/slicer-profiles]: /srv/printfarmer/profiles
Creating slicer profiles directory: /srv/printfarmer/slicer-profiles
  Directory created successfully
```

Configuration is **automatically saved** to `.deploy-config` for future deployments.

### Non-Interactive Deployment

Set environment variables before running:

```bash
export USE_EXTERNAL_STORAGE=yes
export EXTERNAL_MODELS_PATH=/var/lib/printfarmer/models
export EXTERNAL_GCODE_PATH=/var/lib/printfarmer/gcode
export EXTERNAL_PROFILES_PATH=/var/lib/printfarmer/slicer-profiles
./scripts/deploy-docker.sh --non-interactive
```

Or in a deployment script:

```bash
#!/bin/bash
./scripts/deploy-docker.sh \
  --non-interactive \
  --architecture microservices \
  --include-monitoring
```

The script will use environment variables or `.deploy-config` saved from previous runs.

### Environment File (.env)

The generated `.env` file includes:

```bash
# External Storage Configuration (P0 - Critical Data Persistence)
USE_EXTERNAL_STORAGE=yes
EXTERNAL_MODELS_PATH=/var/lib/printfarmer/models
EXTERNAL_GCODE_PATH=/var/lib/printfarmer/gcode
EXTERNAL_PROFILES_PATH=/var/lib/printfarmer/slicer-profiles
```

These are passed to Docker Compose via `.env` file substitution.

## Data Persistence Guarantees

✅ **Data survives:**
- Container recreation: `docker-compose down && docker-compose up`
- Image rebuild: New builds pull fresh images
- Version updates: Deploy new API/frontend images
- Bug fixes and patches: Any Docker-level restart
- System reboot: Mounted host directories persist

✅ **Data deleted only when:**
- Explicitly removing the host directory: `rm -rf /var/lib/printfarmer/models`
- Manual backup cleanup by administrator
- Explicit database wipe (separate operation)

❌ **NOT deleted when:**
- Database file(s) deleted or reset
- Docker system cleanup: `docker system prune`
- Volume cleanup: `docker volume prune`
- Container restart or recreate
- Image rebuild or update

## Directory Permissions & Ownership

The deploy script creates directories with recommended permissions:

```bash
sudo mkdir -p /var/lib/printfarmer/{models,gcode,slicer-profiles}
sudo chmod 755 /var/lib/printfarmer
```

**For Docker container access:**

If running Docker daemon without root privileges, ensure the directories are writable by the Docker user:

```bash
# Option 1: Use Docker group
sudo chown -R root:docker /var/lib/printfarmer
sudo chmod 755 /var/lib/printfarmer
sudo chmod 775 /var/lib/printfarmer/{models,gcode,slicer-profiles}

# Option 2: Open world-writable (less secure)
sudo chmod 777 /var/lib/printfarmer/{models,gcode,slicer-profiles}
```

### SELinux & AppArmor

On systems with SELinux/AppArmor enabled, you may need to add mount contexts:

**SELinux:**
```bash
sudo semanage fcontext -a -t container_file_t "/var/lib/printfarmer(/.*)?"
sudo restorecon -R /var/lib/printfarmer
```

**AppArmor:** Edit `/etc/apparmor.d/docker-default` to allow mount access.

## Network Storage (NFS/SMB)

For high-availability deployments, mount external storage network shares:

```bash
# Mount NFS share
sudo mount -t nfs 192.168.1.100:/exports/printfarmer /var/lib/printfarmer

# Or SMB share
sudo mount -t cifs //nas.example.com/printfarmer /var/lib/printfarmer \
  -o username=user,password=pass,uid=1000,gid=1000
```

Then set external storage paths to the mounted location:

```bash
export EXTERNAL_MODELS_PATH=/var/lib/printfarmer/models
export EXTERNAL_GCODE_PATH=/var/lib/printfarmer/gcode
```

## Backup & Restore

### Backup to External Drive

```bash
# Simple tarball backup
tar -czf /mnt/backup/printfarmer-data-$(date +%Y%m%d).tar.gz \
  /var/lib/printfarmer/

# Or per-component
cp -r /var/lib/printfarmer/models /mnt/backup/models-$(date +%Y%m%d)
```

### Restore from Backup

```bash
# Restore entire dataset
tar -xzf /mnt/backup/printfarmer-data-20250110.tar.gz -C /

# Or selective restore
rsync -av /mnt/backup/models-20250110/ /var/lib/printfarmer/models/
```

### Docker Volume Backup (for comparison)

If using Docker-managed volumes, backup would require:

```bash
# Export volume
docker run --rm -v printfarmer-model-uploads:/data \
  -v /mnt/backup:/backup \
  alpine tar czf /backup/models-volume.tar.gz /data

# Restore
docker volume create printfarmer-model-uploads
docker run --rm -v printfarmer-model-uploads:/data \
  -v /mnt/backup:/backup \
  alpine tar xzf /backup/models-volume.tar.gz -C /
```

## Migration from Docker Volumes to External Storage

If you have existing data in Docker-managed volumes and want to migrate:

```bash
# 1. Export existing Docker volume
docker run --rm -v printfarmer-model-uploads:/data \
  -v /tmp:/backup \
  alpine tar czf /backup/models-export.tar.gz /data

# 2. Create external storage directories
mkdir -p /var/lib/printfarmer/{models,gcode,slicer-profiles}

# 3. Extract into external storage
cd /var/lib/printfarmer/models
tar xzf /tmp/models-export.tar.gz --strip-components=1

# 4. Update configuration (will be asked on next deployment)
export USE_EXTERNAL_STORAGE=yes
export EXTERNAL_MODELS_PATH=/var/lib/printfarmer/models

# 5. Redeploy
./scripts/deploy-docker.sh --redeploy

# 6. Verify data is accessible
ls -la /var/lib/printfarmer/models/
```

## Troubleshooting

### Data Not Appearing After Deployment

1. **Check directory mounting:**
   ```bash
   docker exec printfarmer-api ls -la /app/uploads/
   ```

2. **Verify external directory has data:**
   ```bash
   ls -la /var/lib/printfarmer/models/
   ```

3. **Check environment variables in container:**
   ```bash
   docker exec printfarmer-api env | grep EXTERNAL
   ```

4. **Verify permissions:**
   ```bash
   ls -la /var/lib/printfarmer/
   ```

### "Permission denied" when saving models

```bash
# Fix permissions
sudo chmod 755 /var/lib/printfarmer
sudo chmod 755 /var/lib/printfarmer/{models,gcode,slicer-profiles}

# If Docker daemon runs as different user:
sudo chown $(docker info --format '{{.SecurityOptions}}' | grep -o 'userns' || echo 'root') \
  /var/lib/printfarmer/{models,gcode,slicer-profiles}
```

### Disk space issues

Monitor external storage usage:

```bash
du -sh /var/lib/printfarmer/*
df -h /var/lib/printfarmer/

# Archive and clean old G-code
find /var/lib/printfarmer/gcode -mtime +30 -exec tar -czf /archive/{}.tar.gz {} \;
find /var/lib/printfarmer/gcode -mtime +30 -delete
```

## Testing

To verify external storage is working correctly:

```bash
# 1. Deploy with external storage
./scripts/deploy-docker.sh --non-interactive

# 2. Upload a test model via UI
# (Upload a small STL file through the web interface)

# 3. Verify file appears on host
ls -la /var/lib/printfarmer/models/

# 4. Recreate containers (this preserves external data)
docker-compose down
docker-compose up -d

# 5. Verify data still accessible through UI
# (Check that uploaded model is still there)

# 6. Test file persistence
touch /var/lib/printfarmer/models/test.txt
docker-compose down && docker-compose up -d
ls /var/lib/printfarmer/models/test.txt  # Should still exist
```

## See Also

- [DEPLOYMENT_CONFIG_PERSISTENCE.md](./DEPLOYMENT_CONFIG_PERSISTENCE.md) - Config file management
- [DOCKER_DEPLOYMENT.md](../DOCKER_DEPLOYMENT.md) - General Docker deployment guide
- [DEPLOYMENT_TESTING_CHECKLIST.md](./DEPLOYMENT_TESTING_CHECKLIST.md) - Deployment validation
