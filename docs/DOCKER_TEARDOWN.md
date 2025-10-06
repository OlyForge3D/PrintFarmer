# Docker Deployment Tear-Down Guide

This guide explains how to completely tear down and clean up a PrintFarmer Docker deployment.

## Quick Start

To tear down an existing deployment and start fresh:

```bash
./scripts/deploy-docker.sh --tear-down
```

This will:
1. Stop all Docker containers
2. Remove all containers
3. Remove all volumes (**⚠️ ALL DATA WILL BE DELETED!**)
4. Remove all PrintFarmer Docker images
5. Clean up Docker networks
6. Remove generated configuration files (`.env`, `docker-compose.override.yml`, etc.)
7. Optionally keep or remove `.deploy-config` (your saved preferences)

## When to Use Tear-Down

Use the `--tear-down` option when:

- **Switching database types** (e.g., PostgreSQL → SQL Server)
- **Changing network modes** (e.g., bridge → host)
- **Troubleshooting deployment issues** that require a clean slate
- **Port conflicts** with existing containers
- **Starting completely fresh** after testing

## Safety Features

### Interactive Confirmation

By default, the script will:
1. Show you exactly what will be deleted
2. Ask for confirmation before proceeding
3. Require you to type `yes` to continue

Example:
```
⚠️  WARNING: This is a destructive operation!
   All database data and uploaded files will be permanently deleted.

Are you sure you want to continue? Type 'yes' to confirm:
```

### Configuration Preservation

The script will ask if you want to keep your `.deploy-config` file:
- **Keep it**: Your preferences are remembered (architecture, database choice, ports, etc.)
- **Remove it**: Start completely fresh with no saved preferences

## Usage Examples

### Standard Tear-Down (Interactive)

```bash
./scripts/deploy-docker.sh --tear-down
```

You'll be prompted for confirmation and asked about keeping `.deploy-config`.

### Non-Interactive Tear-Down (Automation)

```bash
./scripts/deploy-docker.sh --tear-down --non-interactive
```

Automatically tears down without prompts. Keeps `.deploy-config` by default.

### Alternative Syntax

All of these work the same way:
```bash
./scripts/deploy-docker.sh --tear-down
./scripts/deploy-docker.sh --teardown
./scripts/deploy-docker.sh --clean
```

## Step-by-Step Tear-Down Process

The script performs these steps in order:

### Step 1: Stop All Containers
```
ℹ️  Step 1/6: Stopping all Docker containers...
✅ Containers stopped
```

### Step 2: Remove All Containers
```
ℹ️  Step 2/6: Removing all Docker containers...
✅ Containers removed
```

### Step 3: Remove All Volumes
```
ℹ️  Step 3/6: Removing all Docker volumes...
✅ Volumes removed
```

**⚠️ Warning:** This deletes:
- All database data (printers, models, jobs, users)
- All uploaded model files
- All generated G-code files
- All slicer profiles
- All temporary worker files

### Step 4: Remove PrintFarmer Images
```
ℹ️  Step 4/6: Removing PrintFarmer Docker images...
✅ PrintFarmer images removed
```

Removes these images (forces rebuild on next deployment):
- `printfarmer-api`
- `printfarmer-frontend`
- `printfarmer-orcaslicer-worker`
- `printfarmer-prusaslicer-worker`
- `printfarmer-slicer-base`

### Step 5: Clean Networks
```
ℹ️  Step 5/6: Cleaning up Docker networks...
✅ Networks cleaned
```

### Step 6: Remove Configuration Files
```
ℹ️  Step 6/6: Removing generated configuration files...
  • Removed docker-compose.host-network.yml
  • Removed docker-compose.override.yml
  • Removed .env
✅ Configuration files cleaned
```

## After Tear-Down

Once tear-down completes, you'll see:

```
✨ Tear-down complete!

ℹ️  You can now run './scripts/deploy-docker.sh' to start a fresh deployment.
```

### Redeploy

Simply run the deployment script again:

```bash
./scripts/deploy-docker.sh
```

If you kept `.deploy-config`, your previous choices will be used as defaults.

## Manual Tear-Down (Alternative)

If you prefer to tear down manually or the script isn't working:

```bash
# Stop and remove all containers
docker stop $(docker ps -aq) 2>/dev/null
docker rm $(docker ps -aq) 2>/dev/null

# Remove all volumes
docker volume rm $(docker volume ls -q) 2>/dev/null

# Remove PrintFarmer images
docker images | grep printfarmer | awk '{print $3}' | xargs docker rmi -f 2>/dev/null

# Clean up networks
docker network prune -f

# Remove generated files
rm -f docker-compose.host-network.yml docker-compose.override.yml .env
```

## Troubleshooting

### "Cannot remove container" errors

If containers are still running:
```bash
docker ps -a  # List all containers
docker stop <container-id>  # Stop specific container
docker rm <container-id>    # Remove specific container
```

### "Volume is in use" errors

If volumes are still mounted:
```bash
docker volume ls  # List all volumes
docker volume rm <volume-name>  # Remove specific volume
```

### "Permission denied" errors

On Linux, you may need sudo:
```bash
sudo ./scripts/deploy-docker.sh --tear-down
```

### Port still in use after tear-down

Check if any process is still using the port:
```bash
# Linux
sudo lsof -i :1433  # Replace 1433 with your port

# macOS
lsof -i :1433

# Kill process if needed
sudo kill -9 <PID>
```

## Best Practices

1. **Backup first**: If you have important data, export it before tear-down
2. **Double-check**: Make sure you're tearing down the right deployment
3. **Keep config**: Usually you want to keep `.deploy-config` for easier redeployment
4. **Document changes**: Note why you're tearing down (for future reference)

## See Also

- [Docker Deployment Guide](DOCKER_DEPLOYMENT.md)
- [Local Development Guide](../LOCAL_DEVELOPMENT.md)
- [Troubleshooting Guide](DOCKER_DEPLOYMENT.md#troubleshooting)
