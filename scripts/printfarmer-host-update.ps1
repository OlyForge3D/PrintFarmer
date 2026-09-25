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
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>] [-Json]
      printfarmer-host-update.ps1 help

    Environment:
      PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute directory containing the CLI (default: cli\ beside an
                                       installed package's wrapper). A self-contained package launcher
                                       (Farm.HostUpdate.Cli.exe) runs directly; otherwise
                                       Farm.HostUpdate.Cli.dll runs on the dotnet host.
      PRINTFARMER_DOTNET               absolute path to the dotnet host (optional; default: dotnet on PATH;
                                       refused for a self-contained package)

    Exit codes are the CLI's (see docs/HOST_UPDATE_RUNBOOK.md); the wrapper itself only returns 2
    for a usage or setup error, before the CLI runs. Arguments are parsed by the wrapper rather
    than by PowerShell parameter binding, so a usage error never prompts and always exits 2.
    Parameter names are case-insensitive; identifier values are case-sensitive.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleasePattern = '^(stable|insider):[0-9A-Za-z.+-]{1,128}$'
$RequestPattern = '^[A-Za-z0-9._:-]{1,128}$'
$DriftTokenPattern = '^drift-[0-9a-f]{32}$'
$PhysicalTokenPattern = '^physical-[0-9a-f]{32}$'
$UsageText = @'
usage:
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>] [-Json]
  printfarmer-host-update.ps1 help
'@

function Exit-Usage([string] $Message) {
    [Console]::Error.WriteLine("printfarmer-host-update: $Message")
    [Console]::Error.WriteLine($UsageText)
    exit 2
}

function Test-FullyQualified([string] $Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and [System.IO.Path]::IsPathFullyQualified($Path)
}

$rawArgs = @($args | ForEach-Object { [string] $_ })

if ($rawArgs.Count -ge 1 -and @('help', '-help', '--help', '-h', '-?') -contains $rawArgs[0].ToLowerInvariant()) {
    [Console]::Out.WriteLine($UsageText)
    exit 0
}

$config = $null
$command = $null
$release = $null
$requestId = $null
$confirm = $null
$reapproveDrift = $null
$printersReconciled = $null
$preview = $false
$json = $false

$index = 0
while ($index -lt $rawArgs.Count) {
    $token = $rawArgs[$index]
    $name = $token.ToLowerInvariant()
    $hasValue = ($index + 1) -lt $rawArgs.Count
    switch -CaseSensitive ($name) {
        '-config' {
            if ($null -ne $config) { Exit-Usage '-Config may only be given once' }
            if (-not $hasValue) { Exit-Usage '-Config requires a value' }
            $config = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-release' {
            if ($null -ne $release) { Exit-Usage '-Release may only be given once' }
            if (-not $hasValue -or $rawArgs[$index + 1] -cnotmatch $ReleasePattern) { Exit-Usage '-Release requires a release id like stable:1.2.3' }
            $release = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-requestid' {
            if ($null -ne $requestId) { Exit-Usage '-RequestId may only be given once' }
            if (-not $hasValue -or $rawArgs[$index + 1] -cnotmatch $RequestPattern) { Exit-Usage '-RequestId requires [A-Za-z0-9._:-]{1,128}' }
            $requestId = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-confirm' {
            if ($null -ne $confirm) { Exit-Usage '-Confirm may only be given once' }
            if (-not $hasValue -or $rawArgs[$index + 1] -cnotmatch $ReleasePattern) { Exit-Usage '-Confirm requires the release id retyped exactly' }
            $confirm = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-reapprovedrift' {
            if ($null -ne $reapproveDrift) { Exit-Usage '-ReapproveDrift may only be given once' }
            if (-not $hasValue -or $rawArgs[$index + 1] -cnotmatch $DriftTokenPattern) { Exit-Usage '-ReapproveDrift requires the drift-<32 hex> token printed by -Preview' }
            $reapproveDrift = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-printersreconciled' {
            if ($null -ne $printersReconciled) { Exit-Usage '-PrintersReconciled may only be given once' }
            if (-not $hasValue -or $rawArgs[$index + 1] -cnotmatch $PhysicalTokenPattern) { Exit-Usage '-PrintersReconciled requires the physical-<32 hex> token printed by -Preview' }
            $printersReconciled = $rawArgs[$index + 1]; $index += 2; continue
        }
        '-preview' {
            if ($preview) { Exit-Usage '-Preview may only be given once' }
            $preview = $true; $index += 1; continue
        }
        '-json' {
            if ($json) { Exit-Usage '-Json may only be given once' }
            $json = $true; $index += 1; continue
        }
        { $_ -ceq 'status' -or $_ -ceq 'recover' } {
            # Commands are matched exactly, like the CLI grammar.
            if ($token -cne $name) { Exit-Usage "unknown command: $token" }
            if ($null -ne $command) { Exit-Usage 'only one command may be given' }
            $command = $token; $index += 1; continue
        }
        default {
            Exit-Usage "unsupported argument: $token"
        }
    }
}

if ($null -eq $config) { Exit-Usage '-Config <absolute-json-path> is required' }
if ($null -eq $command) { Exit-Usage 'missing command (status or recover)' }

# Existence/readability is proven by the CLI (exit 3): Test-Path cannot tell denied from absent.
if (-not (Test-FullyQualified $config)) {
    Exit-Usage '-Config must be an absolute JSON file path'
}

$cliDir = $env:PRINTFARMER_HOST_UPDATE_CLI_DIR
# An installed package (issue #3041) carries its self-contained CLI in cli\ beside this wrapper.
if ([string]::IsNullOrEmpty($cliDir) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'host-update-cli-package.json') -PathType Leaf)) {
    $cliDir = Join-Path $PSScriptRoot 'cli'
}
if (-not (Test-FullyQualified $cliDir)) {
    Exit-Usage 'PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory'
}

$cliAppHost = Join-Path $cliDir ($IsWindows ? 'Farm.HostUpdate.Cli.exe' : 'Farm.HostUpdate.Cli')
$launcherArgs = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $cliAppHost -PathType Leaf) {
    # Self-contained package: the launcher carries its own runtime, so no dotnet host is used.
    if ($env:PRINTFARMER_DOTNET) {
        Exit-Usage 'PRINTFARMER_DOTNET must not be set for a self-contained CLI package'
    }

    $launcher = $cliAppHost
} else {
    $cliDll = Join-Path $cliDir 'Farm.HostUpdate.Cli.dll'
    if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
        Exit-Usage 'Farm.HostUpdate.Cli.dll not found in PRINTFARMER_HOST_UPDATE_CLI_DIR'
    }

    $launcher = 'dotnet'
    if ($env:PRINTFARMER_DOTNET) {
        if (-not (Test-FullyQualified $env:PRINTFARMER_DOTNET) -or -not (Test-Path -LiteralPath $env:PRINTFARMER_DOTNET -PathType Leaf)) {
            Exit-Usage 'PRINTFARMER_DOTNET must be an absolute executable path'
        }

        $launcher = $env:PRINTFARMER_DOTNET
    }

    $launcherArgs.Add($cliDll)
}

# The CLI re-validates everything, including the canonical release grammar and option combinations.
$cliArgs = [System.Collections.Generic.List[string]]::new()
$cliArgs.Add($command)
if ($null -ne $release) { $cliArgs.Add('--release'); $cliArgs.Add($release) }
if ($null -ne $requestId) { $cliArgs.Add('--request-id'); $cliArgs.Add($requestId) }
if ($preview) { $cliArgs.Add('--preview') }
if ($null -ne $confirm) { $cliArgs.Add('--confirm'); $cliArgs.Add($confirm) }
if ($null -ne $reapproveDrift) { $cliArgs.Add('--reapprove-drift'); $cliArgs.Add($reapproveDrift) }
if ($null -ne $printersReconciled) { $cliArgs.Add('--printers-reconciled'); $cliArgs.Add($printersReconciled) }
if ($json) { $cliArgs.Add('--json') }

& $launcher @launcherArgs --config $config @cliArgs
exit $LASTEXITCODE
