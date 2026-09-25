#Requires -Version 7.0
<#
.SYNOPSIS
    Host-local PrintFarmer host-update status/recovery wrapper (issues #2980, #2997).

.DESCRIPTION
    Runs Farm.HostUpdate.Cli without the API. Only three fixed operations are
    exposed; no arbitrary shell text, compose files, or credentials are accepted. This is NOT
    rollout authorization: it never starts a forward update.

      printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-Json]
      printfarmer-host-update.ps1 help

    Environment:
      PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute CLI directory. Optional in a release package, where it
                                       defaults to the package's cli directory beside this script.
      PRINTFARMER_DOTNET               absolute dotnet host; only for a framework-dependent CLI
                                       directory (no Farm.HostUpdate.Cli apphost). Default: dotnet on PATH.

    Grammar and validation match scripts/printfarmer-host-update.sh (option names differ only in
    spelling).

    Exit codes are the CLI's (see docs/HOST_UPDATE_RUNBOOK.md); the wrapper itself only returns 2
    for a usage or setup error, before the CLI runs. Arguments are parsed by the wrapper rather
    than by PowerShell parameter binding, so a usage error never prompts and always exits 2.
    Parameter names are case-insensitive; identifier values are case-sensitive.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleasePattern = '^(stable|insider):[0-9A-Za-z.+-]{1,128}$'
$RequestPattern = '^[A-Za-z0-9._:-]{1,128}$'
$UsageText = @'
usage:
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-Json]
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
$packagedCliDir = Join-Path $PSScriptRoot 'cli'
if ([string]::IsNullOrEmpty($cliDir) -and (Test-Path -LiteralPath $packagedCliDir -PathType Container)) {
    # Release package layout: the wrapper sits beside the self-contained cli directory.
    $cliDir = $packagedCliDir
}

if (-not (Test-FullyQualified $cliDir)) {
    Exit-Usage 'PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory'
}

$cliName = 'Farm.HostUpdate.Cli'
$appHost = Join-Path $cliDir ($IsWindows ? "$cliName.exe" : $cliName)
$launcher = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $appHost -PathType Leaf) {
    # Self-contained package: run the apphost directly; no dotnet install is required.
    if ($env:PRINTFARMER_DOTNET) {
        Exit-Usage 'PRINTFARMER_DOTNET applies only to a framework-dependent CLI directory'
    }

    $launcher.Add($appHost)
}
else {
    $cliDll = Join-Path $cliDir "$cliName.dll"
    if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) {
        Exit-Usage "$cliName not found in the CLI directory"
    }

    $dotnetHost = 'dotnet'
    if ($env:PRINTFARMER_DOTNET) {
        if (-not (Test-FullyQualified $env:PRINTFARMER_DOTNET) -or -not (Test-Path -LiteralPath $env:PRINTFARMER_DOTNET -PathType Leaf)) {
            Exit-Usage 'PRINTFARMER_DOTNET must be an absolute executable path'
        }

        $dotnetHost = $env:PRINTFARMER_DOTNET
    }

    $launcher.Add($dotnetHost)
    $launcher.Add($cliDll)
}

# The CLI re-validates everything, including the canonical release grammar and option combinations.
$cliArgs = [System.Collections.Generic.List[string]]::new()
$cliArgs.Add($command)
if ($null -ne $release) { $cliArgs.Add('--release'); $cliArgs.Add($release) }
if ($null -ne $requestId) { $cliArgs.Add('--request-id'); $cliArgs.Add($requestId) }
if ($preview) { $cliArgs.Add('--preview') }
if ($null -ne $confirm) { $cliArgs.Add('--confirm'); $cliArgs.Add($confirm) }
if ($json) { $cliArgs.Add('--json') }

$launcherArgs = @($launcher | Select-Object -Skip 1)
& $launcher[0] @launcherArgs --config $config @cliArgs
exit $LASTEXITCODE
