# PrintFarmer Docker Deployment Script for Windows PowerShell / PowerShell Core
# Automated setup for Docker-based deployment with offline image support
# Requires PowerShell Core 7+ for optimal functionality, especially for OrcaSlicer download

#Requires -Version 7.0

param(
    [switch]$DryRun,
    [switch]$NonInteractive,
    [switch]$TearDown,
    [switch]$Help,
    [switch]$Redeploy,
    [switch]$VerifyDeployment,
    [switch]$BuildBaseImages,
    [switch]$PullImages,
    [switch]$SaveImages,
    [switch]$LoadImages,
    [switch]$CacheOrcaSlicer,
    [switch]$LoadCachedOrcaSlicer,
    [switch]$PrepareOffline,
    [switch]$DeployOffline,
    [string]$ImagesDir = "./docker-images",
    [string]$Architecture = "",
    [string]$EnvFile = "",
    [string]$ConfigFile = "",
    [string]$OutputDir = "",
    [switch]$IncludeMonitoring,
    [switch]$IncludeTelemetry,
    [switch]$IncludeSecurity,
    [switch]$IncludeRegistry,
    [switch]$IncludeDiscovery,
    [switch]$AutoAdmin,
    [string]$AutoAdminUsername = "",
    [string]$AutoAdminPassword = "",
    [string]$AutoAdminEmail = ""
)

$ErrorActionPreference = "Stop"

# ============================================================================
# ALL FUNCTION DEFINITIONS - Place before any execution code
# ============================================================================

# Helper functions for colored output
function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "[*] $Message" -ForegroundColor Cyan
}

function Write-Warning {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor Yellow
}

function Write-ErrorMsg {
    param([string]$Message)
    Write-Host "[X] $Message" -ForegroundColor Red
}

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "=============================================================" -ForegroundColor Blue
    Write-Host "  $Message" -ForegroundColor Blue
    Write-Host "=============================================================" -ForegroundColor Blue
    Write-Host ""
}

# Show help message
function Show-Help {
    Write-Host "PrintFarmer Docker Deployment Script for Windows (PowerShell Core 7+)"
    Write-Host ""
    Write-Host "USAGE:"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1 [OPTIONS]"
    Write-Host ""
    Write-Host "SMART IMAGE CACHING - Automatic offline support:"
    Write-Host "    * Downloaded images are automatically cached for offline use"
    Write-Host "    * Subsequent deployments use cached images when available (NO arguments needed)"
    Write-Host "    * Automatically searches: ./docker-images, ~/docker-images, C:\docker-images, D:\docker-images"
    Write-Host "    * Cache location: ~/.printfarmer/images-cache.json"
    Write-Host ""
    Write-Host "ORCASLICER AUTO-DISCOVERY - Automatic offline support:"
    Write-Host "    * Cached OrcaSlicer AppImage is automatically discovered and used"
    Write-Host "    * NO arguments needed - searches the same cache locations as Docker images"
    Write-Host "    * Automatically searches: ./docker-images/orcaslicer, ~/docker-images/orcaslicer, etc."
    Write-Host ""
    Write-Host "SIMPLIFIED OFFLINE DEPLOYMENT (RECOMMENDED):"
    Write-Host "    Single command prepares ALL offline materials (images + OrcaSlicer):"
    Write-Host "    "
    Write-Host "    On machine WITH internet:"
    Write-Host "        pwsh .\scripts\deploy-docker.ps1 -PrepareOffline"
    Write-Host "    "
    Write-Host "    Transfer ./docker-images folder to offline machine, then:"
    Write-Host "    "
    Write-Host "    On machine WITHOUT internet:"
    Write-Host "        pwsh .\scripts\deploy-docker.ps1 -DeployOffline"
    Write-Host ""
    Write-Host "MANUAL IMAGE MANAGEMENT OPTIONS (Advanced):"
    Write-Host "    -PrepareOffline            Comprehensive prep: downloads images, exports TAR, caches OrcaSlicer"
    Write-Host "    -DeployOffline             Load cached images and proceed with deployment"
    Write-Host "    -BuildBaseImages           Build pre-upgraded base images for offline deployment"
    Write-Host "    -PullImages                Download all base container images from registry"
    Write-Host "    -SaveImages                Export downloaded images to TAR files for offline use"
    Write-Host "    -LoadImages                Manually load saved images from TAR files"
    Write-Host "    -ImagesDir PATH            Directory for storing image TAR files (default: ./docker-images)"
    Write-Host ""
    Write-Host "ORCASLICER SUPPORT - Optional binary caching for distributed slicing:"
    Write-Host "    -CacheOrcaSlicer           Download OrcaSlicer Linux AppImage for offline use"
    Write-Host "    -LoadCachedOrcaSlicer      Show cached OrcaSlicer AppImage info"
    Write-Host ""
    Write-Host "DEPLOYMENT OPTIONS:"
    Write-Host "    -DryRun                    Validate configuration without deploying"
    Write-Host "    -NonInteractive            Automated deployment (CI/CD mode)"
    Write-Host "    -TearDown                  Stop and remove containers/volumes (preserve images)"
    Write-Host "    -Redeploy                  Restart existing deployment with same config"
    Write-Host ""
    Write-Host "REQUIREMENTS:"
    Write-Host "    PowerShell Core 7.0 or newer (pwsh.exe)"
    Write-Host "    Download from: https://github.com/PowerShell/PowerShell/releases"
    Write-Host ""
    Write-Host "EXAMPLES:"
    Write-Host "    # Basic deployment (images downloaded and cached automatically)"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1"
    Write-Host ""
    Write-Host "    # Re-deploy with cached images (NO arguments needed - finds cache automatically)"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1"
    Write-Host ""
    Write-Host "    # Build pre-upgraded base images for offline deployment"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1 -BuildBaseImages -ImagesDir C:\docker-cache"
    Write-Host ""
    Write-Host "    # Offline deployment setup on internet machine"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1 -PullImages -SaveImages"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1 -CacheOrcaSlicer"
    Write-Host ""
    Write-Host "    # Transfer ./docker-images folder to offline machine, then:"
    Write-Host "    pwsh .\scripts\deploy-docker.ps1  # Images auto-load from cache!"
    Write-Host ""
    exit 0
}

# Define base images to manage
$script:BaseImages = @(
    # .NET SDK for build stages (required for multi-stage builds during docker compose up)
    "mcr.microsoft.com/dotnet/sdk:10.0",
    # .NET API runtime (ASP.NET 10.0 for API service and slicer worker base)
    "mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim",
    # Ubuntu for OrcaSlicer binaries extraction stage
    "ubuntu:24.04",
    # Node.js for frontend (React build and runtime) - upgraded to v22 for compatibility
    "node:22-alpine",
    # PostgreSQL database (primary database for PrintFarmer)
    "postgres:16-alpine",
    # Nginx reverse proxy/load balancer (required for microservices architecture)
    "nginx:alpine"
)

# Get the default images cache directory (~/.printfarmer/images)
function Get-ImagesCacheDir {
    $printfarmerDir = Join-Path $env:USERPROFILE ".printfarmer"
    if (-not (Test-Path $printfarmerDir)) {
        New-Item -ItemType Directory -Path $printfarmerDir -Force | Out-Null
    }
    return $printfarmerDir
}

# Get the images manifest file path
function Get-ImagesCacheManifest {
    $cacheDir = Get-ImagesCacheDir
    return Join-Path $cacheDir "images-cache.json"
}

# Save image cache metadata
function Save-ImagesCacheMetadata {
    param([string]$ImagesDir = "./docker-images")
    
    $manifest = Get-ImagesCacheManifest
    $cacheEntry = @{
        timestamp = Get-Date -Format "o"
        imagesDir = (Resolve-Path $ImagesDir).Path
        baseImages = @($script:BaseImages)
    } | ConvertTo-Json
    
    Set-Content -Path $manifest -Value $cacheEntry
}

# Load image cache metadata
function Load-ImagesCacheMetadata {
    $manifest = Get-ImagesCacheManifest
    if (Test-Path $manifest) {
        return Get-Content -Path $manifest -Raw | ConvertFrom-Json
    }
    return $null
}

# Check if image exists in Docker
function Test-ImageExists {
    param([string]$ImageName)
    
    try {
        $result = docker image inspect $ImageName 2>$null
        return $result -ne $null
    } catch {
        return $false
    }
}

# Find cached images automatically in common locations
function Find-CachedImagesDir {
    # Search order for cached images (most specific to most general)
    $searchPaths = @(
        "./docker-images",           # Current directory (most common)
        ".\docker-images",           # Windows current directory
        "$PWD/docker-images",        # Explicit PWD
        "~/docker-images",           # Home directory
        "$env:USERPROFILE/docker-images",  # Windows home
        "C:\docker-images",          # Root C: drive
        "D:\docker-images",          # Root D: drive (common for external drives)
        "E:\docker-images",          # Root E: drive
        "/mnt/usb/docker-images",    # WSL USB mount
        "/media/*/docker-images"     # Linux mount points
    )
    
    foreach ($path in $searchPaths) {
        try {
            # For absolute paths with drive letters, check if drive exists first
            if ($path -match '^[A-Z]:') {
                $drive = $path.Substring(0, 2)
                if (-not (Test-Path "$drive\")) {
                    continue  # Skip if drive doesn't exist
                }
            }
            
            $resolvedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
            if (Test-Path $resolvedPath -ErrorAction SilentlyContinue) {
                $tarFiles = Get-ChildItem -Path $resolvedPath -Filter "*.tar" -ErrorAction SilentlyContinue
                if ($tarFiles.Count -gt 0) {
                    Write-Info "Found cached images at: $resolvedPath"
                    return $resolvedPath
                }
            }
        } catch {
            # Silently skip paths that cause errors (missing drives, etc.)
            continue
        }
    }
    
    return $null
}

# Auto-load cached images if they exist and are not in Docker
function Auto-Load-CachedImages {
    param([string]$ImagesDir = "")
    
    # If ImagesDir not specified, search for cached images automatically
    if ([string]::IsNullOrEmpty($ImagesDir)) {
        Write-Info "Searching for cached Docker images..."
        $ImagesDir = Find-CachedImagesDir
        if ([string]::IsNullOrEmpty($ImagesDir)) {
            Write-Info "No cached images found in common locations"
            return $false
        }
    } elseif (-not (Test-Path $ImagesDir)) {
        Write-Info "Images directory not found: $ImagesDir"
        return $false
    }
    
    # Check if there are any TAR files
    $tarFiles = Get-ChildItem -Path $ImagesDir -Filter "*.tar" -ErrorAction SilentlyContinue
    if ($tarFiles.Count -eq 0) {
        Write-Info "No cached images found in $ImagesDir"
        return $false
    }
    
    # Find images that need to be loaded
    $imagesToLoad = @()
    foreach ($image in $script:BaseImages) {
        if (-not (Test-ImageExists $image)) {
            $imagesToLoad += $image
        }
    }
    
    if ($imagesToLoad.Count -eq 0) {
        Write-Info "All required images are already in Docker"
        return $true
    }
    
    Write-Info "Found $($imagesToLoad.Count) missing images. Loading from cache ($ImagesDir)..."
    
    # Load the missing images
    $successCount = 0
    $failCount = 0
    
    foreach ($tar in $tarFiles) {
        Write-Info "Loading $($tar.Name)..."
        try {
            docker load -i $tar.FullName 2>&1 | Out-Null
            Write-Success "Loaded: $($tar.Name)"
            $successCount++
        } catch {
            Write-Warning "Failed to load $($tar.Name) : $_"
            $failCount++
        }
    }
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to load $failCount images from cache"
        return $false
    }
    
    Write-Success "All cached images loaded successfully"
    return $true
}

# Find OrcaSlicer AppImage in common cache locations
function Find-CachedOrcaSlicerDir {
    param([string[]]$SearchPaths = @())
    
    # Default search paths if not provided
    if ($SearchPaths.Count -eq 0) {
        $SearchPaths = @(
            "./docker-images/orcaslicer",
            ".\docker-images\orcaslicer",
            "$PWD/docker-images/orcaslicer",
            "~/docker-images/orcaslicer",
            "$env:USERPROFILE/docker-images/orcaslicer",
            "C:\docker-images\orcaslicer",
            "D:\docker-images\orcaslicer",
            "E:\docker-images\orcaslicer",
            "/mnt/usb/docker-images/orcaslicer",
            "/media/*/docker-images/orcaslicer"
        )
    }
    
    foreach ($path in $SearchPaths) {
        try {
            # For absolute paths with drive letters, check if drive exists first
            if ($path -match '^[A-Z]:') {
                $drive = $path.Substring(0, 2)
                if (-not (Test-Path "$drive\" -ErrorAction SilentlyContinue)) {
                    continue  # Skip if drive doesn't exist
                }
            }
            
            $expandedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
            
            # Try glob expansion for media mounts
            if ($path -like "/media/*") {
                $globPaths = @(Get-ChildItem -Path "/media/" -Directory -ErrorAction SilentlyContinue)
                foreach ($mediaDir in $globPaths) {
                    $testPath = Join-Path $mediaDir.FullName "docker-images/orcaslicer"
                    if ((Test-Path $testPath) -and (Get-ChildItem $testPath -Filter "*.AppImage" -ErrorAction SilentlyContinue).Count -gt 0) {
                        return $testPath
                    }
                }
                continue
            }
            
            if ((Test-Path $expandedPath) -and (Get-ChildItem $expandedPath -Filter "*.AppImage" -ErrorAction SilentlyContinue).Count -gt 0) {
                return $expandedPath
            }
        } catch {
            # Silently skip paths that cause errors (missing drives, etc.)
            continue
        }
    }
    
    return $null
}

# Auto-load OrcaSlicer AppImage if found in cache
function Auto-Load-OrcaSlicer {
    param([string]$OrcaDir = "")
    
    # If OrcaDir not specified, search for it
    if (-not $OrcaDir) {
        $OrcaDir = Find-CachedOrcaSlicerDir
        if (-not $OrcaDir) {
            Write-Info "OrcaSlicer AppImage not found in any cache location"
            Write-Info "Will be downloaded during first Docker build if needed"
            return $true
        }
    }
    
    if (-not (Test-Path $OrcaDir)) {
        Write-Info "OrcaSlicer cache directory not found: $OrcaDir"
        return $true
    }
    
    $appImages = @(Get-ChildItem -Path $OrcaDir -Filter "*.AppImage" -ErrorAction SilentlyContinue)
    if ($appImages.Count -eq 0) {
        Write-Info "No OrcaSlicer AppImage found in cache: $OrcaDir"
        return $true
    }
    
    # Set environment variable for Docker build context
    $resolvedPath = (Resolve-Path $OrcaDir).Path
    $env:ORCA_ASSET_PATH = $resolvedPath
    
    Write-Success "Found $($appImages.Count) cached OrcaSlicer AppImage(s)"
    foreach ($img in $appImages) {
        $size = $img.Length / 1MB
        Write-Info "  ✓ $($img.Name) ($([math]::Round($size, 1)) MB)"
    }
    
    Write-Info "OrcaSlicer cache location: $resolvedPath"
    Write-Info "Automatically configured for deployment"
    
    return $true
}

# Generate random password for database
function New-RandomPassword {
    param([int]$Length = 32)
    $chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%'
    $random = New-Object System.Random
    $password = ""
    for ($i = 0; $i -lt $Length; $i++) {
        $password += $chars[$random.Next($chars.Length)]
    }
    return $password
}

# Generate environment file for deployment
function Generate-EnvFile {
    param(
        [hashtable]$Config,
        [string]$OutputPath = ".env"
    )
    
    Write-Header "Generating Environment Configuration"
    
    $Architecture = $Config['ARCHITECTURE']
    $DbProvider = $Config['DB_PROVIDER']
    # Set deployment environment (Development uses EnsureCreated, Production uses migrations)
    # TODO: Once migrations are set up, add user choice between Development and Production
    $Environment = "Development"
    $HttpPort = 80
    $ApiPort = 5245
    
    Write-Info "Creating environment file: $OutputPath"
    
    # Generate CORS origins based on architecture and ports
    $CorsOrigins = "http://localhost:3000"
    if ($Architecture -eq "microservices") {
        $CorsOrigins += ",http://localhost:$HttpPort,http://localhost:$ApiPort"
    } else {
        $CorsOrigins += ",http://localhost:$HttpPort"
    }
    
    # Start building the env file content
    $EnvContent = @"
# PrintFarmer Docker Configuration
# Generated by deploy-docker.ps1 on $(Get-Date -Format "o")

# Architecture
DEPLOYMENT_TYPE=$Architecture

# Application Settings
ASPNETCORE_ENVIRONMENT=$Environment
ASPNETCORE_URLS=http://0.0.0.0:8080

# Database Configuration
DB_PROVIDER=$DbProvider
"@

    # Add ONLY database-specific environment variables for the selected provider
    # DO NOT add variables for providers we're not using
    switch ($DbProvider) {
        "postgresql" {
            $PostgresDb = "printfarmer"
            $PostgresUser = "printfarmer"
            $PostgresPort = 5432
            $PostgresPassword = New-RandomPassword
            
            $EnvContent += "`n# PostgreSQL Configuration"
            $EnvContent += "`nPOSTGRES_IMAGE=postgres:15-alpine"
            $EnvContent += "`nPOSTGRES_DB=$PostgresDb"
            $EnvContent += "`nPOSTGRES_USER=$PostgresUser"
            $EnvContent += "`nPOSTGRES_PASSWORD=$PostgresPassword"
            $EnvContent += "`nPOSTGRES_PORT=$PostgresPort"
            
            $ConnectionString = "Host=database;Database=$PostgresDb;Username=$PostgresUser;Password=$PostgresPassword;Port=$PostgresPort"
            Write-Info "Generated random PostgreSQL password"
        }
        "sqlserver" {
            $SqlServerPassword = New-RandomPassword
            $SqlServerPort = 1433
            
            $EnvContent += "`n# SQL Server Configuration"
            $EnvContent += "`nPOSTGRES_IMAGE=mcr.microsoft.com/mssql/server:2022-latest"
            $EnvContent += "`nACCEPT_EULA=Y"
            $EnvContent += "`nMSSQL_SA_PASSWORD=$SqlServerPassword"
            $EnvContent += "`nMSSQL_PID=Developer"
            
            $ConnectionString = "Server=database;Database=printfarmer;User Id=sa;Password=$SqlServerPassword;TrustServerCertificate=True;"
            Write-Info "Generated random SQL Server SA password"
        }
        "mysql" {
            $MysqlPassword = New-RandomPassword
            $MysqlRootPassword = New-RandomPassword
            $MysqlPort = 3306
            
            $EnvContent += "`n# MySQL Configuration"
            $EnvContent += "`nPOSTGRES_IMAGE=mysql:8.0"
            $EnvContent += "`nDATABASE_NAME=printfarmer"
            $EnvContent += "`nDATABASE_USER=printfarmer"
            $EnvContent += "`nMYSQL_PASSWORD=$MysqlPassword"
            $EnvContent += "`nMYSQL_ROOT_PASSWORD=$MysqlRootPassword"
            $EnvContent += "`nMYSQL_PORT=$MysqlPort"
            
            $ConnectionString = "Server=database;Database=printfarmer;User=printfarmer;Password=$MysqlPassword;Port=$MysqlPort"
            Write-Info "Generated random MySQL passwords"
        }
        "sqlite" {
            $EnvContent += "`n# SQLite Configuration"
            $ConnectionString = "Data Source=/data/farm.db"
            Write-Info "SQLite configured (file-based database)"
        }
        default {
            $ConnectionString = "Data Source=/data/farm.db"
        }
    }
    
    # Add connection string for API
    $EnvContent += "`nConnectionStrings__Default=$ConnectionString"
    
    # Add common application settings
    $EnvContent += @"

# Network Configuration
CORS__AllowedOrigins=$CorsOrigins
ALLOW_LOCAL_NETWORK=true
NETWORK_MODE=bridge

# Port Configuration
HTTP_PORT=$HttpPort
API_PORT=$ApiPort

# Optional Services (disabled by default)
SPOOLMAN_ENABLED=false
PFARM__Spoolman__BaseUrl=
PFARM__NetworkDiscovery__EnableDiscovery=true
PFARM__NetworkDiscovery__DiscoverySubnets=

# Deployment
DEPLOYMENT_MODE=$Architecture
ENABLE_DISTRIBUTED_SLICING=true
"@
    
    # Write to file
    try {
        Set-Content -Path $OutputPath -Value $EnvContent -Encoding UTF8
        Write-Success "Environment file generated: $OutputPath"
        return $true
    } catch {
        Write-ErrorMsg "Failed to generate environment file: $_"
        return $false
    }
}


# Pull all base images from registry
function Pull-BaseImages {
    Write-Header "Pulling Base Container Images"
    
    Write-Info "Pulling essential images for PrintFarmer core services:"
    Write-Info "  - .NET SDK 10.0 (multi-stage builds during deployment)"
    Write-Info "  - .NET ASP.NET 10.0 (API runtime)"
    Write-Info "  - Node.js 22 Alpine (React frontend)"
    Write-Info "  - PostgreSQL 16 Alpine (database)"
    Write-Info "  - Nginx Alpine (reverse proxy/load balancer for microservices)"
    Write-Info ""
    Write-Info "Download size: approximately 450-700MB"
    Write-Info "Note: Images already present locally will be checked for updates"
    Write-Host ""
    
    $successCount = 0
    $skipCount = 0
    $failCount = 0
    
    foreach ($image in $script:BaseImages) {
        # Check if image already exists locally
        $imageExists = docker images --quiet $image 2>$null
        
        if ($imageExists) {
            Write-Info "Image already present locally: $image"
            Write-Info "Checking for updates..."
        } else {
            Write-Info "Pulling $image..."
        }
        
        try {
            docker pull $image
            if ($imageExists) {
                Write-Success "Updated: $image"
            } else {
                Write-Success "Pulled: $image"
            }
            $successCount++
        } catch {
            Write-Warning "Failed to pull $image : $_"
            $failCount++
        }
    }
    
    Write-Header "Pull Summary"
    Write-Host "Successfully processed: $successCount/$($script:BaseImages.Count)" -ForegroundColor Green
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to pull: $failCount images"
        Write-Info "Check your internet connection and try again"
        return $false
    }
    
    Write-Success "All base images processed successfully!"
    return $true
}

# Save images to tar files
function Save-ImagesToTar {
    param([string]$TargetDir = "./docker-images")
    
    Write-Header "Exporting Images to TAR Files"
    
    if (-not (Test-Path $TargetDir)) {
        New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
        Write-Info "Created directory: $TargetDir"
    }
    
    $successCount = 0
    $failCount = 0
    $totalSize = 0
    
    foreach ($image in $script:BaseImages) {
        $safeName = $image -replace '[:/]', '-'
        $tarFile = Join-Path $TargetDir "$safeName.tar"
        
        Write-Info "Exporting $image to $tarFile..."
        try {
            docker save -o $tarFile $image
            
            $fileSize = (Get-Item $tarFile).Length
            $fileSizeMB = [math]::Round($fileSize / 1MB, 2)
            $totalSize += $fileSize
            
            Write-Success "Exported: $image - Size: $fileSizeMB MB"
            $successCount++
        } catch {
            Write-Warning "Failed to export $image : $_"
            $failCount++
        }
    }
    
    $totalSizeMB = [math]::Round($totalSize / 1MB, 2)
    $totalSizeGB = [math]::Round($totalSize / 1GB, 2)
    
    Write-Header "Export Summary"
    Write-Host "Successfully exported: $successCount/$($script:BaseImages.Count)" -ForegroundColor Green
    Write-Host "Total size: $totalSizeGB GB - $totalSizeMB MB" -ForegroundColor Cyan
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to export: $failCount images"
        return $false
    }
    
    Write-Success "All images exported successfully!"
    Write-Info "TAR files location: $TargetDir"
    Write-Info "You can now transfer this folder to offline machines"
    
    $manifestPath = Join-Path $TargetDir "manifest.txt"
    $script:BaseImages | Set-Content $manifestPath
    Write-Info "Created manifest file: $manifestPath"
    
    # Save cache metadata for auto-loading
    Save-ImagesCacheMetadata -ImagesDir $TargetDir
    Write-Info "Saved cache metadata for automatic offline loading"
    
    return $true
}

# Load images from tar files
function Load-ImagesFromTar {
    param([string]$SourceDir = "./docker-images")
    
    Write-Header "Loading Images from TAR Files"
    
    if (-not (Test-Path $SourceDir)) {
        Write-ErrorMsg "Images directory not found: $SourceDir"
        Write-Info "Use -PullImages -SaveImages first to download and export images"
        return $false
    }
    
    $tarFiles = Get-ChildItem -Path $SourceDir -Filter "*.tar" -ErrorAction SilentlyContinue
    
    if ($tarFiles.Count -eq 0) {
        Write-ErrorMsg "No TAR files found in $SourceDir"
        return $false
    }
    
    Write-Info "Found $($tarFiles.Count) image TAR files to load"
    Write-Host ""
    
    $successCount = 0
    $failCount = 0
    
    foreach ($tar in $tarFiles) {
        Write-Info "Loading $($tar.Name)..."
        try {
            docker load -i $tar.FullName
            Write-Success "Loaded: $($tar.Name)"
            $successCount++
        } catch {
            Write-Warning "Failed to load $($tar.Name) : $_"
            $failCount++
        }
    }
    
    Write-Header "Load Summary"
    Write-Host "Successfully loaded: $successCount/$($tarFiles.Count)" -ForegroundColor Green
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to load: $failCount images"
        return $false
    }
    
    Write-Success "All images loaded successfully!"
    Write-Info "Images are now available in local Docker daemon"
    
    return $true
}

# ============================================================================
# Offline Deployment Preparation & Deployment
# ============================================================================

# Save only upgraded images to TAR (for offline deployment)
function Save-UpgradedImagesToTar {
    param([string]$TargetDir = "./docker-images")
    
    Write-Header "Exporting Pre-Upgraded Images to TAR Files"
    
    if (-not (Test-Path $TargetDir)) {
        New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
        Write-Info "Created directory: $TargetDir"
    }
    
    # Get all Docker images and filter for -upgraded tagged ones
    $allImages = docker images --format "{{.Repository}}:{{.Tag}}" 2>$null
    $upgradedImages = $allImages | Where-Object { $_ -like "*-upgraded" }
    
    if ($upgradedImages.Count -eq 0) {
        Write-ErrorMsg "No pre-upgraded images found in Docker"
        Write-Info "Run with -BuildBaseImages first to create pre-upgraded images"
        return $false
    }
    
    $successCount = 0
    $failCount = 0
    $totalSize = 0
    
    foreach ($image in $upgradedImages) {
        $safeName = $image -replace '[:/]', '-'
        $tarFile = Join-Path $TargetDir "$safeName.tar"
        
        Write-Info "Exporting $image to $tarFile..."
        try {
            docker save -o $tarFile $image
            
            $fileSize = (Get-Item $tarFile).Length
            $fileSizeMB = [math]::Round($fileSize / 1MB, 2)
            $totalSize += $fileSize
            
            Write-Success "Exported: $image - Size: $fileSizeMB MB"
            $successCount++
        } catch {
            Write-Warning "Failed to export $image : $_"
            $failCount++
        }
    }
    
    $totalSizeMB = [math]::Round($totalSize / 1MB, 2)
    $totalSizeGB = [math]::Round($totalSize / 1GB, 2)
    
    Write-Header "Upgraded Images Export Summary"
    Write-Host "Successfully exported: $successCount pre-upgraded images" -ForegroundColor Green
    Write-Host "Total size: $totalSizeGB GB - $totalSizeMB MB" -ForegroundColor Cyan
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to export: $failCount images"
        return $false
    }
    
    Write-Success "All pre-upgraded images exported successfully!"
    Write-Info "TAR files location: $TargetDir"
    
    # Save upgraded images manifest
    $manifestPath = Join-Path $TargetDir "manifest-upgraded.txt"
    $upgradedImages | Set-Content $manifestPath
    Write-Info "Created upgraded images manifest: $manifestPath"
    
    return $true
}

# Comprehensive offline preparation - builds pre-upgraded base images, exports to TAR, and caches OrcaSlicer
function Prepare-OfflineDeployment {
    param([string]$TargetDir = "./docker-images")
    
    Write-Header "OFFLINE DEPLOYMENT PREPARATION"
    Write-Info "This process prepares all materials needed for offline deployment:"
    Write-Info "  1. Build pre-upgraded base images (450-700MB)"
    Write-Info "  2. Export upgraded images to TAR files for transport"
    Write-Info "  3. Download and cache OrcaSlicer AppImage (250-400MB)"
    Write-Info ""
    Write-Info "Total time: ~15-25 minutes (depends on internet speed and system performance)"
    Write-Host ""
    
    $startTime = Get-Date
    $succeeded = $true
    
    try {
        # Step 1: Build pre-upgraded base images
        Write-Header "STEP 1/3: Building Pre-Upgraded Base Images"
        if (-not (Test-Path "scripts/docker/build-base-images.ps1")) {
            Write-ErrorMsg "Build script not found: scripts/docker/build-base-images.ps1"
            $succeeded = $false
        } else {
            & "scripts/docker/build-base-images.ps1" -CacheDir $TargetDir
            if ($LASTEXITCODE -ne 0) {
                Write-ErrorMsg "Failed to build pre-upgraded base images"
                $succeeded = $false
            }
        }
        
        if ($succeeded) {
            Write-Host ""
            Write-Header "STEP 2/3: Exporting Pre-Upgraded Images to TAR Files"
            if (-not (Save-UpgradedImagesToTar -TargetDir $TargetDir)) {
                Write-ErrorMsg "Failed to export pre-upgraded images to TAR"
                $succeeded = $false
            }
        }
        
        if ($succeeded) {
            Write-Host ""
            Write-Header "STEP 3/3: Caching OrcaSlicer AppImage"
            if (-not (Cache-OrcaSlicer -TargetDir "$TargetDir/orcaslicer" -Version "latest")) {
                Write-ErrorMsg "Failed to cache OrcaSlicer AppImage"
                $succeeded = $false
            }
        }
    } catch {
        Write-ErrorMsg "Unexpected error during offline preparation: $_"
        $succeeded = $false
    }
    
    $elapsed = (Get-Date) - $startTime
    Write-Host ""
    Write-Header "OFFLINE PREPARATION SUMMARY"
    
    if ($succeeded) {
        Write-Success "✓ Offline deployment materials prepared successfully!"
        Write-Info "Location: $TargetDir"
        Write-Info "Contents:"
        Write-Info "  - Pre-upgraded base images (TAR files with -upgraded suffix)"
        Write-Info "  - OrcaSlicer AppImage for distributed slicing"
        Write-Info "  - Manifest files for offline loading"
        Write-Info "Total time: $([math]::Round($elapsed.TotalMinutes, 1)) minutes"
        Write-Host ""
        Write-Info "Next steps:"
        Write-Info "  1. Transfer the '$TargetDir' folder to your offline machine"
        Write-Info "  2. Run: .\scripts\deploy-docker.ps1 -ImagesDir <path-to-images-folder>"
        Write-Info "     (The script will auto-detect and load cached images)"
        Write-Host ""
        return $true
    } else {
        Write-ErrorMsg "✗ Offline preparation failed. Check errors above and retry."
        Write-Info "Total time: $([math]::Round($elapsed.TotalMinutes, 1)) minutes"
        return $false
    }
}

# Deploy using cached offline materials
function Deploy-OfflineMode {
    param([string]$SourceDir = "./docker-images")
    
    Write-Header "OFFLINE DEPLOYMENT MODE"
    Write-Info "Loading pre-cached container images and preparing deployment"
    Write-Host ""
    
    try {
        # Check if images directory exists
        if (-not (Test-Path $SourceDir)) {
            Write-ErrorMsg "Images directory not found: $SourceDir"
            Write-Info "Run with -PrepareOffline first to download and cache all materials"
            return $false
        }
        
        # Load cached images from TAR files
        Write-Header "STEP 1/2: Loading Cached Container Images"
        if (-not (Load-ImagesFromTar -SourceDir $SourceDir)) {
            Write-ErrorMsg "Failed to load cached images"
            return $false
        }
        
        # Auto-load OrcaSlicer if available
        Write-Host ""
        Write-Header "STEP 2/2: Loading OrcaSlicer AppImage (Optional)"
        $orcaDir = Join-Path $SourceDir "orcaslicer"
        if (Test-Path $orcaDir) {
            Load-CachedOrcaSlicer -SourceDir $orcaDir
        } else {
            Write-Info "OrcaSlicer cache not found - distributed slicing will be disabled"
        }
        
        Write-Host ""
        Write-Success "✓ Offline images loaded successfully!"
        Write-Info "Proceeding with deployment configuration..."
        return $true
    } catch {
        Write-ErrorMsg "Unexpected error during offline deployment setup: $_"
        return $false
    }
}

# Download OrcaSlicer Linux AppImage for offline deployments
function Cache-OrcaSlicer {
    param(
        [string]$TargetDir = "./docker-images/orcaslicer",
        [string]$Version = "2.4.0"
    )
    
    Write-Header "Caching OrcaSlicer Linux AppImage"
    
    if (-not (Test-Path $TargetDir)) {
        New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
        Write-Success "Created cache directory: $TargetDir"
    }
    
    try {
        Write-Info "Looking up OrcaSlicer v${Version} release information..."
        
        # Use .NET HttpClient directly
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AllowAutoRedirect = $true
        $handler.MaxAutomaticRedirections = 10
        
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = New-TimeSpan -Seconds 30
        $client.DefaultRequestHeaders.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36')
        
        # Get release information
        Write-Info "Fetching release info from GitHub API..."
        $releaseUrl = if ($Version -eq "latest") {
            "https://api.github.com/repos/SoftFever/OrcaSlicer/releases/latest"
        } else {
            "https://api.github.com/repos/SoftFever/OrcaSlicer/releases/tags/v${Version}"
        }
        
        $response = $client.GetAsync($releaseUrl).Result
        if (-not $response.IsSuccessStatusCode) {
            throw "Failed to get release info: HTTP $($response.StatusCode)"
        }
        
        $json = $response.Content.ReadAsStringAsync().Result | ConvertFrom-Json
        
        # Find the Linux AppImage asset (prefer x86_64 Ubuntu variant)
        $appImageAsset = $json.assets | Where-Object { 
            $_.name -match 'AppImage' -and $_.name -match 'Linux' -and $_.name -match 'x86_64|amd64'
        } | Select-Object -First 1
        
        if (-not $appImageAsset) {
            # Try any AppImage
            $appImageAsset = $json.assets | Where-Object { 
                $_.name -match 'AppImage' -and $_.name -match 'Linux'
            } | Select-Object -First 1
        }
        
        if (-not $appImageAsset) {
            throw "Could not find AppImage asset for Linux in release"
        }
        
        $downloadUrl = $appImageAsset.browser_download_url
        $fileName = $appImageAsset.name
        $appImagePath = Join-Path $TargetDir $fileName
        
        # Resolve to absolute path for reliable checking
        $resolvedAppImagePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($appImagePath)
        
        # Check if already cached - verify BOTH: path exists AND file has reasonable size
        $fileExists = Test-Path $resolvedAppImagePath -PathType Leaf
        $fileSize = if ($fileExists) { (Get-Item $resolvedAppImagePath -ErrorAction SilentlyContinue).Length } else { 0 }
        
        if ($fileExists -and $fileSize -gt 50MB) {
            $size = $fileSize / 1MB
            Write-Success "OrcaSlicer AppImage already cached: $resolvedAppImagePath ($([math]::Round($size, 1)) MB)"
            return $true
        }
        
        # If file exists but is too small or corrupted, delete it and re-download
        if ($fileExists -and $fileSize -le 50MB) {
            Write-Warning "Found corrupted/incomplete AppImage ($([math]::Round($fileSize / 1MB, 1)) MB), deleting and re-downloading..."
            Remove-Item $resolvedAppImagePath -Force -ErrorAction SilentlyContinue
        }
        
        Write-Info "Found asset: $fileName"
        Write-Info "Download URL: $downloadUrl"
        Write-Info ""
        Write-Info "Downloading OrcaSlicer v${Version} Linux AppImage..."
        Write-Info "This may take several minutes depending on internet speed"
        
        Write-Info "Starting download with .NET HTTP client..."
        $response = $client.GetAsync($downloadUrl).Result
        
        Write-Info "Response Status: $($response.StatusCode)"
        
        if (-not $response.IsSuccessStatusCode) {
            throw "HTTP $($response.StatusCode): $($response.ReasonPhrase)"
        }
        
        Write-Info "Reading response content..."
        $content = $response.Content.ReadAsByteArrayAsync().Result
        $size = $content.Length / 1MB
        
        Write-Info "Downloaded size: $([math]::Round($size, 1)) MB"
        
        # Validate file is reasonable size (AppImage typically 250-400 MB)
        if ($size -lt 50) {
            Write-ErrorMsg "Downloaded file too small ($([math]::Round($size, 1)) MB), likely invalid"
            
            # Check if it's HTML error response
            $maxLen = [Math]::Min(500, $content.Length - 1)
            $text = [System.Text.Encoding]::ASCII.GetString($content[0..$maxLen])
            if ($text -match 'html|DOCTYPE|<!') {
                Write-ErrorMsg "Response is HTML (likely GitHub error page), not binary"
            }
            
            throw "Download failed: file size is $size MB (expected 250+)"
        }
        
        # Write bytes to file
        Write-Info "Writing to disk..."
        [System.IO.File]::WriteAllBytes($appImagePath, $content)
        
        # Verify file was written
        if (-not (Test-Path $appImagePath)) {
            throw "File written but not found at $appImagePath"
        }
        
        # Verify ELF magic number for Linux binary
        try {
            $fileStream = [System.IO.File]::OpenRead($appImagePath)
            $buffer = New-Object byte[] 4
            $fileStream.Read($buffer, 0, 4) | Out-Null
            $fileStream.Close()
            
            if ($buffer[0] -eq 0x7F -and $buffer[1] -eq 0x45 -and $buffer[2] -eq 0x4C -and $buffer[3] -eq 0x46) {
                Write-Success "File verified as valid ELF binary (AppImage)"
            } else {
                Write-Warning "Could not verify ELF magic bytes, but file size looks reasonable"
            }
        }
        catch {
            Write-Warning "Could not verify ELF magic bytes: $_"
        }
        Write-Success "OrcaSlicer AppImage cached successfully"
        Write-Info "Location: $appImagePath"
        Write-Info "Size: $([math]::Round($size, 1)) MB"
        Write-Info ""
        Write-Info "OrcaSlicer AppImage will be automatically detected during deployment"
        Write-Info "No environment variables or additional arguments needed!"
        Write-Info ""
        Write-Info "Deploy with:"
        Write-Info "  pwsh .\scripts\deploy-docker.ps1"
        return $true
    }
    catch {
        Write-ErrorMsg "Failed to download OrcaSlicer: $_"
        Write-Info ""
        Write-Info "Alternative solutions:"
        Write-Info "1. Manual download from GitHub releases:"
        Write-Info "   https://github.com/SoftFever/OrcaSlicer/releases"
        Write-Info ""
        Write-Info "2. Download using browser and save to:"
        Write-Info "   $TargetDir/"
        Write-Info ""
        Write-Info "3. Set environment variable for manual file:"
        Write-Info "   `$env:ORCA_ASSET_PATH='$TargetDir'"
        Write-Info ""
        Write-Info "3. Retry with:"
        Write-Info "   .\scripts\deploy-docker.ps1 -CacheOrcaSlicer"
        return $false
    }
}

# Load cached OrcaSlicer binaries for Docker build
function Load-CachedOrcaSlicer {
    param([string]$SourceDir = "")
    
    Write-Header "Loading Cached OrcaSlicer AppImage"
    
    # If SourceDir not specified, auto-search
    if (-not $SourceDir) {
        $SourceDir = Find-CachedOrcaSlicerDir
        if (-not $SourceDir) {
            Write-Info "OrcaSlicer cache not found - will download during Docker build if needed"
            return $true
        }
    }
    
    if (-not (Test-Path $SourceDir)) {
        Write-Info "OrcaSlicer cache directory not found: $SourceDir"
        return $true
    }
    
    $appImages = @(Get-ChildItem -Path $SourceDir -Filter "*.AppImage" -ErrorAction SilentlyContinue)
    if ($appImages.Count -eq 0) {
        Write-Info "No OrcaSlicer AppImage found in $SourceDir"
        return $true
    }
    
    Write-Success "Found $($appImages.Count) OrcaSlicer AppImage file(s) in cache"
    foreach ($img in $appImages) {
        $size = $img.Length / 1MB
        Write-Info "  ✓ $($img.Name) ($([math]::Round($size, 1)) MB)"
    }
    
    # Set environment variable for Docker build to find AppImage
    $resolvedPath = (Resolve-Path $SourceDir).Path
    $env:ORCA_ASSET_PATH = $resolvedPath
    Write-Info "Cache automatically configured as: ORCA_ASSET_PATH=$resolvedPath"
    
    return $true
}


# Check prerequisites
function Check-Prerequisites {
    Write-Header "Checking Prerequisites"
    
    # Check Docker installation
    Write-Info "Checking Docker installation..."
    try {
        $DockerVersion = docker --version
        Write-Success "Docker found: $DockerVersion"
    } catch {
        Write-ErrorMsg "Docker is not installed!"
        Write-Info "Please install Docker Desktop for Windows from:"
        Write-Info "  https://docs.docker.com/desktop/install/windows-install/"
        exit 1
    }
    
    # Check Docker Compose installation
    Write-Info "Checking Docker Compose installation..."
    try {
        $ComposeVersion = docker compose version
        Write-Success "Docker Compose found: $ComposeVersion"
    } catch {
        Write-ErrorMsg "Docker Compose is not installed!"
        Write-Info "Please install Docker Desktop with Compose support"
        exit 1
    }
    
    # Check Docker daemon is running
    Write-Info "Checking if Docker daemon is running..."
    try {
        $null = docker ps 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Docker daemon is running"
        } else {
            Write-ErrorMsg "Docker daemon is not responding!"
            Write-Info "Please start Docker Desktop"
            exit 1
        }
    } catch {
        Write-ErrorMsg "Docker daemon is not running!"
        Write-Info "Please start Docker Desktop"
        exit 1
    }
    
    Write-Success "All prerequisites satisfied"
    Write-Host ""
}

# Load deployment configuration from file
function Load-DeploymentConfig {
    param([string]$ConfigPath = "./.deploy-config")
    
    Write-Info "Loading deployment configuration..."
    
    if (Test-Path $ConfigPath) {
        Write-Info "Found config file: $ConfigPath"
        $config = @{}
        Get-Content $ConfigPath | ForEach-Object {
            if ($_ -match '^([^=]+)=(.*)$') {
                $config[$matches[1]] = $matches[2]
            }
        }
        return $config
    }
    
    Write-Info "No existing configuration found"
    return @{}
}

# Save deployment configuration to file
function Save-DeploymentConfig {
    param(
        [hashtable]$Config,
        [string]$ConfigPath = "./.deploy-config"
    )
    
    Write-Info "Saving deployment configuration to $ConfigPath..."
    
    $content = @"
# PrintFarmer Docker Deployment Configuration
# Generated on $(Get-Date)
# This file stores deployment settings for future use

"@
    
    foreach ($key in $Config.Keys) {
        $content += "$key=$($Config[$key])`n"
    }
    
    Set-Content -Path $ConfigPath -Value $content
    Write-Success "Configuration saved to $ConfigPath"
}

# Choose deployment architecture
function Choose-Architecture {
    param([hashtable]$Config, [switch]$Quiet = $false)
    
    if (-not $Quiet) {
        Write-Header "Deployment Architecture Selection"
        
        Write-Host ""
        Write-Host "PrintFarmer supports two deployment architectures:" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "1. Monolithic (Recommended)" -ForegroundColor Green
        Write-Host "   - Single container with API + Web frontend" -ForegroundColor White
        Write-Host "   - Simpler configuration and networking" -ForegroundColor White
        Write-Host "   - Good for most deployments" -ForegroundColor White
        Write-Host "   - Uses SQLite database by default" -ForegroundColor White
        Write-Host ""
        Write-Host "2. Microservices (Advanced)" -ForegroundColor Green
        Write-Host "   - Separate containers for API, Web, Database" -ForegroundColor White
        Write-Host "   - Enhanced networking capabilities" -ForegroundColor White
        Write-Host "   - Better for large-scale deployments" -ForegroundColor White
        Write-Host "   - Supports PostgreSQL, SQL Server, MySQL" -ForegroundColor White
        Write-Host ""
    }
    
    $default = if ($Config['ARCHITECTURE'] -eq 'microservices') { '2' } else { '1' }
    
    if ($Quiet) {
        $choice = $default
    } else {
        $choice = Read-Host "Choose architecture [1=Monolithic, 2=Microservices] (default: $default)"
    }
    
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $choice = $default
    }
    
    switch ($choice) {
        '1' {
            Write-Success "Selected: Monolithic deployment"
            return 'monolithic'
        }
        '2' {
            Write-Success "Selected: Microservices deployment"
            return 'microservices'
        }
        default {
            Write-ErrorMsg "Invalid choice. Please select 1 or 2."
            return Choose-Architecture -Config $Config
        }
    }
}

# Choose database provider
function Choose-DatabaseProvider {
    param([hashtable]$Config, [string]$Architecture = 'monolithic', [switch]$Quiet = $false)
    
    if ($Quiet) {
        # Non-interactive mode - use default from config or architecture default
        if ($Config['DB_PROVIDER']) {
            return $Config['DB_PROVIDER']
        }
        # Default to sqlite for monolithic, postgresql for microservices
        if ($Architecture -eq 'microservices') {
            return 'postgresql'
        } else {
            return 'sqlite'
        }
    }
    
    Write-Header "Database Configuration"
    
    Write-Host ""
    Write-Host "Select your database provider:" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "1. SQLite (File-based)" -ForegroundColor Green
    Write-Host "   - Simple, no setup required" -ForegroundColor White
    Write-Host "   - Good for single-machine deployments" -ForegroundColor White
    Write-Host ""
    Write-Host "2. PostgreSQL" -ForegroundColor Green
    Write-Host "   - Advanced, high-performance" -ForegroundColor White
    Write-Host "   - Recommended for microservices" -ForegroundColor Yellow
    Write-Host "   - Requires container or external server" -ForegroundColor White
    Write-Host ""
    Write-Host "3. SQL Server" -ForegroundColor Green
    Write-Host "   - Enterprise database" -ForegroundColor White
    Write-Host "   - Requires container or external server" -ForegroundColor White
    Write-Host ""
    Write-Host "4. MySQL" -ForegroundColor Green
    Write-Host "   - Popular open-source database" -ForegroundColor White
    Write-Host "   - Requires container or external server" -ForegroundColor White
    Write-Host ""
    
    # Default to SQLite for monolithic, PostgreSQL for microservices
    $defaultChoice = if ($Architecture -eq 'microservices') { '2' } else { '1' }
    $default = if ($Config['DB_PROVIDER']) { 
        switch ($Config['DB_PROVIDER']) {
            'sqlite' { '1' }
            'postgresql' { '2' }
            'sqlserver' { '3' }
            'mysql' { '4' }
            default { $defaultChoice }
        }
    } else { $defaultChoice }
    $choice = Read-Host "Choose database provider [1-4] (default: $default)"
    
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $choice = $default
    }
    
    switch ($choice) {
        '1' { return 'sqlite' }
        '2' { return 'postgresql' }
        '3' { return 'sqlserver' }
        '4' { return 'mysql' }
        default {
            Write-ErrorMsg "Invalid choice. Please select 1-4."
            return Choose-DatabaseProvider -Config $Config
        }
    }
}

# Confirm deployment settings
function Confirm-DeploymentSettings {
    param([hashtable]$Settings)
    
    Write-Header "Deployment Summary"
    
    Write-Host ""
    Write-Host "Deployment Settings:" -ForegroundColor Cyan
    Write-Host "  Architecture:              $($Settings['ARCHITECTURE'])" -ForegroundColor White
    Write-Host "  Database:                  $($Settings['DB_PROVIDER'])" -ForegroundColor White
    
    # Distributed slicing
    if ($Settings['ENABLE_DISTRIBUTED_SLICING'] -eq 'true') {
        Write-Host "  Distributed Slicing:       Enabled" -ForegroundColor Green
        if ($Settings['ENABLE_ORCA_WORKER'] -eq 'true') {
            Write-Host "    - OrcaSlicer Workers:    Enabled" -ForegroundColor Green
            Write-Host "      * Version:             $($Settings['ORCASLICER_VERSION'])" -ForegroundColor White
            Write-Host "      * Replica Count:       $($Settings['ORCA_WORKER_COUNT'])" -ForegroundColor White
            if ($Settings['ORCA_WORKER_ENDPOINT']) {
                Write-Host "      * Endpoint:            $($Settings['ORCA_WORKER_ENDPOINT'])" -ForegroundColor White
            }
        } else {
            Write-Host "    - OrcaSlicer Workers:    Disabled" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  Distributed Slicing:       Disabled" -ForegroundColor Yellow
    }
    
    # Spoolman integration
    if ($Settings['ENABLE_SPOOLMAN'] -eq 'true') {
        Write-Host "  Spoolman Integration:      Enabled" -ForegroundColor Green
        if ($Settings['SPOOLMAN_BASE_URL']) {
            Write-Host "    - URL:                   $($Settings['SPOOLMAN_BASE_URL'])" -ForegroundColor White
        }
    } else {
        Write-Host "  Spoolman Integration:      Disabled" -ForegroundColor Yellow
    }
    
    # Monitoring & Telemetry
    if ($Settings['INCLUDE_MONITORING'] -eq 'true') {
        Write-Host "  Monitoring:                Enabled (Prometheus + Grafana)" -ForegroundColor Green
    }
    if ($Settings['INCLUDE_TELEMETRY'] -eq 'true') {
        Write-Host "  Telemetry:                 Enabled (OpenTelemetry)" -ForegroundColor Green
    }
    
    Write-Host ""
    
    if ($NonInteractive) {
        Write-Success "Non-interactive mode: Proceeding with deployment"
        return $true
    }
    
    $confirm = Read-Host "Proceed with deployment? (y/n)"
    return $confirm -match "^[Yy]"
}

# Tear down existing deployment
function Tear-Down-Deployment {
    Write-Header "Tearing Down Existing Deployment"
    
    Write-Warning "This will STOP and REMOVE PrintFarmer containers and volumes"
    Write-Info "The following will be PRESERVED:"
    Write-Info "  - Base container images (mcr.microsoft.com/*, node:*, postgres:*, etc.)"
    Write-Info "  - OrcaSlicer binaries and worker images"
    Write-Info "  - Downloaded image TAR files (if using offline mode)"
    Write-Host ""
    
    if (-not $NonInteractive) {
        $confirm = Read-Host "Are you sure? (y/n)"
        if ($confirm -notmatch "^[Yy]") {
            Write-Info "Tear-down cancelled"
            return
        }
    }
    
    Write-Info "Stopping PrintFarmer containers..."
    try {
        # docker compose down -v removes containers and volumes but NOT images
        # This preserves base images and OrcaSlicer binaries
        docker compose --env-file .env -f docker-compose.yml down -v
        Write-Success "PrintFarmer containers stopped and volumes removed"
    } catch {
        Write-ErrorMsg "Failed to tear down: $_"
        exit 1
    }
    
    Write-Info "Cleaning up orphaned containers (if any)..."
    try {
        docker compose --env-file .env -f docker-compose.yml down --remove-orphans 2>$null
        Write-Success "Orphaned containers cleaned"
    } catch {
        # Non-critical - continue anyway
    }
    
    Write-Success "Tear-down completed successfully"
    Write-Info ""
    Write-Info "To redeploy with the same configuration, run:"
    Write-Info "  .\scripts\deploy-docker.ps1 -Redeploy"
    Write-Info ""
    Write-Info "To deploy with different settings, run:"
    Write-Info "  .\scripts\deploy-docker.ps1"
    Write-Info ""
    Write-Info "To manually clean up all images (NOT recommended):"
    Write-Info "  docker image prune -a"
}

# Redeploy existing configuration
# Wait for API to become healthy by polling /health endpoint
function Wait-ForApiHealth {
    param(
        [int]$TimeoutSeconds = 180,
        [int]$IntervalSeconds = 3,
        [string]$ApiPort = "5245"
    )
    
    $ApiUrl = "http://localhost:${ApiPort}"
    $HealthEndpoint = "${ApiUrl}/health"
    $HealthzEndpoint = "${ApiUrl}/healthz"
    
    Write-Info "Waiting for API to become healthy (timeout: ${TimeoutSeconds}s)..."
    Write-Info "  Health endpoint: $HealthEndpoint"
    
    $elapsed = 0
    $lastDetailLog = -999
    
    while ($elapsed -lt $TimeoutSeconds) {
        try {
            $response = Invoke-WebRequest -Uri $HealthzEndpoint -TimeoutSec 3 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                # Try to get detailed health
                try {
                    $healthResponse = Invoke-WebRequest -Uri $HealthEndpoint -TimeoutSec 3 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
                    if ($healthResponse.StatusCode -eq 200) {
                        $healthJson = $healthResponse.Content | ConvertFrom-Json
                        if ($healthJson.status -eq "Healthy") {
                            Write-Success "API /health reports Healthy (HTTP 200)"
                            return $true
                        }
                    }
                } catch {
                    # Basic healthz is ok, but detailed /health not ready yet
                }
            }
        } catch {
            # No response yet
        }
        
        if (($elapsed - $lastDetailLog) -ge 15) {
            Write-Info "Still waiting for API to be fully healthy... ($elapsed/${TimeoutSeconds}s)"
            $lastDetailLog = $elapsed
        }
        
        Start-Sleep -Seconds $IntervalSeconds
        $elapsed += $IntervalSeconds
    }
    
    Write-Warning "API did not become healthy within ${TimeoutSeconds}s timeout."
    return $false
}

# Test API endpoint functionality
function Test-ApiEndpoints {
    param(
        [string]$ApiPort = "5245"
    )
    
    Write-Info "Testing API endpoints..."
    
    $ApiUrl = "http://localhost:${ApiPort}"
    $TestEndpoint = "${ApiUrl}/api/catalog/manufacturers"
    
    try {
        $response = Invoke-WebRequest -Uri $TestEndpoint -TimeoutSec 5 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            try {
                $data = $response.Content | ConvertFrom-Json
                $mfrCount = $data.Count
                Write-Success "✓ API endpoints: OK (/api/catalog/manufacturers - $mfrCount manufacturers)"
                return $true
            } catch {
                Write-Success "✓ API endpoints: OK (HTTP 200)"
                return $true
            }
        }
    } catch {
        Write-Warning "✗ API endpoints: Failed"
        return $false
    }
}

# Test proxy health (for microservices mode)
function Test-ProxyHealth {
    param(
        [string]$Architecture = "monolithic",
        [string]$HttpPort = "8080"
    )
    
    if ($Architecture -ne "microservices") {
        Write-Info "Skipping proxy health check (not microservices architecture)"
        return $true
    }
    
    Write-Info "Testing nginx proxy health..."
    
    $ProxyUrl = "http://localhost:${HttpPort}/api/healthz"
    $retries = 6
    $interval = 5
    
    for ($attempt = 1; $attempt -le $retries; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $ProxyUrl -TimeoutSec 3 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                $content = $response.Content
                if ($content -match '"status"' -or $content -match '^OK$') {
                    Write-Success "✓ nginx proxy appears to forward /api to API (HTTP ${HttpPort})"
                    return $true
                }
            }
        } catch {
            # No response yet
        }
        
        if ($attempt -le $retries) {
            Write-Info "  No response from proxy (attempt ${attempt}/${retries})"
            Start-Sleep -Seconds $interval
        }
    }
    
    Write-Warning "✗ nginx proxy check failed after $retries attempts"
    return $false
}

# Verify all containers are running and healthy
function Verify-ContainersRunning {
    Write-Info "Verifying containers are running..."
    
    try {
        $psOutput = docker compose --env-file .env -f docker-compose.yml ps 2>&1
        if (-not $?) {
            Write-Warning "Could not get container status"
            return $false
        }
        
        # Simple check - look for containers with "Up" in output
        $runningCount = ($psOutput | Select-String "Up" | Measure-Object).Count
        
        if ($runningCount -gt 0) {
            Write-Success "✓ Found $runningCount running container(s)"
            
            # Show brief status
            Write-Info "Container status:"
            $psOutput | ForEach-Object { Write-Host "  $_" }
            
            return $true
        } else {
            Write-Warning "✗ No running containers found"
            $psOutput | ForEach-Object { Write-Host "  $_" }
            return $false
        }
    } catch {
        Write-Warning "Error checking container status: $_"
        return $false
    }
}

# Comprehensive deployment verification
function Verify-Deployment {
    param(
        [string]$Architecture = "monolithic",
        [string]$ApiPort = "5245",
        [string]$HttpPort = "8080"
    )
    
    Write-Header "Verifying Deployment"
    
    Write-Info "Waiting 10 seconds for containers to initialize..."
    Start-Sleep -Seconds 10
    
    $allHealthy = $true
    
    # Step 1: Verify containers are running
    if (-not (Verify-ContainersRunning)) {
        $allHealthy = $false
    }
    
    Write-Host ""
    
    # Step 2: Wait for API health
    if (-not (Wait-ForApiHealth -TimeoutSeconds 180 -ApiPort $ApiPort)) {
        Write-Warning "⚠️ API health check timed out - services may need more time"
        Write-Info "Run 'docker compose logs api' to see API logs"
        $allHealthy = $false
    }
    
    Write-Host ""
    
    # Step 3: Test API endpoints
    if (-not (Test-ApiEndpoints -ApiPort $ApiPort)) {
        Write-Warning "✗ API endpoint tests failed"
        $allHealthy = $false
    }
    
    Write-Host ""
    
    # Step 4: Test proxy (microservices only)
    if (-not (Test-ProxyHealth -Architecture $Architecture -HttpPort $HttpPort)) {
        Write-Warning "⚠️ Proxy health check failed"
        # Don't fail deployment for this - proxy might need more time
    }
    
    Write-Host ""
    
    if ($allHealthy) {
        Write-Success "All deployment verification checks passed!"
        return $true
    } else {
        Write-Warning "Some verification checks failed or timed out"
        Write-Info "The deployment may still complete successfully - services continue initializing"
        return $false
    }
}

function Redeploy-Deployment {
    Write-Header "Redeploying PrintFarmer"
    
    Write-Info "Stopping existing containers..."
    try {
        docker compose down
    } catch {
        Write-Warning "Could not stop containers: $_"
    }
    
    Write-Info "Restarting deployment..."
    try {
        docker compose --env-file .env -f docker-compose.yml up -d --pull=missing
        Write-Success "Deployment restarted successfully"
    } catch {
        Write-ErrorMsg "Failed to redeploy: $_"
        exit 1
    }
}

# ============================================================================
# MAIN EXECUTION STARTS HERE
# ============================================================================

# Handle -Help flag first, before any other execution
if ($Help) {
    Show-Help
}

# Check prerequisites immediately (skip for -Help which already exited)
Check-Prerequisites

# Handle teardown mode
if ($TearDown) {
    Tear-Down-Deployment
    exit 0
}

# Handle redeploy mode
if ($Redeploy) {
    Redeploy-Deployment
    exit 0
}

Write-Header "PrintFarmer Docker Deployment - Windows Edition"

# Handle comprehensive offline deployment workflow
if ($PrepareOffline) {
    if (Prepare-OfflineDeployment -TargetDir $ImagesDir) {
        Write-Success "All offline materials prepared. You can now transfer the folder to your offline machine."
        exit 0
    } else {
        Write-ErrorMsg "Failed to prepare offline deployment materials"
        exit 1
    }
}

if ($DeployOffline) {
    if (Deploy-OfflineMode -SourceDir $ImagesDir) {
        Write-Info "Continuing with interactive deployment configuration..."
        # Fall through to normal deployment flow below
    } else {
        Write-ErrorMsg "Failed to load offline deployment materials"
        exit 1
    }
}

# Handle image management options (these exit early if used)
if ($BuildBaseImages) {
    Write-Info "Building pre-upgraded base images for offline deployment..."
    
    # Check if build-base-images.ps1 exists
    $buildScript = Join-Path $PSScriptRoot "docker" "build-base-images.ps1"
    if (-not (Test-Path $buildScript)) {
        Write-ErrorMsg "Build script not found: $buildScript"
        exit 1
    }
    
    # Run the build script with the specified images directory
    Write-Info "Invoking: $buildScript -CacheDir $ImagesDir"
    & $buildScript -CacheDir $ImagesDir
    
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Pre-upgraded base images built successfully!"
        Write-Info "Cache directory: $ImagesDir"
        Write-Info "Run 'docker load -i <tar-file>' to use these images"
    } else {
        Write-ErrorMsg "Failed to build base images"
        exit 1
    }
    exit 0
}

if ($PullImages) {
    if (Pull-BaseImages) {
        if ($SaveImages) {
            Save-ImagesToTar -TargetDir $ImagesDir
        }
    }
    exit 0
}

if ($SaveImages) {
    if (-not $PullImages) {
        Write-Info "Saving already downloaded images..."
    }
    Save-ImagesToTar -TargetDir $ImagesDir
    exit 0
}

if ($LoadImages) {
    if (Load-ImagesFromTar -SourceDir $ImagesDir) {
        Write-Info "Proceeding with deployment..."
    } else {
        exit 1
    }
}

if ($CacheOrcaSlicer) {
    Cache-OrcaSlicer -TargetDir "$ImagesDir/orcaslicer"
    exit 0
}

if ($LoadCachedOrcaSlicer) {
    Load-CachedOrcaSlicer -SourceDir "$ImagesDir/orcaslicer"
    exit 0
}

# Auto-load cached images if available (before interactive deployment)
# This searches common locations automatically - no user intervention needed
$autoLoadResult = Auto-Load-CachedImages -ImagesDir $ImagesDir

# Auto-load OrcaSlicer AppImage if available
$autoOrcaResult = Auto-Load-OrcaSlicer -OrcaDir ""

# Interactive deployment configuration
Write-Info "This script will help you deploy PrintFarmer using Docker containers."
Write-Host ""

# Load previous configuration (if it exists)
$config = Load-DeploymentConfig -ConfigPath "./.deploy-config"

# Use previously configured ImagesDir if not explicitly specified
if ($config['IMAGES_DIR'] -and $ImagesDir -eq "./docker-images") {
    $ImagesDir = $config['IMAGES_DIR']
    Write-Info "Using previously configured images directory: $ImagesDir"
}

# Choose architecture
$architecture = Choose-Architecture -Config $config -Quiet:$NonInteractive
$config['ARCHITECTURE'] = $architecture

# Choose database provider
$dbProvider = Choose-DatabaseProvider -Config $config -Architecture $architecture -Quiet:$NonInteractive
$config['DB_PROVIDER'] = $dbProvider

# Optional: Enable slicer workers (OrcaSlicer)
if (-not $NonInteractive) {
    Write-Host ""
    Write-Header "Distributed Slicing Configuration"
    
    $enableDistSlicing = Read-Host "Enable distributed slicing (uses external slicer workers)? [y/n] (default: y)"
    if ($enableDistSlicing -match "^[Nn]") {
        $config['ENABLE_DISTRIBUTED_SLICING'] = 'false'
        $config['ENABLE_ORCA_WORKER'] = 'false'
        $config['ORCA_WORKER_COUNT'] = '0'
        Write-Info "Distributed slicing disabled"
    } else {
        $config['ENABLE_DISTRIBUTED_SLICING'] = 'true'
        Write-Success "Distributed slicing enabled"
        
        # Ask about OrcaSlicer workers
        Write-Host ""
        $enableOrcaWorker = Read-Host "Enable OrcaSlicer worker(s)? [y/n] (default: n)"
        if ($enableOrcaWorker -match "^[Yy]") {
            $config['ENABLE_ORCA_WORKER'] = 'true'
            Write-Success "OrcaSlicer workers enabled"
            
            # OrcaSlicer version
            $orcaVersion = Read-Host "OrcaSlicer version to deploy (default: 2.4.0)"
            $config['ORCASLICER_VERSION'] = if ([string]::IsNullOrWhiteSpace($orcaVersion)) { '2.4.0' } else { $orcaVersion }
            
            # Worker replica count
            $workerCount = Read-Host "Number of OrcaSlicer worker replicas (default: 1)"
            $config['ORCA_WORKER_COUNT'] = if ([string]::IsNullOrWhiteSpace($workerCount)) { '1' } else { $workerCount }
            
            # Endpoint override (only for microservices)
            if ($architecture -eq 'microservices') {
                $overrideEndpoints = Read-Host "Override default worker service endpoints? [y/n] (default: n)"
                if ($overrideEndpoints -match "^[Yy]") {
                    $orcaEndpoint = Read-Host "OrcaSlicer worker endpoint (API reachable URL) (default: http://orcaslicer-worker:8080)"
                    $config['ORCA_WORKER_ENDPOINT'] = if ([string]::IsNullOrWhiteSpace($orcaEndpoint)) { 'http://orcaslicer-worker:8080' } else { $orcaEndpoint }
                }
            }
        } else {
            $config['ENABLE_ORCA_WORKER'] = 'false'
            $config['ORCA_WORKER_COUNT'] = '0'
            Write-Info "OrcaSlicer workers disabled"
        }
    }
}

# Optional: Enable Spoolman integration
if (-not $NonInteractive) {
    Write-Host ""
    Write-Header "Spoolman Integration"
    Write-Info "Spoolman provides centralized filament spool tracking."
    Write-Info "If you already run Spoolman you can point PrintFarmer at its base URL now."
    Write-Info "(You can also configure this later in the UI)"
    Write-Host ""
    
    $enableSpoolman = Read-Host "Enable Spoolman integration? [y/n] (default: n)"
    if ($enableSpoolman -match "^[Yy]") {
        $config['ENABLE_SPOOLMAN'] = 'true'
        Write-Success "Spoolman integration enabled"
        
        # Spoolman URL
        $spoolmanUrl = Read-Host "Spoolman base URL (protocol + host[:port], no trailing slash) (default: http://spoolman:7912)"
        $config['SPOOLMAN_BASE_URL'] = if ([string]::IsNullOrWhiteSpace($spoolmanUrl)) { 'http://spoolman:7912' } else { $spoolmanUrl }
    } else {
        $config['ENABLE_SPOOLMAN'] = 'false'
        Write-Info "Spoolman integration disabled"
    }
}

# Optional: Add monitoring support
if (-not $NonInteractive) {
    $addMonitoring = Read-Host "Include monitoring stack? (Prometheus + Grafana) [y/n] (default: n)"
    if ($addMonitoring -match "^[Yy]") {
        $config['INCLUDE_MONITORING'] = 'true'
        Write-Success "Monitoring enabled"
    } else {
        $config['INCLUDE_MONITORING'] = 'false'
    }
}

# Optional: Add telemetry support
if (-not $NonInteractive) {
    $addTelemetry = Read-Host "Include telemetry stack? (OpenTelemetry) [y/n] (default: n)"
    if ($addTelemetry -match "^[Yy]") {
        $config['INCLUDE_TELEMETRY'] = 'true'
        Write-Success "Telemetry enabled"
    } else {
        $config['INCLUDE_TELEMETRY'] = 'false'
    }
}

# Confirm deployment
if (-not (Confirm-DeploymentSettings -Settings $config)) {
    Write-Info "Deployment cancelled by user"
    exit 0
}

# Dry run validation
if ($DryRun) {
    Write-Success "Dry-run validation successful!"
    Write-Info "Configuration is valid and ready for deployment"
    Write-Info "Remove -DryRun flag to proceed with actual deployment"
    exit 0
}

# Save images directory for future use
$config['IMAGES_DIR'] = $ImagesDir

# Save configuration for future use
Save-DeploymentConfig -Config $config -ConfigPath "./.deploy-config"

# Build docker-compose configuration
Write-Header "Generating Docker Compose Configuration"

# Remove old compose file to force regeneration with current settings
if (Test-Path "docker-compose.yml") {
    Write-Info "Removing old docker-compose.yml for regeneration..."
    Remove-Item "docker-compose.yml" -Force
}

Write-Info "Generating docker-compose.yml using PowerShell generator..."

# Build generator command arguments
$generatorArgs = @(
    "-Architecture", $architecture,
    "-DbProvider", $dbProvider,
    "-OutputDir", (Get-Location).Path
)

# Add optional services
if ($config['INCLUDE_MONITORING'] -eq 'true') {
    $generatorArgs += "-IncludeMonitoring"
}
if ($config['INCLUDE_TELEMETRY'] -eq 'true') {
    $generatorArgs += "-IncludeTelemetry"
}
if ($config['INCLUDE_SECURITY'] -eq 'true') {
    $generatorArgs += "-IncludeSecurity"
}
if ($config['INCLUDE_REGISTRY'] -eq 'true') {
    $generatorArgs += "-IncludeRegistry"
}

# Add OrcaSlicer configuration if enabled
if ($config['ENABLE_ORCA_WORKER'] -eq 'true') {
    $generatorArgs += "-EnableOrcaWorker", $config['ORCA_WORKER_COUNT']
}

# Call the PowerShell compose-generator script
try {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $generatorScript = Join-Path $scriptDir "compose-generator.ps1"
    
    if (-not (Test-Path $generatorScript)) {
        throw "Compose generator not found at $generatorScript"
    }
    
    Write-Info "Calling PowerShell compose generator..."
    Write-Info "Generator: $generatorScript"
    Write-Info "Architecture: $architecture"
    Write-Info "Database: $dbProvider"
    
    # Call PowerShell script with arguments
    & pwsh -File $generatorScript @generatorArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Compose generator failed with exit code $LASTEXITCODE"
    }
    
    Write-Success "Docker compose configuration generated successfully"
} catch {
    Write-ErrorMsg "Failed to generate docker-compose.yml: $_"
    exit 1
}

# Verify docker-compose.yml was created
if (-not (Test-Path "docker-compose.yml")) {
    Write-ErrorMsg "docker-compose.yml was not generated by compose-generator"
    exit 1
}

# CRITICAL: Fix Windows Docker volume mount paths
# On Windows, Docker needs absolute paths for volume mounts or they won't resolve
# Convert any remaining relative paths to absolute paths with forward slashes
Write-Info "Fixing volume mount paths for Windows Docker compatibility..."
$composeContent = Get-Content "docker-compose.yml" -Raw
$repoRoot = (Get-Location).Path -replace '\\', '/'

# Line-by-line replacement to handle Docker volume mounts correctly
$lines = $composeContent -split "`n"
$fixedLines = @()
foreach ($line in $lines) {
    if ($line -match '\./scripts/docker/([^:]+):') {
        $fileName = $matches[1]
        $newLine = $line -replace '\./scripts/docker/[^:]+:', "$repoRoot/scripts/docker/$fileName`:"
        Write-Verbose "Converting volume mount: ./scripts/docker/$fileName -> $repoRoot/scripts/docker/$fileName"
        $fixedLines += $newLine
    } else {
        $fixedLines += $line
    }
}
$composeContent = $fixedLines -join "`n"

Set-Content "docker-compose.yml" -Value $composeContent
Write-Info "Volume mount paths fixed for Windows Docker"

Write-Success "Docker-compose.yml ready for deployment"

# Generate .env file with database credentials and configuration
if (-not (Generate-EnvFile -Config $config -OutputPath ".env")) {
    Write-ErrorMsg "Failed to generate .env file"
    exit 1
}

# Deploy
Write-Header "Starting Deployment"

# Ensure we're in the repository root for volume mounts to resolve correctly
# (docker compose uses relative paths for volume mounts like ./scripts/docker/init-postgres.sh)
$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
Write-Info "Ensuring deployment runs from repository root: $repoRoot"
Push-Location $repoRoot

try {
    docker image prune -f 2>&1 | Out-Null
} catch {
    Write-Warning "Image prune failed (non-critical): $_"
}

Write-Info "Starting Docker containers..."
Write-Info "This may take a few moments on first run..."
try {
    # Start ONLY the database container first to ensure it initializes properly
    # If we start all containers with -d, they all start in parallel and API fails
    # trying to connect to a database that hasn't finished initializing
    Write-Info "Step 1/3: Starting database container..."
    docker compose --env-file .env -f docker-compose.yml up -d database
    Write-Success "Database container started"
    
    # Wait for database to be healthy (max 120 seconds)
    Write-Info "Step 2/3: Waiting for database to become healthy..."
    $dbHealthy = $false
    $maxAttempts = 24  # 24 * 5 seconds = 120 seconds
    $attempt = 0
    
    while (-not $dbHealthy -and $attempt -lt $maxAttempts) {
        Start-Sleep -Seconds 5
        $attempt++
        
        $psOutput = docker compose --env-file .env -f docker-compose.yml ps database 2>&1
        if ($psOutput -match "healthy|Up.*\(healthy\)") {
            $dbHealthy = $true
            Write-Success "Database is healthy"
        } else {
            $status = if ($psOutput -match "(Up|Exited|Created)") { $matches[1] } else { "Unknown" }
            $healthStatus = if ($psOutput -match "\(([^)]+)\)") { $matches[1] } else { "no health status" }
            Write-Info "Waiting for database... (attempt $attempt/$maxAttempts) Status: $status ($healthStatus)"
            
            # If exited, show logs immediately
            if ($psOutput -match "Exited") {
                Write-Warning "Database container exited! Showing logs:"
                docker compose --env-file .env -f docker-compose.yml logs database 2>&1 | Select-Object -Last 20
                Pop-Location
                exit 1
            }
        }
    }
    
    if (-not $dbHealthy) {
        Write-ErrorMsg "Database failed to become healthy after 120 seconds"
        Write-Info "Final database status:"
        docker compose --env-file .env -f docker-compose.yml ps database
        Write-Info "Database logs (last 30 lines):"
        docker compose --env-file .env -f docker-compose.yml logs database 2>&1 | Select-Object -Last 30
        Pop-Location
        exit 1
    }
    
    # Now start the rest of the containers (they can now connect to database)
    Write-Info "Step 3/3: Starting remaining containers..."
    Write-Info "Verifying database is accepting connections before starting dependent services..."
    
    # Do a more direct test - try to connect to the database
    $maxConnectionAttempts = 12
    $connectionAttempt = 0
    $dbConnected = $false
    
    while (-not $dbConnected -and $connectionAttempt -lt $maxConnectionAttempts) {
        $connectionAttempt++
        try {
            # Try to run a simple query in the database container
            $testQuery = docker exec printfarmer-database psql -U printfarmer -d printfarmer -c "SELECT 1" 2>&1
            if ($testQuery -match "1" -or $testQuery -match "row") {
                Write-Success "Database is accepting connections"
                $dbConnected = $true
            } else {
                Write-Info "Database connection test (attempt $connectionAttempt/$maxConnectionAttempts): $testQuery"
                Start-Sleep -Seconds 5
            }
        } catch {
            if ($connectionAttempt -lt $maxConnectionAttempts) {
                Write-Info "Database connection attempt $connectionAttempt/$maxConnectionAttempts failed, retrying..."
                Start-Sleep -Seconds 5
            }
        }
    }
    
    if (-not $dbConnected) {
        Write-Warning "Could not verify database connection after $maxConnectionAttempts attempts, but continuing anyway..."
        Write-Info "Waiting an extra 10 seconds for database to fully initialize..."
        Start-Sleep -Seconds 10
    } else {
        Write-Info "Waiting 3 seconds before starting dependent services..."
        Start-Sleep -Seconds 3
    }
    
    docker compose --env-file .env -f docker-compose.yml up -d api orcaslicer-worker frontend nginx-proxy --pull=missing
    Write-Success "All containers started successfully"
} catch {
    Write-ErrorMsg "Failed to start containers: $_"
    Pop-Location
    exit 1
}

Write-Host ""

# Give containers time to start
Write-Info "Waiting for containers to initialize..."
Start-Sleep -Seconds 5

# Check if containers actually started (before verification)
Write-Info "Checking container status..."
$psOutput = docker compose --env-file .env -f docker-compose.yml ps 2>&1
$runningCount = ($psOutput | Select-String "Up" | Measure-Object).Count
$exitedCount = ($psOutput | Select-String "Exited" | Measure-Object).Count

if ($exitedCount -gt 0) {
    Write-ErrorMsg "✗ Some containers failed to start!"
    Write-Host ""
    Write-Info "Container status:"
    $psOutput | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Info "Showing container logs for failed containers..."
    docker compose --env-file .env -f docker-compose.yml logs
    Write-ErrorMsg "Container startup failed. Check logs above for details."
    Pop-Location
    exit 1
}

if ($runningCount -eq 0) {
    Write-ErrorMsg "✗ No containers are running!"
    Write-Info "Container status:"
    $psOutput | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Info "Run 'docker compose logs' to see what went wrong"
    Pop-Location
    exit 1
}

Write-Success "All containers started successfully"

Write-Host ""

# Verify deployment before declaring success
$verificationPassed = Verify-Deployment -Architecture $architecture -ApiPort "5245" -HttpPort "80"

Write-Header "Deployment Status"

if ($verificationPassed) {
    Write-Success "✅ PrintFarmer Deployment Verified and Ready!"
} else {
    Write-Warning "⚠️ Deployment verification incomplete, but containers are running"
    Write-Info "Services may still be initializing. Check logs with: docker compose logs"
}

Write-Host ""
Write-Success "PrintFarmer is now running!"
Write-Host ""
Write-Host "Access the application:" -ForegroundColor Cyan
Write-Host "  Web UI:      http://localhost" -ForegroundColor White
Write-Host "  API:         http://localhost:5245" -ForegroundColor White
Write-Host ""
Write-Host "Check status:" -ForegroundColor Cyan
Write-Host "  docker compose ps" -ForegroundColor White
Write-Host "  docker compose logs -f" -ForegroundColor White
Write-Host ""
Write-Host "Management:" -ForegroundColor Cyan
Write-Host "  Stop:       docker compose down" -ForegroundColor White
Write-Host "  Restart:    docker compose restart" -ForegroundColor White
Write-Host "  Logs:       docker compose logs -f" -ForegroundColor White
Write-Host ""
Write-Host "For detailed information, see:" -ForegroundColor Cyan
Write-Host "  README.md" -ForegroundColor White
Write-Host "  DEPLOYMENT_OVERVIEW.md" -ForegroundColor White
Write-Host ""

# Restore original directory
Pop-Location
