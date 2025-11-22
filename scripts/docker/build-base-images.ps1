# Script to build and cache pre-upgraded base images for offline deployments
# Run this ONCE online to create cached tar files for offline use
# Usage: .\scripts\docker\build-base-images.ps1 -CacheDir C:\docker-images

param(
    [string]$CacheDir = ".\.docker-cache",
    [switch]$Force
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "PrintFarmer Base Image Pre-Cache Builder" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This script builds and caches pre-upgraded base images."
Write-Host "Cache directory: $CacheDir"
Write-Host ""

# Create cache directory
if (-not (Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    Write-Host "✓ Created cache directory: $CacheDir"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DockerDir = Join-Path $ScriptDir "dockerfiles"

# Array of base images to build and cache
$images = @(
    @{
        base = "ubuntu:24.04"
        dockerfile = "Dockerfile.base-ubuntu"
        tag = "ubuntu:24.04-upgraded"
        tarName = "ubuntu-24.04-upgraded.tar"
    },
    @{
        base = "node:22-alpine"
        dockerfile = "Dockerfile.base-node"
        tag = "node:22-alpine-upgraded"
        tarName = "node-22-alpine-upgraded.tar"
    },
    @{
        base = "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim"
        dockerfile = "Dockerfile.base-aspnet"
        tag = "mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim-upgraded"
        tarName = "aspnet-9.0-bookworm-slim-upgraded.tar"
    },
    @{
        base = "mcr.microsoft.com/dotnet/sdk:9.0"
        dockerfile = "Dockerfile.base-sdk"
        tag = "mcr.microsoft.com/dotnet/sdk:9.0-upgraded"
        tarName = "dotnet-sdk-9.0-upgraded.tar"
    },
    @{
        base = "postgres:16-alpine"
        dockerfile = "Dockerfile.base-postgres"
        tag = "postgres:16-alpine-upgraded"
        tarName = "postgres-16-alpine-upgraded.tar"
    },
    @{
        base = "nginx:alpine"
        dockerfile = "Dockerfile.base-nginx"
        tag = "nginx:alpine-upgraded"
        tarName = "nginx-alpine-upgraded.tar"
    }
)

$successCount = 0
$failCount = 0

foreach ($image in $images) {
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "Building: $($image.tag)" -ForegroundColor Yellow
    Write-Host "  Base: $($image.base)"
    Write-Host "  Dockerfile: $($image.dockerfile)"
    Write-Host "==========================================" -ForegroundColor Cyan
    
    $dockerfilePath = Join-Path $DockerDir $image.dockerfile
    
    if (-not (Test-Path $dockerfilePath)) {
        Write-Host "✗ Dockerfile not found: $dockerfilePath" -ForegroundColor Red
        $failCount++
        continue
    }
    
    try {
        # Build the image
        Write-Host "Building Docker image..."
        docker build `
            -f $dockerfilePath `
            -t $image.tag `
            --label="printfarmer-precache=true" `
            .
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "✓ Build successful: $($image.tag)" -ForegroundColor Green
            
            # Export to tar
            $tarPath = Join-Path $CacheDir $image.tarName
            Write-Host "  Exporting to: $tarPath"
            
            docker save -o $tarPath $image.tag
            
            if ($LASTEXITCODE -eq 0) {
                $tarSize = (Get-Item $tarPath).Length / 1MB
                Write-Host "✓ Exported: $($image.tarName) ($([Math]::Round($tarSize, 2)) MB)" -ForegroundColor Green
                $successCount++
            } else {
                Write-Host "✗ Export failed: $tarPath" -ForegroundColor Red
                $failCount++
            }
        } else {
            Write-Host "✗ Build failed: $($image.tag)" -ForegroundColor Red
            $failCount++
        }
    } catch {
        Write-Host "✗ Error: $_" -ForegroundColor Red
        $failCount++
    }
    
    Write-Host ""
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Pre-Cache Build Summary" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Successful: $successCount" -ForegroundColor $(if ($successCount -gt 0) { "Green" } else { "Yellow" })
Write-Host "Failed: $failCount" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
Write-Host ""

if ((Test-Path $CacheDir) -and (Get-ChildItem -Path $CacheDir -Filter "*.tar" -ErrorAction SilentlyContinue)) {
    Write-Host "Cached images in: $CacheDir" -ForegroundColor Green
    Get-ChildItem -Path $CacheDir -Filter "*.tar" | ForEach-Object {
        $sizeMB = $_.Length / 1MB
        Write-Host "  ✓ $($_.Name) ($([Math]::Round($sizeMB, 2)) MB)"
    }
} else {
    Write-Host "No cached images found in: $CacheDir" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "To deploy offline:" -ForegroundColor Yellow
Write-Host "  1. Transfer all .tar files from $CacheDir to offline system"
Write-Host "  2. Run: docker load -i ubuntu-24.04-upgraded.tar"
Write-Host "  3. Run: docker load -i node-22-alpine-upgraded.tar"
Write-Host "  4. Run: docker load -i aspnet-9.0-bookworm-slim-upgraded.tar"
Write-Host "  5. Run: docker load -i dotnet-sdk-9.0-upgraded.tar"
Write-Host "  6. Run: docker load -i postgres-16-alpine-upgraded.tar"
Write-Host "  7. Run: docker load -i nginx-alpine-upgraded.tar"
Write-Host "  8. Then deploy: .\scripts\deploy-docker.ps1 --pull=missing"
Write-Host ""
