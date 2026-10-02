[CmdletBinding()]
param(
    [string]$Cell = 'c2',
    [string]$WorkDir,
    [string]$Evidence,
    [string]$Cosign,
    [string[]]$Fault = @(),
    [switch]$KeepWork,
    [switch]$Help
)

if ($Help) {
    @'
Usage: pwsh scripts/ci/recovery-matrix/run-cell.ps1 [OPTIONS]

Options:
  -Cell <id|all|faults|imports>  Cell or group to run. Default: c2.
  -WorkDir <path>                Repo-local scratch directory.
  -Evidence <path>               Evidence JSON output path.
  -Cosign <path>                 Cosign executable.
  -Fault <POINT=COMMAND>         Repeatable fault-hook option.
  -KeepWork                      Keep work files and containers on failure.

Live recovery-matrix cells require Ubuntu LTS x64. Windows hosts are unsupported.
'@
    exit 0
}

if (-not $IsLinux) {
    $hostMessage = if ($IsWindows) {
        'Windows hosts are unsupported'
    } else {
        'Only Ubuntu LTS x64 hosts are supported'
    }
    [Console]::Error.WriteLine("$hostMessage; live recovery-matrix cells require Ubuntu LTS x64.")
    exit 2
}

$osRelease = Get-Content -Raw '/etc/os-release'
$osId = [regex]::Match($osRelease, '(?m)^ID="?([^"\n]+)"?$').Groups[1].Value
$osVersion = [regex]::Match($osRelease, '(?m)^VERSION_ID="?([^"\n]+)"?$').Groups[1].Value
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($osId -ne 'ubuntu' -or $osVersion -notin @('22.04', '24.04', '26.04') -or $architecture -ne 'X64') {
    [Console]::Error.WriteLine('Live recovery-matrix cells require Ubuntu LTS x64 (22.04, 24.04 or 26.04).')
    exit 2
}

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$bashScript = Join-Path $PSScriptRoot 'run-cell.sh'
$bashArguments = @(
    $bashScript,
    '--cell', $Cell,
    '--entry-point', 'powershell'
)

if ($WorkDir) {
    $bashArguments += @('--work-dir', $WorkDir)
}
if ($Evidence) {
    $bashArguments += @('--evidence', $Evidence)
}
if ($Cosign) {
    $bashArguments += @('--cosign', $Cosign)
}
foreach ($faultHook in $Fault) {
    $bashArguments += @('--fault', $faultHook)
}
if ($KeepWork) {
    $bashArguments += '--keep-work'
}

if (-not (Get-Command bash -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('bash is required to run the recovery-matrix harness.')
    exit 2
}

Push-Location $repoRoot
try {
    & bash @bashArguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
