<#
Bootstrap script for Windows (PowerShell)
Installs prerequisites to build and run PrintFarmer: .NET SDK 9.x, Node.js 18.x, npm, git.
Uses winget when available, falls back to direct installer using the Microsoft dotnet-install.ps1 if needed.
Run this script from an elevated PowerShell (Run as Administrator).
#>

param()

function Write-Info([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Yellow }

# Ensure script runs elevated
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warn "This script must be run as Administrator. Re-run PowerShell as Administrator and re-run this script."
    exit 1
}

$REQ_DOTNET_VERSION = $env:DOTNET_VERSION -or '9.0.302'
$REQ_NODE_MAJOR = $env:NODE_VERSION -or '18'

# Check for winget and Chocolatey availability
Write-Info "Checking winget availability..."
 $haveWinget = $null -ne (Get-Command winget -ErrorAction SilentlyContinue)
Write-Info "Checking Chocolatey availability..."
 $haveChoco = $null -ne (Get-Command choco -ErrorAction SilentlyContinue)

if ($haveWinget) {
    Write-Info "winget found — using winget to install packages"
    try {
        Write-Info "Installing .NET SDK (9.x)"
        winget install --id Microsoft.DotNet.SDK.9 -e --accept-package-agreements --accept-source-agreements -h
    } catch {
        Write-Warn "winget install of .NET SDK failed; will try fallback installer"
    }

    try {
        Write-Info "Installing Node.js $REQ_NODE_MAJOR"
        winget install --id OpenJS.NodeJS.$REQ_NODE_MAJOR -e --accept-package-agreements --accept-source-agreements -h
    } catch {
        Write-Warn "winget install of Node.js failed; will try fallback"
    }

    try {
        Write-Info "Installing Git"
        winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements -h
    } catch {
        Write-Warn "winget install of Git failed; continuing"
    }
} else {
    Write-Warn "winget not found — attempting choco fallback if available"
    if ($haveChoco) {
        Write-Info "Chocolatey found — using choco to install packages"
        try {
            Write-Info "Installing Node.js $REQ_NODE_MAJOR via choco"
            choco install nodejs-lts -y --no-progress
        } catch {
            Write-Warn "choco install of Node.js failed: $_"
        }
        try {
            Write-Info "Installing Git via choco"
            choco install git -y --no-progress
        } catch {
            Write-Warn "choco install of Git failed: $_"
        }
    } else {
        Write-Warn "winget and choco not found — attempting manual installs where possible"
    }
}

# Verify dotnet presence; fallback to dotnet-install.ps1 from Microsoft if missing
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Info ".NET SDK not found or not on PATH — attempting manual install"
    # Use the official dotnet-install script (PowerShell)
    $installScriptUrl = 'https://dot.net/v1/dotnet-install.ps1'
    $tmp = Join-Path $env:TEMP 'dotnet-install.ps1'
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $installScriptUrl -OutFile $tmp -ErrorAction Stop
        powershell -NoProfile -ExecutionPolicy Bypass -File $tmp -Version $REQ_DOTNET_VERSION
        Write-Success ".NET install attempted via dotnet-install.ps1"
    } catch {
        Write-Warn "Automatic dotnet install failed: $_"
        Write-Warn "Please install .NET SDK ${REQ_DOTNET_VERSION} manually from https://dotnet.microsoft.com/download/dotnet/9.0"
        exit 2
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
    }
} else {
    Write-Info "dotnet already installed: $(dotnet --info 2>$null | Select-Object -First 1)"
}

# Verify Node
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Info "Node.js not found — trying to install via winget if available"
    if ($haveWinget) {
        try { winget install --id OpenJS.NodeJS.$REQ_NODE_MAJOR -e --accept-package-agreements --accept-source-agreements -h; } catch { Write-Warn "winget node install failed" }
    }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        Write-Warn "Node.js still not installed. Please install Node.js 18+ from https://nodejs.org/ and re-run."
    }
} else {
    Write-Info "node present: $(node -v)"
}

# Verify npm
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Warn "npm not found (usually packaged with Node.js). Please install Node.js which includes npm."
} else {
    Write-Info "npm present: $(npm -v)"
}

# Verify git
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Info "Git not found — attempting winget install"
    if ($haveWinget) {
        try { winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements -h } catch { Write-Warn "winget git install failed" }
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Warn "Git still not present. Please install Git for Windows and re-run script." }
} else { Write-Info "git present: $(git --version)" }

Write-Success "Bootstrap complete. Run these commands as a normal user to verify:"
Write-Host "    dotnet --info"
Write-Host "    node --version"
Write-Host "    npm --version"
Write-Host "    git --version"

Write-Host "To build the repo (from repo 'src' directory):"
Write-Host "    cd src"
Write-Host "    dotnet restore ./farm-web.sln"
Write-Host "    dotnet build ./farm-web.sln -c Debug"

Write-Success "Done."