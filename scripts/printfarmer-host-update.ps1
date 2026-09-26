#Requires -Version 7.0
<#
.SYNOPSIS
    Host-local PrintFarmer host-update status/recovery wrapper (issue #2980, first slice).

.DESCRIPTION
    Runs the packaged Farm.HostUpdate.Cli without the API. Only fixed operations are
    exposed; no arbitrary shell text, compose files, or credentials are accepted. This is NOT
    rollout authorization: it never starts a forward update.

      printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
      printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>] [-Json]
      printfarmer-host-update.ps1 import -Config C:\abs\host-update.json -Bundle C:\abs\bundle.tar -Channel <stable|insider> -Version <v> -TrustedRoot C:\abs\trusted_root.json -TrustedRootApproval C:\abs\approval.json -Staging C:\abs\new-dir -Records C:\abs\records-dir -Operator <id> [-PriorRecoverySet C:\abs\dir] [-ProtectedBackup C:\abs\reference.json]
      printfarmer-host-update.ps1 activate -Config C:\abs\host-update.json -Staging C:\abs\verified-staging -Channel <stable|insider> -TrustedRoot C:\abs\trusted_root.json [-Cosign C:\abs\cosign.exe] [-Json]
      printfarmer-host-update.ps1 recover-offline -Config C:\abs\host-update.json -Staging C:\abs\verified-staging -Channel <stable|insider> -TrustedRoot C:\abs\trusted_root.json -ProtectedBackup C:\abs\reference.json -Release <id> [-RequestId <id>] (-Preview | -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>]) [-Cosign C:\abs\cosign.exe] [-Json]
      printfarmer-host-update.ps1 help

    `import` (issue #3063) verifies a signed offline update bundle without network access, loads
    only its verified images into the local Docker engine and writes one durable, redacted
    decision record under -Records. Issue #3064: it also enforces the offline trust expiry policy
    (-TrustedRootApproval) and records the release in the host's durable replay store through the CLI
    (offline-admit, using -Config), refusing replays, downgrades and cross-channel imports. It never
    authorizes a rollout.

    `recover-offline` (issue #3082) recovers a failed offline activation to the staged bundle's
    signature-verified prior recovery set with no network access; -ProtectedBackup must equal the
    reference bound at import. It refuses registry-mode requests and mismatched host state.

    Environment:
      PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute directory containing the CLI (default: cli\ beside an
                                       installed package's wrapper). A self-contained package launcher
                                       (Farm.HostUpdate.Cli.exe) runs directly; otherwise
                                       Farm.HostUpdate.Cli.dll runs on the dotnet host.
      PRINTFARMER_DOTNET               absolute path to the dotnet host (optional; default: dotnet on PATH;
                                       refused for a self-contained package)
      PRINTFARMER_NODE                 import only: absolute path to node (optional; default: node on PATH)
      PRINTFARMER_OFFLINE_BUNDLE_TOOL  import only: absolute path to offline-update-bundle.mjs (default:
                                       ci\offline-update-bundle.mjs beside this wrapper in a repository checkout)
      PRINTFARMER_COSIGN               import only: absolute path to cosign (optional; default: cosign on PATH)
      PRINTFARMER_DOCKER               import only: absolute path to docker (optional; default: docker on PATH)

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
$ChannelPattern = '^(stable|insider)$'
$VersionPattern = '^[0-9A-Za-z.+-]{1,128}$'
$OperatorPattern = '^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$'
$UsageText = @'
usage:
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json status [-Release <id>] [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Preview [-Json]
  printfarmer-host-update.ps1 -Config C:\abs\host-update.json recover -Release <id> [-RequestId <id>] -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>] [-Json]
  printfarmer-host-update.ps1 import -Config C:\abs\host-update.json -Bundle C:\abs\bundle.tar -Channel <stable|insider> -Version <v> -TrustedRoot C:\abs\trusted_root.json -TrustedRootApproval C:\abs\approval.json -Staging C:\abs\new-dir -Records C:\abs\records-dir -Operator <id> [-PriorRecoverySet C:\abs\dir] [-ProtectedBackup C:\abs\reference.json]
  printfarmer-host-update.ps1 activate -Config C:\abs\host-update.json -Staging C:\abs\verified-staging -Channel <stable|insider> -TrustedRoot C:\abs\trusted_root.json [-Cosign C:\abs\cosign.exe] [-Json]
  printfarmer-host-update.ps1 recover-offline -Config C:\abs\host-update.json -Staging C:\abs\verified-staging -Channel <stable|insider> -TrustedRoot C:\abs\trusted_root.json -ProtectedBackup C:\abs\reference.json -Release <id> [-RequestId <id>] (-Preview | -Confirm <id> [-ReapproveDrift <token>] [-PrintersReconciled <token>]) [-Cosign C:\abs\cosign.exe] [-Json]
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

function Resolve-OptionalExecutable([string] $Variable, [string] $Default) {
    $value = [Environment]::GetEnvironmentVariable($Variable)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    if (-not (Test-FullyQualified $value) -or -not (Test-Path -LiteralPath $value -PathType Leaf)) {
        Exit-Usage "$Variable must be an absolute executable path"
    }
    return $value
}

# Resolves the CLI: the self-contained apphost alone (Dll = $null), or the dotnet host plus
# Farm.HostUpdate.Cli.dll.
function Resolve-Launcher {
    $cliDir = $env:PRINTFARMER_HOST_UPDATE_CLI_DIR
    # An installed package (issue #3041) carries its self-contained CLI in cli\ beside this wrapper.
    if ([string]::IsNullOrEmpty($cliDir) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'host-update-cli-package.json') -PathType Leaf)) {
        $cliDir = Join-Path $PSScriptRoot 'cli'
    }
    if (-not (Test-FullyQualified $cliDir)) {
        Exit-Usage 'PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory'
    }

    $cliAppHost = Join-Path $cliDir ($IsWindows ? 'Farm.HostUpdate.Cli.exe' : 'Farm.HostUpdate.Cli')
    if (Test-Path -LiteralPath $cliAppHost -PathType Leaf) {
        # Self-contained package: the launcher carries its own runtime, so no dotnet host is used.
        if ($env:PRINTFARMER_DOTNET) {
            Exit-Usage 'PRINTFARMER_DOTNET must not be set for a self-contained CLI package'
        }

        return @{ Launcher = $cliAppHost; Dll = $null }
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

    return @{ Launcher = $dotnetHost; Dll = $cliDll }
}

if ($rawArgs.Count -ge 1 -and $rawArgs[0] -ceq 'import') {    # Issues #3063/#3064: host-local offline bundle import with replay admission. Never authorizes a rollout.
    $importOptions = [ordered]@{
        '-bundle' = @{ Flag = '--bundle'; Kind = 'path' }
        '-channel' = @{ Flag = '--channel'; Kind = 'channel' }
        '-version' = @{ Flag = '--version'; Kind = 'version' }
        '-trustedroot' = @{ Flag = '--trusted-root'; Kind = 'path' }
        '-trustedrootapproval' = @{ Flag = '--trusted-root-approval'; Kind = 'path' }
        '-staging' = @{ Flag = '--staging'; Kind = 'path' }
        '-records' = @{ Flag = '--records'; Kind = 'path' }
        '-operator' = @{ Flag = '--operator'; Kind = 'operator' }
        '-config' = @{ Flag = '--config'; Kind = 'path' }
        '-priorrecoveryset' = @{ Flag = '--prior-recovery-set'; Kind = 'path' }
        '-protectedbackup' = @{ Flag = '--protected-backup'; Kind = 'path' }
    }
    $importValues = @{}
    $index = 1
    while ($index -lt $rawArgs.Count) {
        $token = $rawArgs[$index]
        $name = $token.ToLowerInvariant()
        if (-not $importOptions.Contains($name)) { Exit-Usage "unsupported argument: $token" }
        $spec = $importOptions[$name]
        if ($importValues.ContainsKey($name)) { Exit-Usage "$token may only be given once" }
        if (($index + 1) -ge $rawArgs.Count -or [string]::IsNullOrEmpty($rawArgs[$index + 1])) { Exit-Usage "$token requires a value" }
        $value = $rawArgs[$index + 1]
        switch ($spec.Kind) {
            'channel' { if ($value -cnotmatch $ChannelPattern) { Exit-Usage '-Channel must be stable or insider' } }
            'version' { if ($value -cnotmatch $VersionPattern) { Exit-Usage '-Version requires [0-9A-Za-z.+-]{1,128}' } }
            'operator' { if ($value -cnotmatch $OperatorPattern) { Exit-Usage '-Operator requires [A-Za-z0-9][A-Za-z0-9._@-]{0,63}' } }
            'path' { if (-not (Test-FullyQualified $value)) { Exit-Usage "$token must be an absolute path" } }
        }
        $importValues[$name] = $value
        $index += 2
    }
    foreach ($requiredName in @('-config', '-bundle', '-channel', '-version', '-trustedroot', '-trustedrootapproval', '-staging', '-records', '-operator')) {
        if (-not $importValues.ContainsKey($requiredName)) {
            $display = @{ '-bundle' = '-Bundle'; '-channel' = '-Channel'; '-version' = '-Version'; '-trustedroot' = '-TrustedRoot'
                '-trustedrootapproval' = '-TrustedRootApproval'; '-config' = '-Config'
                '-staging' = '-Staging'; '-records' = '-Records'; '-operator' = '-Operator' }[$requiredName]
            Exit-Usage "import requires $display"
        }
    }

    $nodeHost = Resolve-OptionalExecutable 'PRINTFARMER_NODE' 'node'
    $cosignHost = Resolve-OptionalExecutable 'PRINTFARMER_COSIGN' 'cosign'
    $dockerHost = Resolve-OptionalExecutable 'PRINTFARMER_DOCKER' 'docker'
    $tool = $env:PRINTFARMER_OFFLINE_BUNDLE_TOOL
    if ([string]::IsNullOrEmpty($tool)) { $tool = Join-Path $PSScriptRoot 'ci' 'offline-update-bundle.mjs' }
    if (-not (Test-FullyQualified $tool) -or -not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        Exit-Usage 'PRINTFARMER_OFFLINE_BUNDLE_TOOL must be an absolute path to offline-update-bundle.mjs'
    }

    $resolved = Resolve-Launcher
    $toolArgs = [System.Collections.Generic.List[string]]::new()
    $toolArgs.Add('import')
    foreach ($key in $importOptions.Keys) {
        if ($key -in @('-priorrecoveryset', '-protectedbackup')) { continue }
        $toolArgs.Add($importOptions[$key].Flag); $toolArgs.Add($importValues[$key])
    }
    $toolArgs.Add('--host-update-cli'); $toolArgs.Add(($null -ne $resolved.Dll) ? $resolved.Dll : $resolved.Launcher)
    if ($null -ne $resolved.Dll -and $env:PRINTFARMER_DOTNET) { $toolArgs.Add('--dotnet'); $toolArgs.Add($resolved.Launcher) }
    foreach ($key in @('-priorrecoveryset', '-protectedbackup')) {
        if ($importValues.ContainsKey($key)) { $toolArgs.Add($importOptions[$key].Flag); $toolArgs.Add($importValues[$key]) }
    }
    if ($env:PRINTFARMER_COSIGN) { $toolArgs.Add('--cosign'); $toolArgs.Add($cosignHost) }
    if ($env:PRINTFARMER_DOCKER) { $toolArgs.Add('--docker'); $toolArgs.Add($dockerHost) }

    & $nodeHost $tool @toolArgs
    exit $LASTEXITCODE
}

if ($rawArgs.Count -ge 1 -and $rawArgs[0] -ceq 'activate') {
    $activateOptions = [ordered]@{
        '-config' = @{ Flag = '--config'; Kind = 'path' }
        '-staging' = @{ Flag = '--staging'; Kind = 'path' }
        '-channel' = @{ Flag = '--channel'; Kind = 'channel' }
        '-trustedroot' = @{ Flag = '--trusted-root'; Kind = 'path' }
        '-cosign' = @{ Flag = '--cosign'; Kind = 'path' }
    }
    $activateValues = @{}
    $activateJson = $false
    $index = 1
    while ($index -lt $rawArgs.Count) {
        $token = $rawArgs[$index]
        $name = $token.ToLowerInvariant()
        if ($name -eq '-json') {
            if ($activateJson) { Exit-Usage '-Json may only be given once' }
            $activateJson = $true; $index += 1; continue
        }

        if (-not $activateOptions.Contains($name)) { Exit-Usage "unsupported argument: $token" }
        $spec = $activateOptions[$name]
        if ($activateValues.ContainsKey($name)) { Exit-Usage "$token may only be given once" }
        if (($index + 1) -ge $rawArgs.Count -or [string]::IsNullOrEmpty($rawArgs[$index + 1])) { Exit-Usage "$token requires a value" }
        $value = $rawArgs[$index + 1]
        switch ($spec.Kind) {
            'channel' { if ($value -cnotmatch $ChannelPattern) { Exit-Usage '-Channel must be stable or insider' } }
            'path' { if (-not (Test-FullyQualified $value)) { Exit-Usage "$token must be an absolute path" } }
        }
        $activateValues[$name] = $value
        $index += 2
    }
    foreach ($requiredName in @('-config', '-staging', '-channel', '-trustedroot')) {
        if (-not $activateValues.ContainsKey($requiredName)) {
            $display = @{ '-config' = '-Config'; '-staging' = '-Staging'; '-channel' = '-Channel'; '-trustedroot' = '-TrustedRoot' }[$requiredName]
            Exit-Usage "activate requires $display"
        }
    }

    $resolved = Resolve-Launcher
    $launcher = $resolved.Launcher
    $launcherArgs = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $resolved.Dll) { $launcherArgs.Add($resolved.Dll) }
    $cliArgs = [System.Collections.Generic.List[string]]::new()
    $cliArgs.Add('offline-activate')
    foreach ($key in @('-staging', '-channel', '-trustedroot', '-cosign')) {
        if ($activateValues.ContainsKey($key)) { $cliArgs.Add($activateOptions[$key].Flag); $cliArgs.Add($activateValues[$key]) }
    }
    if ($activateJson) { $cliArgs.Add('--json') }

    & $launcher @launcherArgs --config $activateValues['-config'] @cliArgs
    exit $LASTEXITCODE
}

if ($rawArgs.Count -ge 1 -and $rawArgs[0] -ceq 'recover-offline') {
    # Issue #3082: recover a failed offline activation to the staged, verified prior recovery set.
    $recoverOptions = [ordered]@{
        '-config' = @{ Flag = '--config'; Kind = 'path' }
        '-staging' = @{ Flag = '--staging'; Kind = 'path' }
        '-channel' = @{ Flag = '--channel'; Kind = 'channel' }
        '-trustedroot' = @{ Flag = '--trusted-root'; Kind = 'path' }
        '-cosign' = @{ Flag = '--cosign'; Kind = 'path' }
        '-protectedbackup' = @{ Flag = '--protected-backup'; Kind = 'path' }
        '-release' = @{ Flag = '--release'; Kind = 'release' }
        '-requestid' = @{ Flag = '--request-id'; Kind = 'request' }
        '-confirm' = @{ Flag = '--confirm'; Kind = 'release' }
        '-reapprovedrift' = @{ Flag = '--reapprove-drift'; Kind = 'drift' }
        '-printersreconciled' = @{ Flag = '--printers-reconciled'; Kind = 'physical' }
    }
    $recoverValues = @{}
    $recoverPreview = $false
    $recoverJson = $false
    $index = 1
    while ($index -lt $rawArgs.Count) {
        $token = $rawArgs[$index]
        $name = $token.ToLowerInvariant()
        if ($name -eq '-json') {
            if ($recoverJson) { Exit-Usage '-Json may only be given once' }
            $recoverJson = $true; $index += 1; continue
        }

        if ($name -eq '-preview') {
            if ($recoverPreview) { Exit-Usage '-Preview may only be given once' }
            $recoverPreview = $true; $index += 1; continue
        }

        if (-not $recoverOptions.Contains($name)) { Exit-Usage "unsupported argument: $token" }
        $spec = $recoverOptions[$name]
        if ($recoverValues.ContainsKey($name)) { Exit-Usage "$token may only be given once" }
        if (($index + 1) -ge $rawArgs.Count -or [string]::IsNullOrEmpty($rawArgs[$index + 1])) { Exit-Usage "$token requires a value" }
        $value = $rawArgs[$index + 1]
        switch ($spec.Kind) {
            'channel' { if ($value -cnotmatch $ChannelPattern) { Exit-Usage '-Channel must be stable or insider' } }
            'release' { if ($value -cnotmatch $ReleasePattern) { Exit-Usage "$token requires a release id like stable:1.2.3" } }
            'request' { if ($value -cnotmatch $RequestPattern) { Exit-Usage '-RequestId requires [A-Za-z0-9._:-]{1,128}' } }
            'drift' { if ($value -cnotmatch $DriftTokenPattern) { Exit-Usage '-ReapproveDrift requires the drift-<32 hex> token printed by -Preview' } }
            'physical' { if ($value -cnotmatch $PhysicalTokenPattern) { Exit-Usage '-PrintersReconciled requires the physical-<32 hex> token printed by -Preview' } }
            'path' { if (-not (Test-FullyQualified $value)) { Exit-Usage "$token must be an absolute path" } }
        }
        $recoverValues[$name] = $value
        $index += 2
    }
    foreach ($requiredName in @('-config', '-staging', '-channel', '-trustedroot', '-protectedbackup', '-release')) {
        if (-not $recoverValues.ContainsKey($requiredName)) {
            $display = @{ '-config' = '-Config'; '-staging' = '-Staging'; '-channel' = '-Channel'; '-trustedroot' = '-TrustedRoot'
                '-protectedbackup' = '-ProtectedBackup'; '-release' = '-Release' }[$requiredName]
            Exit-Usage "recover-offline requires $display"
        }
    }

    $resolved = Resolve-Launcher
    $launcher = $resolved.Launcher
    $launcherArgs = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $resolved.Dll) { $launcherArgs.Add($resolved.Dll) }
    # Canonical argument order, identical to the bash wrapper.
    $cliArgs = [System.Collections.Generic.List[string]]::new()
    $cliArgs.Add('offline-recover')
    foreach ($key in @('-staging', '-channel', '-trustedroot', '-cosign', '-protectedbackup', '-release', '-requestid')) {
        if ($recoverValues.ContainsKey($key)) { $cliArgs.Add($recoverOptions[$key].Flag); $cliArgs.Add($recoverValues[$key]) }
    }
    if ($recoverPreview) { $cliArgs.Add('--preview') }
    foreach ($key in @('-confirm', '-reapprovedrift', '-printersreconciled')) {
        if ($recoverValues.ContainsKey($key)) { $cliArgs.Add($recoverOptions[$key].Flag); $cliArgs.Add($recoverValues[$key]) }
    }
    if ($recoverJson) { $cliArgs.Add('--json') }

    & $launcher @launcherArgs --config $recoverValues['-config'] @cliArgs
    exit $LASTEXITCODE
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

$resolved = Resolve-Launcher
$launcher = $resolved.Launcher
$launcherArgs = [System.Collections.Generic.List[string]]::new()
if ($null -ne $resolved.Dll) { $launcherArgs.Add($resolved.Dll) }

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
