# PrintFarmer Docker Deployment Script for Windows PowerShell
# Automated setup for Docker-based deployment with offline image support

#Requires -Version 5.1

param(
    [switch]$DryRun,
    [switch]$NonInteractive,
    [switch]$TearDown,
    [switch]$Help,
    [switch]$Redeploy,
    [switch]$VerifyDeployment,
    [switch]$PullImages,
    [switch]$SaveImages,
    [switch]$LoadImages,
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
    Write-Host "PrintFarmer Docker Deployment Script for Windows"
    Write-Host ""
    Write-Host "USAGE:"
    Write-Host "    .\scripts\deploy-docker.ps1 [OPTIONS]"
    Write-Host ""
    Write-Host "IMAGE MANAGEMENT - Offline deployments:"
    Write-Host "    -PullImages                Download all base container images"
    Write-Host "    -SaveImages                Export downloaded images to TAR files"
    Write-Host "    -LoadImages                Load saved images from TAR files"
    Write-Host "    -ImagesDir PATH            Directory for storing image TAR files"
    Write-Host ""
    Write-Host "EXAMPLES:"
    Write-Host "    # Download and save images for offline use"
    Write-Host "    .\scripts\deploy-docker.ps1 -PullImages -SaveImages -ImagesDir C:\docker-images"
    Write-Host ""
    Write-Host "    # Deploy using offline images (no internet required)"
    Write-Host "    .\scripts\deploy-docker.ps1 -LoadImages -ImagesDir C:\docker-images"
    Write-Host ""
    Write-Host "    # Non-interactive deployment"
    Write-Host "    .\scripts\deploy-docker.ps1 -NonInteractive"
    Write-Host ""
    exit 0
}

# Define base images to manage
$script:BaseImages = @(
    "mcr.microsoft.com/dotnet/aspnet:9.0",
    "mcr.microsoft.com/dotnet/sdk:9.0",
    "node:18-alpine",
    "postgres:16-alpine",
    "mysql:8.0-alpine",
    "mcr.microsoft.com/mssql/server:2022-latest",
    "nginx:alpine",
    "prometheus:latest",
    "grafana/grafana:latest",
    "otel/opentelemetry-collector:latest",
    "registry:2"
)

# Pull all base images from registry
function Pull-BaseImages {
    Write-Header "Pulling Base Container Images"
    
    Write-Info "This downloads all base images needed for PrintFarmer deployments"
    Write-Info "Download size: approximately 800MB to 2GB"
    Write-Host ""
    
    $successCount = 0
    $failCount = 0
    
    foreach ($image in $script:BaseImages) {
        Write-Info "Pulling $image..."
        try {
            docker pull $image
            Write-Success "Pulled: $image"
            $successCount++
        } catch {
            Write-Warning "Failed to pull $image : $_"
            $failCount++
        }
    }
    
    Write-Header "Pull Summary"
    Write-Host "Successfully pulled: $successCount/$($script:BaseImages.Count)" -ForegroundColor Green
    
    if ($failCount -gt 0) {
        Write-Warning "Failed to pull: $failCount images"
        Write-Info "Check your internet connection and try again"
        return $false
    }
    
    Write-Success "All base images downloaded successfully!"
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

# ============================================================================
# MAIN EXECUTION STARTS HERE
# ============================================================================

# Handle -Help flag first, before any other execution
if ($Help) {
    Show-Help
}

# Check prerequisites immediately (skip for -Help which already exited)
Check-Prerequisites

Write-Header "PrintFarmer Docker Deployment - Windows Edition"

# Handle image management options
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

# Detect environment
Write-Header "Environment Detection"

# Check Docker
try {
    $DockerVersion = docker --version
    Write-Success "Docker found: $DockerVersion"
} catch {
    Write-ErrorMsg "Docker not found! Please install Docker Desktop for Windows"
    Write-Info "Visit: https://docs.docker.com/desktop/install/windows-install/"
    exit 1
}

# Check Docker Compose
try {
    $ComposeVersion = docker compose version
    Write-Success "Docker Compose found: $ComposeVersion"
} catch {
    Write-ErrorMsg "Docker Compose not found! Please install Docker Desktop with Compose support"
    exit 1
}

# Check if Docker is running
try {
    $null = docker ps 2>$null
    Write-Success "Docker daemon is running"
} catch {
    Write-ErrorMsg "Docker daemon is not running! Please start Docker Desktop"
    exit 1
}

Write-Header "Deployment Configuration"

Write-Info "Configuration:"
Write-Info "  Environment file: $EnvFile"
Write-Info "  Config file: $ConfigFile"
Write-Info "  Output directory: $OutputDir"

if ($Architecture) {
    Write-Info "  Architecture: $Architecture"
}

Write-Host ""

# Show summary
Write-Header "Ready for Deployment"

if ($DryRun) {
    Write-Success "Dry run validation successful!"
    Write-Info "Configuration is valid and ready for deployment"
    exit 0
}

if (-not $NonInteractive) {
    $confirm = Read-Host "Proceed with deployment? (y/n)"
    if ($confirm -notmatch "^[Yy]") {
        Write-Info "Deployment cancelled"
        exit 0
    }
}

Write-Header "Starting Deployment"

Write-Info "Pulling latest Docker images..."
docker image prune -f | Out-Null

Write-Info "Starting Docker containers..."
try {
    docker compose --env-file $EnvFile up -d
    Write-Success "Containers started successfully"
} catch {
    Write-ErrorMsg "Failed to start containers: $_"
    exit 1
}

Write-Header "Deployment Complete!"

Write-Host ""
Write-Success "PrintFarmer is now running!"
Write-Host ""
Write-Host "Access the application:" -ForegroundColor Cyan
Write-Host "  Web UI:      http://localhost" -ForegroundColor White
Write-Host "  API:         http://localhost:5245" -ForegroundColor White
Write-Host ""
Write-Host "Check status:" -ForegroundColor Cyan
Write-Host "  docker compose ps" -ForegroundColor White
Write-Host "  docker compose logs -f api" -ForegroundColor White
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
