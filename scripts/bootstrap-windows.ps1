<#
Bootstrap script for Windows (PowerShell)
Installs prerequisites to build and run PrintFarmer: .NET SDK 10.x, Node.js (recommended v24.13.0), npm, git.
Uses winget when available, falls back to direct installer using the Microsoft dotnet-install.ps1 if needed.
Run this script from an elevated PowerShell (Run as Administrator).
#>

param()

# Support a -Verify switch to run dotnet/node verification and a small build smoke-test
# Simple CLI flag parsing for -Verify / --verify
$Verify = $false
$ForceElevated = $false
if ($args -ne $null) {
    foreach ($a in $args) {
        if ($a -ieq '-Verify' -or $a -ieq '--verify') { $Verify = $true }
        if ($a -ieq '-ForceElevated' -or $a -ieq '--elevate' -or $a -ieq '-Elevate') { $ForceElevated = $true }
    }
}

function Write-Info([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Cyan }
function Write-Success([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "[bootstrap] $m" -ForegroundColor Yellow }

function Test-IsElevated {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-RunElevated {
    param(
        [Parameter(Mandatory=$true)][string]$Command,
        [string[]]$ExtraArgs = @()
    )
    # Start a new PowerShell process elevated to run the provided command.
    $argLine = $ExtraArgs -join ' '
    $psArgs = "-NoProfile -ExecutionPolicy Bypass -Command &{ $Command $argLine }"
    try {
        Start-Process -FilePath pwsh -ArgumentList $psArgs -Verb RunAs -WindowStyle Hidden -Wait
    } catch {
        # Fall back to powershell.exe if pwsh not present
        Start-Process -FilePath powershell -ArgumentList $psArgs -Verb RunAs -WindowStyle Hidden -Wait
    }
}

# Ensure script runs elevated or prompt user for action
if (-not (Test-IsElevated)) {
    if ($ForceElevated) {
        Write-Info "-ForceElevated specified — re-running script elevated..."
        $scriptPath = $MyInvocation.MyCommand.Definition
        try {
            Start-Process -FilePath pwsh -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs -Wait
        } catch {
            Start-Process -FilePath powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs -Wait
        }
        exit $LASTEXITCODE
    }

    Write-Warn "This script is not running as Administrator. Some install steps require elevation."
    Write-Host "Options: [R]e-run elevated, [C]ontinue (attempt per-command elevation), [E]xit" -ForegroundColor Yellow
    $choice = Read-Host "Choose an option (R/C/E)"
    switch ($choice.ToUpper()) {
        'R' {
            Write-Info "Re-running script elevated..."
            $scriptPath = $MyInvocation.MyCommand.Definition
            try {
                Start-Process -FilePath pwsh -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs -Wait
            } catch {
                Start-Process -FilePath powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs -Wait
            }
            exit $LASTEXITCODE
        }
        'C' {
            Write-Warn "Continuing without full elevation. The script will attempt to elevate individual commands when needed."
            # set a flag so callers know to attempt Run-Elevated where appropriate
            $global:AllowPerCommandElevation = $true
        }
        default {
            Write-Warn "Exiting. Re-run PowerShell as Administrator and re-run this script if you need installs."
            exit 1
        }
    }
} else {
    $global:AllowPerCommandElevation = $false
}

$REQ_DOTNET_VERSION = $env:DOTNET_VERSION -or '10.0.101'
# Default to Node 24 major LTS for frontend toolchain
$REQ_NODE_MAJOR = $env:NODE_VERSION -or '24'

# Check for winget and Chocolatey availability
Write-Info "Checking winget availability..."
 $haveWinget = $null -ne (Get-Command winget -ErrorAction SilentlyContinue)
Write-Info "Checking Chocolatey availability..."
 $haveChoco = $null -ne (Get-Command choco -ErrorAction SilentlyContinue)

if ($haveWinget) {
    Write-Info "winget found — using winget to install packages"
    try {
        Write-Info "Installing .NET SDK (9.x)"
        if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
            Invoke-RunElevated -Command 'winget' -ExtraArgs @('install','--id','Microsoft.DotNet.SDK.9','-e','--accept-package-agreements','--accept-source-agreements','-h')
        } else {
            winget install --id Microsoft.DotNet.SDK.9 -e --accept-package-agreements --accept-source-agreements -h
        }
    } catch {
        Write-Warn "winget install of .NET SDK failed; will try fallback installer"
    }

    try {
        Write-Info "Installing Node.js $REQ_NODE_MAJOR"
        if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
            Invoke-RunElevated -Command 'winget' -ExtraArgs @('install','--id',"OpenJS.NodeJS.$REQ_NODE_MAJOR",'-e','--accept-package-agreements','--accept-source-agreements','-h')
        } else {
            winget install --id OpenJS.NodeJS.$REQ_NODE_MAJOR -e --accept-package-agreements --accept-source-agreements -h
        }
    } catch {
        Write-Warn "winget install of Node.js failed; will try fallback"
    }

    try {
        Write-Info "Installing Git"
        if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
            Invoke-RunElevated -Command 'winget' -ExtraArgs @('install','--id','Git.Git','-e','--accept-package-agreements','--accept-source-agreements','-h')
        } else {
            winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements -h
        }
    } catch {
        Write-Warn "winget install of Git failed; continuing"
    }
} else {
    Write-Warn "winget not found — attempting choco fallback if available"
    if ($haveChoco) {
        Write-Info "Chocolatey found — using choco to install packages"
        try {
            Write-Info "Installing Node.js $REQ_NODE_MAJOR via choco"
                if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
                    Invoke-RunElevated -Command 'choco' -ExtraArgs @('install','nodejs-lts','-y','--no-progress')
                } else {
                    choco install nodejs-lts -y --no-progress
                }
        } catch {
            Write-Warn "choco install of Node.js failed: $_"
        }
        try {
            Write-Info "Installing Git via choco"
            if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
                Invoke-RunElevated -Command 'choco' -ExtraArgs @('install','git','-y','--no-progress')
            } else {
                choco install git -y --no-progress
            }
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
        if (-not (Test-IsElevated) -and $global:AllowPerCommandElevation) {
            Write-Info "Running dotnet-install.ps1 elevated"
            Invoke-RunElevated -Command 'powershell' -ExtraArgs @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$tmp`"",'-Version',$REQ_DOTNET_VERSION)
        } else {
            powershell -NoProfile -ExecutionPolicy Bypass -File $tmp -Version $REQ_DOTNET_VERSION
        }
        Write-Success ".NET install attempted via dotnet-install.ps1"
    } catch {
        Write-Warn "Automatic dotnet install failed: $_"
        Write-Warn "Please install .NET SDK ${REQ_DOTNET_VERSION} manually from https://dotnet.microsoft.com/download/dotnet/10.0"
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
        Write-Warn "Node.js still not installed. Please install Node.js (v20.19.0 or later) from https://nodejs.org/ and re-run."
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

# Verify/Install Python3 and ruamel.yaml (CRITICAL for Docker Compose YAML generation)
# ruamel.yaml is required by compose-generator.sh for proper YAML handling
if (-not (Get-Command python3 -ErrorAction SilentlyContinue)) {
    Write-Info "Python3 not found — attempting winget install"
    if ($haveWinget) {
        try { winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements -h } catch { Write-Warn "winget python3 install failed" }
    }
    if (-not (Get-Command python3 -ErrorAction SilentlyContinue)) { Write-Warn "Python3 still not present. Please install Python 3 from https://www.python.org/downloads/ and re-run script." }
} else { Write-Info "python3 present: $(python3 --version)" }

# Install ruamel.yaml Python module (CRITICAL DEPENDENCY)
if (Get-Command python3 -ErrorAction SilentlyContinue) {
    try {
        & python3 -c "from ruamel.yaml import YAML" 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Info "ruamel.yaml already installed"
        } else {
            Write-Info "Installing Python module ruamel.yaml (CRITICAL for Docker deployment)..."
            & python3 -m pip install --user ruamel.yaml
        }
    } catch {
        Write-Info "Installing Python module ruamel.yaml (CRITICAL for Docker deployment)..."
        & python3 -m pip install --user ruamel.yaml
    }
} else {
    Write-Warn "Python3 not available; cannot install ruamel.yaml. Please install Python3 and re-run script."
}


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

if ($Verify) {
    Write-Info "Running verification (--Verify) checks"
    try {
        dotnet --info
    } catch { Write-Warn "dotnet verification failed: $_" }
    try { node --version } catch { Write-Warn "node verification failed: $_" }
    try { npm --version } catch { Write-Warn "npm verification failed: $_" }
    try { git --version } catch { Write-Warn "git verification failed: $_" }

    # Small smoke test: attempt to build the API project if present
    $repoRoot = Resolve-Path -Path (Join-Path $PSScriptRoot '..')
    $apiProj = Join-Path $repoRoot 'src\api\Farm.Web.Api.csproj'
    if (Test-Path $apiProj) {
        Write-Info "Running small dotnet build smoke test (API project)"
        Push-Location (Join-Path $repoRoot 'src')
        dotnet restore ./farm-web.sln
        dotnet build ./api/Farm.Web.Api.csproj -c Debug --no-restore
        Pop-Location
    } else {
        Write-Info "API project not found for smoke test; skipping build"
    }
}