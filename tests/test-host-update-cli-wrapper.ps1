#Requires -Version 7.0
# Focused coverage for scripts/printfarmer-host-update.ps1 (issue #2980): every usage error must
# exit 2 without prompting and without invoking the CLI; accepted arguments pass through verbatim
# and the CLI's exit code is preserved. Each case runs the wrapper in a child
# `pwsh -NonInteractive -File` so a parameter-binding prompt would surface as a failure.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$wrapper = Join-Path $repoRoot 'scripts/printfarmer-host-update.ps1'
$pwshPath = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("printfarmer-host-update-ps-wrapper-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $cliDir = Join-Path $testRoot 'cli'
    New-Item -ItemType Directory -Path $cliDir | Out-Null
    $dll = Join-Path $cliDir 'Farm.HostUpdate.Cli.dll'
    Set-Content -LiteralPath $dll -Value '' -NoNewline
    $config = Join-Path $testRoot 'host-update.json'
    Set-Content -LiteralPath $config -Value '{}'
    $argsLog = Join-Path $testRoot 'args.log'
    $fakeDotnet = Join-Path $testRoot 'fake-dotnet.ps1'
    Set-Content -LiteralPath $fakeDotnet -Value @"
Set-Content -LiteralPath '$argsLog' -Value (`$args -join "``n") -NoNewline
exit [int](`$env:FAKE_EXIT ?? '0')
"@

    $script:failures = 0
    function Pass([string] $Name) { Write-Host "[PASS] $Name" }
    function Fail([string] $Name) { Write-Host "[FAIL] $Name"; $script:failures++ }

    function Invoke-Wrapper([string[]] $WrapperArgs, [hashtable] $Environment = @{}) {
        Remove-Item -LiteralPath $argsLog -ErrorAction SilentlyContinue
        $saved = @{}
        $defaults = @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $cliDir; PRINTFARMER_DOTNET = $fakeDotnet; FAKE_EXIT = $null }
        foreach ($key in $Environment.Keys) { $defaults[$key] = $Environment[$key] }
        foreach ($key in $defaults.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $defaults[$key])
        }

        try {
            $stdout = & $pwshPath -NoProfile -NonInteractive -File $wrapper @WrapperArgs 2>$null
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Stdout = ($stdout -join "`n"); Invoked = (Test-Path -LiteralPath $argsLog) }
        }
        finally {
            foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        }
    }

    function Expect-Usage([string] $Name, [string[]] $WrapperArgs, [hashtable] $Environment = @{}) {
        $run = Invoke-Wrapper $WrapperArgs $Environment
        if ($run.ExitCode -eq 2 -and -not $run.Invoked -and [string]::IsNullOrEmpty($run.Stdout)) { Pass $Name }
        else { Fail "$Name (exit $($run.ExitCode), cli invoked: $($run.Invoked))" }
    }

    function Expect-Passthrough([string] $Name, [string[]] $Expected, [string[]] $WrapperArgs, [hashtable] $Environment = @{}) {
        $run = Invoke-Wrapper $WrapperArgs $Environment
        $actual = if ($run.Invoked) { Get-Content -LiteralPath $argsLog -Raw } else { '' }
        if ($run.ExitCode -eq 0 -and $run.Invoked -and $actual -ceq ($Expected -join "`n")) { Pass $Name }
        else { Fail "$Name (exit $($run.ExitCode), args: $actual)" }
    }

    Expect-Passthrough 'status passes through' @($dll, '--config', $config, 'status', '--json') @('-Config', $config, 'status', '-Json')
    Expect-Passthrough 'status detail passes through' @($dll, '--config', $config, 'status', '--release', 'stable:1.2.3') @('-Config', $config, 'status', '-Release', 'stable:1.2.3')
    Expect-Passthrough 'recover preview passes through' @($dll, '--config', $config, 'recover', '--release', 'insider:1.2.3-rc.1', '--request-id', 'req-1', '--preview') @('-Config', $config, 'recover', '-Release', 'insider:1.2.3-rc.1', '-RequestId', 'req-1', '-Preview')
    Expect-Passthrough 'recover confirm passes through' @($dll, '--config', $config, 'recover', '--release', 'stable:1.2.3', '--confirm', 'stable:1.2.3', '--json') @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-Json')
    Expect-Passthrough 'parameter names are case-insensitive' @($dll, '--config', $config, 'status', '--json') @('-config', $config, 'status', '-JSON')
    Expect-Passthrough 'recover confirm with drift reapproval passes through' @($dll, '--config', $config, 'recover', '--release', 'stable:1.2.3', '--confirm', 'stable:1.2.3', '--reapprove-drift', 'drift-0123456789abcdef0123456789abcdef') @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-ReapproveDrift', 'drift-0123456789abcdef0123456789abcdef')
    Expect-Usage 'malformed drift token refused' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-ReapproveDrift', 'DRIFT-0123456789ABCDEF0123456789ABCDEF')
    Expect-Usage 'duplicate drift token refused' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-ReapproveDrift', 'drift-0123456789abcdef0123456789abcdef', '-ReapproveDrift', 'drift-0123456789abcdef0123456789abcdef')
    Expect-Passthrough 'recover confirm with physical reconciliation passes through' @($dll, '--config', $config, 'recover', '--release', 'stable:1.2.3', '--confirm', 'stable:1.2.3', '--printers-reconciled', 'physical-0123456789abcdef0123456789abcdef') @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-PrintersReconciled', 'physical-0123456789abcdef0123456789abcdef')
    Expect-Usage 'malformed physical token refused' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-PrintersReconciled', 'PHYSICAL-0123456789ABCDEF0123456789ABCDEF')
    Expect-Usage 'duplicate physical token refused' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-PrintersReconciled', 'physical-0123456789abcdef0123456789abcdef', '-PrintersReconciled', 'physical-0123456789abcdef0123456789abcdef')

    Expect-Usage 'missing -Config refused without prompting' @('status')
    Expect-Usage 'no arguments refused without prompting' @()
    Expect-Usage 'missing command refused' @('-Config', $config)
    Expect-Usage 'relative -Config refused' @('-Config', 'host-update.json', 'status')
    $missingConfig = Join-Path $testRoot 'missing.json'
    Expect-Passthrough 'missing config file is left to the CLI (exit 3)' @($dll, '--config', $missingConfig, 'status') @('-Config', $missingConfig, 'status')
    Expect-Usage 'unknown command refused' @('-Config', $config, 'apply')
    Expect-Usage 'miscased command refused' @('-Config', $config, 'Status')
    Expect-Usage 'duplicate command refused' @('-Config', $config, 'status', 'status')
    Expect-Usage 'unknown parameter refused' @('-Config', $config, 'status', '-ComposeFile', 'x.yml')
    Expect-Usage 'bash-style option refused' @('-Config', $config, 'status', '--json')
    Expect-Usage 'duplicate parameter refused' @('-Config', $config, 'status', '-Json', '-Json')
    Expect-Usage 'path-like release refused' @('-Config', $config, 'status', '-Release', 'stable:../../etc')
    Expect-Usage 'miscased release refused' @('-Config', $config, 'status', '-Release', 'Stable:1.2.3')
    Expect-Usage 'shell metacharacters refused' @('-Config', $config, 'recover', '-Release', 'stable:1;rm', '-Preview')
    Expect-Usage 'invalid request id refused' @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-RequestId', 'a b', '-Preview')
    Expect-Usage 'missing option value refused' @('-Config', $config, 'recover', '-Release')
    Expect-Usage 'missing CLI dir refused' @('-Config', $config, 'status') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $null }
    Expect-Usage 'relative PRINTFARMER_DOTNET refused' @('-Config', $config, 'status') @{ PRINTFARMER_DOTNET = 'fake-dotnet.ps1' }

    $help = Invoke-Wrapper @('help') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $null }
    if ($help.ExitCode -eq 0 -and -not $help.Invoked -and $help.Stdout -match 'usage:') { Pass 'help works without -Config or CLI dir' }
    else { Fail "help works without -Config or CLI dir (exit $($help.ExitCode))" }

    $exitRun = Invoke-Wrapper @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3') @{ FAKE_EXIT = '11' }
    if ($exitRun.ExitCode -eq 11) { Pass 'CLI exit code preserved' } else { Fail "CLI exit code preserved (exit $($exitRun.ExitCode))" }

    # Issue #3041: a self-contained package launcher runs directly, without a dotnet host.
    $appHostDir = Join-Path $testRoot 'apphost-cli'
    New-Item -ItemType Directory -Path $appHostDir | Out-Null
    $appHostName = $IsWindows ? 'Farm.HostUpdate.Cli.exe' : 'Farm.HostUpdate.Cli'
    $appHost = Join-Path $appHostDir $appHostName
    if ($IsWindows) {
        # A placeholder is enough: the refusal below happens before anything is launched.
        Set-Content -LiteralPath $appHost -Value '' -NoNewline
    } else {
        Set-Content -LiteralPath $appHost -Value "#!/bin/sh`nprintf '%s\n' `"`$@`" > '$argsLog'`nexit `${FAKE_EXIT:-0}`n" -NoNewline
        chmod +x $appHost
        $appRun = Invoke-Wrapper @('-Config', $config, 'status', '-Json') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $appHostDir; PRINTFARMER_DOTNET = $null; FAKE_EXIT = '10' }
        $appArgs = if ($appRun.Invoked) { (Get-Content -LiteralPath $argsLog -Raw).TrimEnd("`n") } else { '' }
        if ($appRun.ExitCode -eq 10 -and $appArgs -ceq (@('--config', $config, 'status', '--json') -join "`n")) { Pass 'self-contained launcher runs directly and preserves its exit code' }
        else { Fail "self-contained launcher runs directly (exit $($appRun.ExitCode), args: $appArgs)" }
    }

    Expect-Usage 'PRINTFARMER_DOTNET refused for a self-contained launcher' @('-Config', $config, 'status') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $appHostDir }

    # An installed package resolves its CLI from cli\ beside the wrapper when no directory is given.
    $packageDir = Join-Path $testRoot 'package'
    New-Item -ItemType Directory -Path (Join-Path $packageDir 'cli') -Force | Out-Null
    Copy-Item -LiteralPath $wrapper -Destination $packageDir
    Set-Content -LiteralPath (Join-Path $packageDir 'host-update-cli-package.json') -Value '{}'
    $packageDll = Join-Path $packageDir 'cli' 'Farm.HostUpdate.Cli.dll'
    Set-Content -LiteralPath $packageDll -Value '' -NoNewline
    $packagedWrapper = Join-Path $packageDir 'printfarmer-host-update.ps1'
    $savedWrapper = $wrapper
    try {
        $wrapper = $packagedWrapper
        Expect-Passthrough 'installed package resolves cli beside the wrapper' @($packageDll, '--config', $config, 'status') @('-Config', $config, 'status') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $null }
        Remove-Item -LiteralPath (Join-Path $packageDir 'host-update-cli-package.json')
        Expect-Usage 'no package marker means no default CLI dir' @('-Config', $config, 'status') @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $null }
    }
    finally {
        $wrapper = $savedWrapper
    }

    # Issues #3063/#3064: host-local offline bundle import. It runs the offline bundle tool on node with
    # a fixed, pre-validated argument vector identical to the Bash wrapper's (asserted by
    # tests/test-host-update-cli-wrapper.sh) — the parity contract. -Config and the CLI are forwarded
    # so the tool can record the release in the durable replay store (offline-admit).
    $fakeNode = Join-Path $testRoot 'fake-node.ps1'
    Copy-Item -LiteralPath $fakeDotnet -Destination $fakeNode
    $fakeCosign = Join-Path $testRoot 'fake-cosign.ps1'
    Copy-Item -LiteralPath $fakeDotnet -Destination $fakeCosign
    $fakeTool = Join-Path $testRoot 'offline-update-bundle.mjs'
    Set-Content -LiteralPath $fakeTool -Value '' -NoNewline
    $bundle = Join-Path $testRoot 'printfarmer-offline-update.tar'
    $rootJson = Join-Path $testRoot 'trusted_root.json'
    $approval = Join-Path $testRoot 'trusted-root-approval.json'
    $staging = Join-Path $testRoot 'staging'
    $records = Join-Path $testRoot 'records'
    $prior = Join-Path $testRoot 'prior'
    $backup = Join-Path $testRoot 'backup.json'
    $importEnv = @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $cliDir; PRINTFARMER_DOTNET = $null; PRINTFARMER_NODE = $fakeNode
        PRINTFARMER_OFFLINE_BUNDLE_TOOL = $fakeTool; PRINTFARMER_COSIGN = $null; PRINTFARMER_DOCKER = $null }
    function With-Env([hashtable] $Overrides) {
        $merged = @{}
        foreach ($key in $importEnv.Keys) { $merged[$key] = $importEnv[$key] }
        foreach ($key in $Overrides.Keys) { $merged[$key] = $Overrides[$key] }
        return $merged
    }
    $importBase = @('import', '-Config', $config, '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3', '-TrustedRoot', $rootJson,
        '-TrustedRootApproval', $approval, '-Staging', $staging, '-Records', $records, '-Operator', 'ops.alice@site-1')
    function Import-Vector([string] $Channel, [string] $Version, [string] $Operator, [string[]] $Tail = @()) {
        return @($fakeTool, 'import', '--bundle', $bundle, '--channel', $Channel, '--version', $Version, '--trusted-root', $rootJson,
            '--trusted-root-approval', $approval, '--staging', $staging, '--records', $records, '--operator', $Operator,
            '--config', $config, '--host-update-cli', $dll) + $Tail
    }

    Expect-Passthrough 'import passes a fixed argument vector to the bundle tool' (Import-Vector 'stable' '1.2.3' 'ops.alice@site-1') $importBase $importEnv
    Expect-Passthrough 'import options are normalised to a fixed order' (Import-Vector 'insider' '1.2.3-rc.1' 'ops' @('--prior-recovery-set', $prior,
        '--protected-backup', $backup)) @('import', '-ProtectedBackup', $backup, '-TrustedRootApproval', $approval,
        '-Operator', 'ops', '-Records', $records, '-PriorRecoverySet', $prior, '-Staging', $staging, '-TrustedRoot', $rootJson,
        '-Version', '1.2.3-rc.1', '-Channel', 'insider', '-Bundle', $bundle, '-Config', $config) $importEnv
    Expect-Passthrough 'import parameter names are case-insensitive' (Import-Vector 'stable' '1.2.3' 'ops') @('import', '-config', $config,
        '-bundle', $bundle, '-CHANNEL', 'stable', '-version', '1.2.3', '-trustedroot', $rootJson, '-TRUSTEDROOTAPPROVAL', $approval,
        '-staging', $staging, '-records', $records, '-operator', 'ops') $importEnv
    Expect-Passthrough 'import forwards an explicit dotnet host for a framework-dependent CLI' (Import-Vector 'stable' '1.2.3' 'ops.alice@site-1' @('--dotnet',
        $fakeDotnet)) $importBase (With-Env @{ PRINTFARMER_DOTNET = $fakeDotnet })

    $cosignRun = Invoke-Wrapper $importBase (With-Env @{ PRINTFARMER_COSIGN = $fakeCosign })
    $cosignArgs = if ($cosignRun.Invoked) { @((Get-Content -LiteralPath $argsLog -Raw) -split "`n") } else { @() }
    if ($cosignRun.ExitCode -eq 0 -and $cosignArgs.Count -ge 2 -and $cosignArgs[-2] -ceq '--cosign' -and $cosignArgs[-1] -ceq $fakeCosign) {
        Pass 'import forwards an absolute PRINTFARMER_COSIGN'
    } else { Fail "import forwards an absolute PRINTFARMER_COSIGN (exit $($cosignRun.ExitCode))" }

    $refusedRun = Invoke-Wrapper $importBase (With-Env @{ FAKE_EXIT = '1' })
    if ($refusedRun.ExitCode -eq 1 -and $refusedRun.Invoked) { Pass 'import preserves the refused exit code' }
    else { Fail "import preserves the refused exit code (exit $($refusedRun.ExitCode))" }

    $withoutConfig = @($importBase | Select-Object -Skip 3); $withoutConfig = @('import') + $withoutConfig
    Expect-Usage 'import requires -Config' $withoutConfig $importEnv
    Expect-Usage 'import refuses a relative -Config' (@('import', '-Config', 'host-update.json') + $withoutConfig[1..($withoutConfig.Count - 1)]) $importEnv
    Expect-Usage 'import refuses a duplicate -Config' ($importBase + @('-Config', $config)) $importEnv
    Expect-Usage 'import requires -TrustedRootApproval' @('import', '-Config', $config, '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records, '-Operator', 'ops') $importEnv
    Expect-Usage 'import refuses a relative trusted-root approval' @('import', '-Config', $config, '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-TrustedRootApproval', 'approval.json', '-Staging', $staging, '-Records', $records, '-Operator', 'ops') $importEnv
    Expect-Usage 'import refuses a missing CLI dir' $importBase (With-Env @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $null })
    Expect-Usage 'import refuses PRINTFARMER_DOTNET for a self-contained CLI' $importBase (With-Env @{ PRINTFARMER_HOST_UPDATE_CLI_DIR = $appHostDir; PRINTFARMER_DOTNET = $fakeDotnet })
    $importTail = @('-TrustedRootApproval', $approval, '-Config', $config)
    Expect-Usage 'import refuses a missing required option' (@('import', '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records) + $importTail) $importEnv
    Expect-Usage 'import refuses a duplicate option' ($importBase + @('-Channel', 'stable')) $importEnv
    Expect-Usage 'import refuses a relative bundle path' (@('import', '-Bundle', 'bundle.tar', '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records, '-Operator', 'ops') + $importTail) $importEnv
    Expect-Usage 'import refuses a relative records path' (@('import', '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', 'records', '-Operator', 'ops') + $importTail) $importEnv
    Expect-Usage 'import refuses an unknown channel' (@('import', '-Bundle', $bundle, '-Channel', 'Stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records, '-Operator', 'ops') + $importTail) $importEnv
    Expect-Usage 'import refuses shell metacharacters in the version' (@('import', '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1;rm',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records, '-Operator', 'ops') + $importTail) $importEnv
    Expect-Usage 'import refuses a malformed operator' (@('import', '-Bundle', $bundle, '-Channel', 'stable', '-Version', '1.2.3',
        '-TrustedRoot', $rootJson, '-Staging', $staging, '-Records', $records, '-Operator', '-ops') + $importTail) $importEnv
    Expect-Usage 'import refuses a verification bypass option' ($importBase + @('-SkipVerification', 'true')) $importEnv
    Expect-Usage 'import refuses a bash-style option' ($importBase + @('--prior-recovery-set', $prior)) $importEnv
    Expect-Usage 'import refuses a missing option value' ($importBase + @('-PriorRecoverySet')) $importEnv
    Expect-Usage 'import refuses a miscased command' (@('Import') + $importBase[1..($importBase.Count - 1)]) $importEnv
    Expect-Usage 'import refuses a relative PRINTFARMER_NODE' $importBase (With-Env @{ PRINTFARMER_NODE = 'node' })
    Expect-Usage 'import refuses a relative PRINTFARMER_COSIGN' $importBase (With-Env @{ PRINTFARMER_COSIGN = 'cosign' })
    Expect-Usage 'import refuses a missing bundle tool' $importBase (With-Env @{ PRINTFARMER_OFFLINE_BUNDLE_TOOL = (Join-Path $testRoot 'missing.mjs') })

    if ($script:failures -gt 0) {
        Write-Host "$($script:failures) PowerShell wrapper test(s) failed"
        exit 1
    }

    Write-Host 'All host-update PowerShell wrapper tests passed'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
