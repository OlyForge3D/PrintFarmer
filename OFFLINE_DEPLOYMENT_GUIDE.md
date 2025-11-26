# PrintFarmer Offline Deployment Guide

**Last Updated**: November 22, 2025  
**Status**: ✅ COMPLETE - All 6 base images covered, simplified workflow implemented

## Quick Start (TL;DR)

**Single command to prepare everything offline:**
```powershell
# Machine WITH internet:
.\scripts\deploy-docker.ps1 -PrepareOffline

# Transfer ./docker-images to offline machine, then:

# Machine WITHOUT internet (images already cached - no flag needed):
.\scripts\deploy-docker.ps1
# Script auto-detects and loads cached images automatically
```

**Or if you want explicit offline mode confirmation:**
```powershell
.\scripts\deploy-docker.ps1 -DeployOffline
```

---

## Overview

The PrintFarmer Docker deployment script now includes **automatic image caching** and **simplified one-command offline preparation**. Downloaded images are automatically remembered and reused for future deployments without requiring additional arguments or internet access.

## Smart Image Caching Features

### ✅ Automatic Caching
- Images are automatically cached on first deployment
- Cache location: `~/.printfarmer/images-cache.json`
- Cached images are reused on subsequent deployments
- No manual `-LoadImages` flag needed

### ✅ Offline Support
- Download and save images on internet-connected machine
- Transfer saved images to offline machine
- Deploy on offline machine without internet
- Auto-detects and loads cached images automatically

### ✅ Intelligent Detection
- Checks if images already exist in Docker before loading
- Skips loading images that are already present
- No redundant downloads or loads
- Reports status of each cached image

## Quick Start Workflows

### Scenario 1: Standard Deployment (First Time)

```powershell
# Run on any machine with internet and Docker
pwsh .\scripts\deploy-docker.ps1

# Follow interactive prompts to configure deployment
# Images are automatically downloaded and cached
```

**What happens:**
1. Script checks for cached images locally
2. If images don't exist in Docker, downloads them
3. Automatically saves metadata to `~/.printfarmer/images-cache.json`
4. Deploys using the downloaded images
5. Images remain cached for future use

### Scenario 2: Redeploy with Cached Images (Subsequent Runs)

```powershell
# On the same machine (or any machine with the cached images folder), just run:
pwsh .\scripts\deploy-docker.ps1

# The script automatically:
# - Detects cached images in ./docker-images
# - Loads them if missing from Docker
# - No -DeployOffline flag needed
```

**What happens:**
1. Script checks for TAR files in `./docker-images`
2. Checks if images exist in Docker
3. Loads from TAR files if any are missing
4. Automatically saves cache metadata
5. Proceeds with deployment configuration

**Note**: `-DeployOffline` is optional - only use it if you want explicit confirmation that offline mode is active.

### Scenario 3: Complete Offline Deployment Setup

**Step 1: On Internet-Connected Machine**

```powershell
# Download all images and save them
pwsh .\scripts\deploy-docker.ps1 -PullImages -SaveImages -ImagesDir C:\offline-images

# Download OrcaSlicer AppImage (optional, for distributed slicing)
pwsh .\scripts\deploy-docker.ps1 -CacheOrcaSlicer -ImagesDir C:\offline-images

# Images are now saved in C:\offline-images
# Metadata saved to ~/.printfarmer/images-cache.json
```

**Step 2: Transfer Images**

```powershell
# Copy the images folder to offline machine via USB/network
Copy-Item C:\offline-images -Destination D:\offline-images -Recurse
```

**Step 3: On Offline Machine**

```powershell
# Deploy using cached images - no internet required!
pwsh .\scripts\deploy-docker.ps1 -ImagesDir D:\offline-images

# Images are automatically loaded from cache
# No -LoadImages flag needed
```

**What happens:**
1. Script checks for TAR files in `D:\offline-images`
2. Detects missing images in Docker
3. Automatically loads them from TAR files
4. Saves cache metadata for future use
5. Proceeds with deployment

## Cache Management

### Default Cache Location

Cache metadata is stored at:
```
~/.printfarmer/images-cache.json
```

Example content:
```json
{
  "timestamp": "2025-11-22T10:45:30Z",
  "imagesDir": "C:\\offline-images",
  "baseImages": [
    "mcr.microsoft.com/dotnet/sdk:9.0",
    "mcr.microsoft.com/dotnet/aspnet:9.0",
    "node:18-alpine",
    "postgres:16-alpine",
    "nginx:alpine"
  ]
}
```

### Manual Image Management (Optional)

If you prefer to manually manage images:

```powershell
# Manually load images from TAR files
pwsh .\scripts\deploy-docker.ps1 -LoadImages -ImagesDir C:\offline-images

# Then proceed with deployment
pwsh .\scripts\deploy-docker.ps1
```

## Distributed Slicing with OrcaSlicer

### Offline OrcaSlicer Support

The script automatically caches OrcaSlicer AppImage for offline deployments:

```powershell
# Step 1: Cache OrcaSlicer on internet machine
pwsh .\scripts\deploy-docker.ps1 -CacheOrcaSlicer -ImagesDir C:\offline-images

# Step 2: Transfer images folder to offline machine
# Step 3: Deploy with automatic OrcaSlicer loading
pwsh .\scripts\deploy-docker.ps1 -ImagesDir C:\offline-images
```

## Best Practices

### ✅ DO
- Use `-PullImages -SaveImages` to prepare for offline deployment
- Keep `~/.printfarmer/` directory backed up
- Use the same `ImagesDir` across related deployments
- Let the script auto-load cached images

### ❌ DON'T
- Delete `~/.printfarmer/images-cache.json` unless resetting cache
- Move images folder without updating the cache metadata
- Run `-LoadImages` manually unless you have a specific reason
- Store large TAR files in temporary directories

## Troubleshooting

### "No cached images found" on Offline Machine

**Problem:** Script says no cached images found when deploying offline.

**Solution:**
1. Verify images folder exists: `Test-Path D:\offline-images`
2. Check for TAR files: `Get-ChildItem D:\offline-images -Filter *.tar`
3. Run with explicit path: `pwsh .\scripts\deploy-docker.ps1 -ImagesDir D:\offline-images`

### "Failed to load X from cache"

**Problem:** Script fails to load specific image from TAR file.

**Solution:**
1. Verify TAR file integrity: `docker load -i D:\offline-images\image.tar`
2. Re-download images on internet machine
3. Transfer images again to offline machine

### Images already exist but script still tries to load

**Problem:** Script tries to load images even though they're already in Docker.

**Solution:**
1. This is normal - script checks and skips if images are present
2. Check image status: `docker image ls | grep printfarmer`
3. Script will report "All required images are already in Docker"

## Command Reference

### Core Deployment
```powershell
# Interactive deployment
pwsh .\scripts\deploy-docker.ps1

# With specific image directory
pwsh .\scripts\deploy-docker.ps1 -ImagesDir ./docker-images
```

### Image Management
```powershell
# Download images
pwsh .\scripts\deploy-docker.ps1 -PullImages

# Save to TAR files
pwsh .\scripts\deploy-docker.ps1 -PullImages -SaveImages -ImagesDir C:\images

# Manually load images
pwsh .\scripts\deploy-docker.ps1 -LoadImages -ImagesDir C:\images
```

### OrcaSlicer Caching
```powershell
# Cache OrcaSlicer AppImage
pwsh .\scripts\deploy-docker.ps1 -CacheOrcaSlicer -ImagesDir C:\images

# Show cached OrcaSlicer info
pwsh .\scripts\deploy-docker.ps1 -LoadCachedOrcaSlicer
```

### Deployment Control
```powershell
# Validate without deploying
pwsh .\scripts\deploy-docker.ps1 -DryRun

# Non-interactive CI/CD mode
pwsh .\scripts\deploy-docker.ps1 -NonInteractive

# Restart existing deployment
pwsh .\scripts\deploy-docker.ps1 -Redeploy

# Tear down deployment
pwsh .\scripts\deploy-docker.ps1 -TearDown
```

## Advanced Scenarios

### When to Use -DeployOffline

`-DeployOffline` is optional and useful only in specific scenarios:

```powershell
# Use -DeployOffline when you want explicit confirmation that:
# 1. Offline mode is active
# 2. All images were loaded before interactive prompts
# 3. You're intentionally in offline-first workflow

.\scripts\deploy-docker.ps1 -DeployOffline
```

**When NOT needed:**
```powershell
# Just deploy normally - images auto-load if cached
.\scripts\deploy-docker.ps1

# Specify custom images directory (auto-loads from there)
.\scripts\deploy-docker.ps1 -ImagesDir D:\my-cached-images
```

### Using Docker Registry for Image Caching

Instead of TAR files, you can use a local Docker registry:

```powershell
# (This requires a local registry setup - beyond scope of this guide)
# See DOCKER_REGISTRY.md for detailed setup
```

### CI/CD Pipeline Integration

```powershell
# In CI/CD pipeline, cache images once
if ($env:CI_BUILD_NUM -eq 1) {
    pwsh .\scripts\deploy-docker.ps1 -PullImages -SaveImages
}

# Then use cached images for all deployments
pwsh .\scripts\deploy-docker.ps1 -NonInteractive
```

### Updating Cached Images

To force re-download and update cached images:

```powershell
# Delete cache metadata
Remove-Item ~/.printfarmer/images-cache.json

# Delete cached TAR files
Remove-Item ./docker-images -Recurse

# Re-download and cache
pwsh .\scripts\deploy-docker.ps1 -PullImages -SaveImages
```

## Related Documentation

- **README.md** - General project information
- **DEPLOYMENT_OVERVIEW.md** - Deployment architecture overview
- **docs/DEPLOYMENT_READINESS_CHECK.md** - Pre-deployment checklist
