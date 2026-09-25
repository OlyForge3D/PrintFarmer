#!/usr/bin/env pwsh
# Smoke-tests one packaged host-update CLI archive (issue #2997) with the PowerShell wrapper.
#
#   pwsh -NoProfile -File tests/test-host-update-cli-package.ps1 -Assets <dir> -Tag <vX.Y.Z> -Rid <rid>
#
# Windows packages are .zip; Linux/macOS packages are .tar.gz. Verifies the archive against the
# package SHA256SUMS and the host-update-cli.json per-file inventory, then runs the packaged
# PowerShell wrapper against a throwaway host-update root. No .NET SDK or runtime is used, and
# nothing is started or rolled back: the root has no journal and the fake tools never execute.
# The root lives under $env:RUNNER_TEMP (CI) or $HOME, because the CLI refuses a root inside the
# OS temp directory or the working directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Assets,
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Rid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$isWindowsPackage = $Rid.StartsWith('win-', [StringComparison]::Ordinal)
$name = "printfarmer-host-update-cli-$Tag-$Rid"
$archive = if ($isWindowsPackage) { "$name.zip" } else { "$name.tar.gz" }
$sums = "printfarmer-host-update-cli-$Tag-SHA256SUMS"
$script:failures = 0
$script:passes = 0

function Pass([string] $label) { Write-Host "[PASS] $label"; $script:passes++ }
function Fail([string] $label) { Write-Host "[FAIL] $label"; $script:failures++ }
function Hash([string] $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$Assets = (Resolve-Path -LiteralPath $Assets).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("pf-host-update-pkg-" + [guid]::NewGuid().ToString('N'))
$rootParent = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $HOME }
$root = Join-Path $rootParent ("pf-host-update-root-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work, $root | Out-Null

try {
    # 1. Checksum and archive shape.
    $line = Get-Content -LiteralPath (Join-Path $Assets $sums) | Where-Object { ($_ -split '\s+', 2)[1] -eq $archive }
    $expected = if ($line) { ($line -split '\s+', 2)[0] } else { '' }
    if ($expected -and (Hash (Join-Path $Assets $archive)) -eq $expected) { Pass "archive matches $sums" } else { Fail "archive does not match $sums" }
    if ($isWindowsPackage) {
        Expand-Archive -LiteralPath (Join-Path $Assets $archive) -DestinationPath $work
    } else {
        tar -xzf (Join-Path $Assets $archive) -C $work
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    }
    $top = @(Get-ChildItem -LiteralPath $work -Force)
    if ($top.Count -eq 1 -and $top[0].Name -eq $name) { Pass "archive has one top-level $name directory" } else { Fail "unexpected archive top level" }
    $package = Join-Path $work $name

    # 2. Package identity and per-file inventory.
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $package 'host-update-cli.json') | ConvertFrom-Json
    if ($manifest.tag -eq $Tag -and $manifest.rid -eq $Rid -and $manifest.authorizesRollout -eq $false -and $manifest.selfContained -eq $true) {
        Pass "host-update-cli.json identifies $Tag $Rid"
    } else { Fail "host-update-cli.json identity mismatch" }
    $listed = @($manifest.files.PSObject.Properties | ForEach-Object { $_.Name })
    $inventoryOk = $true
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $path = Join-Path $package $entry.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Hash $path) -ne $entry.Value) {
            $inventoryOk = $false; Write-Host "  bad entry: $($entry.Name)"
        }
    }
    $prefix = $package.Length + 1
    $actual = @(Get-ChildItem -LiteralPath $package -Recurse -File -Force |
        Where-Object { $_.Name -ne 'host-update-cli.json' } |
        ForEach-Object { $_.FullName.Substring($prefix).Replace('\', '/') })
    if (Compare-Object ($actual | Sort-Object) ($listed | Sort-Object) -SyncWindow 0) { $inventoryOk = $false; Write-Host "  file list differs from inventory" }
    if ($inventoryOk) { Pass "every packaged file matches the inventory" } else { Fail "package inventory mismatch" }
    $apphost = if ($isWindowsPackage) { 'Farm.HostUpdate.Cli.exe' } else { 'Farm.HostUpdate.Cli' }
    $required = @('printfarmer-host-update.ps1', 'printfarmer-host-update.sh', 'common-utils.sh', 'LICENSE', 'THIRD-PARTY-NOTICES.md', "cli/$apphost")
    if (@($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $package $_) -PathType Leaf) }).Count -eq 0) {
        Pass "wrappers, apphost and notices present"
    } else { Fail "package layout incomplete" }

    # 3. Throwaway host root that satisfies the namespace proof.
    $toolSuffix = if ($IsWindows) { '.exe' } else { '' }
    $owned = 'app-data', 'model-uploads', 'gcode-storage', 'slicer-profiles', 'data-protection-keys'
    New-Item -ItemType Directory -Path (Join-Path $root 'state'), (Join-Path $root 'tools') | Out-Null
    $ownedMap = [ordered]@{}
    foreach ($dir in $owned) {
        $ownedMap[$dir] = Join-Path (Join-Path $root 'owned') $dir
        New-Item -ItemType Directory -Path $ownedMap[$dir] | Out-Null
    }
    $compose = Join-Path $root 'docker-compose.yml'
    $docker = Join-Path (Join-Path $root 'tools') "docker$toolSuffix"
    $sqlite = Join-Path (Join-Path $root 'tools') "sqlite3$toolSuffix"
    $database = Join-Path $root 'farm.db'
    Set-Content -LiteralPath $compose -Value 'services: {}'
    foreach ($file in $docker, $sqlite, $database) { New-Item -ItemType File -Path $file | Out-Null }
    $config = Join-Path $root 'host-update.json'
    [ordered]@{
        DB_PROVIDER = 'sqlite'
        ConnectionStrings = @{ Default = "Data Source=$database" }
        HostUpdateExecution = [ordered]@{
            RootDirectory = $root
            ComposeFiles = @($compose)
            HostExecutablePaths = [ordered]@{ docker = $docker; sqlite3 = $sqlite }
            OwnedDirectories = $ownedMap
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config

    $wrapper = Join-Path $package 'printfarmer-host-update.ps1'
    function Expect([int] $want, [string] $label, [string[]] $wrapperArgs) {
        Push-Location $work
        $saved = @{}
        foreach ($variable in 'PRINTFARMER_HOST_UPDATE_CLI_DIR', 'PRINTFARMER_DOTNET', 'DOTNET_ROOT') {
            $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
            [Environment]::SetEnvironmentVariable($variable, $null)
        }
        try {
            $output = & pwsh -NoProfile -NonInteractive -File $wrapper @wrapperArgs 2>&1
            $got = $LASTEXITCODE
        } finally {
            foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
            Pop-Location
        }
        if ($got -eq $want) { Pass "$label (exit $got)" } else { Fail "$label (exit $got, want $want)"; $output | ForEach-Object { Write-Host "  $_" } }
    }

    Expect 0 'PowerShell wrapper help' @('help')
    Expect 0 'PowerShell wrapper status on an empty root' @('-Config', $config, 'status', '-Json')
    Expect 5 'PowerShell wrapper recover -Preview reports no history' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Preview', '-Json')
    Expect 2 'PowerShell wrapper refuses a relative config' @('-Config', 'host-update.json', 'status')
    Remove-Item -LiteralPath $compose
    # -Confirm runs the same-namespace proof before reading any state.
    Expect 3 'namespace proof refuses -Confirm without the compose file' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-Json')
} finally {
    Remove-Item -LiteralPath $work, $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passes) passed, $($script:failures) failed"
if ($script:failures -ne 0) { exit 1 }
