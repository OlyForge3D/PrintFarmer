#Requires -Version 7.0
<#
.SYNOPSIS
    Host-local PrintFarmer host-update status/recovery wrapper (issue #2980, first slice).

.DESCRIPTION
    Runs the packaged Farm.HostUpdate.Cli without the API. Only three fixed operations are
    exposed; no arbitrary shell text, compose files, or credentials are accepted. This is NOT
    rollout authorization: it never starts a forward update.

      printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-Json]

    Environment:
      PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute directory containing Farm.HostUpdate.Cli.dll (required)
      PRINTFARMER_DOTNET               absolute path to the dotnet host (optional; default: dotnet on PATH)

    Exit codes are the CLI's (see docs/HOST_UPDATE_RUNBOOK.md); the wrapper itself only returns 2
    for a usage or setup error, before the CLI runs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Config,

    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status', 'recover')]
    [string] $Command,

    [string] $Release,

    [string] $RequestId,

    [switch] $Preview,

    [string] $Confirm,

    [switch] $Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleasePattern = '^(stable|insider):[0-9A-Za-z.+-]{1,128}$'
$RequestPattern = '^[A-Za-z0-9._:-]{1,128}$'

function Exit-Usage([string] $Message) {
    [Console]::Error.WriteLine("printfarmer-host-update: $Message")
    exit 2
}

function Test-FullyQualified([string] $Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and [System.IO.Path]::IsPathFullyQualified($Path)
}

if (-not (Test-FullyQualified $Config) -or -not (Test-Path -LiteralPath $Config -PathType Leaf)) {
    Exit-Usage '-Config must be an existing absolute JSON file path'
}

$cliDir = $env:PRINTFARMER_HOST_UPDATE_CLI_DIR
if (-not (Test-FullyQualified $cliDir)) {
    Exit-Usage 'PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory'
}

$cliDll = Join-Path $cliDir 'Farm.HostUpdate.Cli.dll'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
    Exit-Usage 'Farm.HostUpdate.Cli.dll not found in PRINTFARMER_HOST_UPDATE_CLI_DIR'
}

$dotnetHost = 'dotnet'
if ($env:PRINTFARMER_DOTNET) {
    if (-not (Test-FullyQualified $env:PRINTFARMER_DOTNET) -or -not (Test-Path -LiteralPath $env:PRINTFARMER_DOTNET -PathType Leaf)) {
        Exit-Usage 'PRINTFARMER_DOTNET must be an absolute executable path'
    }

    $dotnetHost = $env:PRINTFARMER_DOTNET
}

# Pre-validate identifiers; the CLI re-validates everything, including option combinations.
$cliArgs = [System.Collections.Generic.List[string]]::new()
$cliArgs.Add($Command)
if ($PSBoundParameters.ContainsKey('Release')) {
    if ($Release -cnotmatch $ReleasePattern) { Exit-Usage '-Release requires a release id like stable:1.2.3' }
    $cliArgs.Add('--release'); $cliArgs.Add($Release)
}

if ($PSBoundParameters.ContainsKey('RequestId')) {
    if ($RequestId -cnotmatch $RequestPattern) { Exit-Usage '-RequestId requires [A-Za-z0-9._:-]{1,128}' }
    $cliArgs.Add('--request-id'); $cliArgs.Add($RequestId)
}

if ($Preview) { $cliArgs.Add('--preview') }

if ($PSBoundParameters.ContainsKey('Confirm')) {
    if ($Confirm -cnotmatch $ReleasePattern) { Exit-Usage '-Confirm requires the release id retyped exactly' }
    $cliArgs.Add('--confirm'); $cliArgs.Add($Confirm)
}

if ($Json) { $cliArgs.Add('--json') }

& $dotnetHost $cliDll --config $Config @cliArgs
exit $LASTEXITCODE
