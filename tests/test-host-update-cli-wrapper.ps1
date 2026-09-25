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

    function Expect-Passthrough([string] $Name, [string[]] $Expected, [string[]] $WrapperArgs) {
        $run = Invoke-Wrapper $WrapperArgs
        $actual = if ($run.Invoked) { Get-Content -LiteralPath $argsLog -Raw } else { '' }
        if ($run.ExitCode -eq 0 -and $run.Invoked -and $actual -ceq ($Expected -join "`n")) { Pass $Name }
        else { Fail "$Name (exit $($run.ExitCode), args: $actual)" }
    }

    Expect-Passthrough 'status passes through' @($dll, '--config', $config, 'status', '--json') @('-Config', $config, 'status', '-Json')
    Expect-Passthrough 'status detail passes through' @($dll, '--config', $config, 'status', '--release', 'stable:1.2.3') @('-Config', $config, 'status', '-Release', 'stable:1.2.3')
    Expect-Passthrough 'recover preview passes through' @($dll, '--config', $config, 'recover', '--release', 'insider:1.2.3-rc.1', '--request-id', 'req-1', '--preview') @('-Config', $config, 'recover', '-Release', 'insider:1.2.3-rc.1', '-RequestId', 'req-1', '-Preview')
    Expect-Passthrough 'recover confirm passes through' @($dll, '--config', $config, 'recover', '--release', 'stable:1.2.3', '--confirm', 'stable:1.2.3', '--json') @('-Config', $config, 'recover', '-Release', 'stable:1.2.3', '-Confirm', 'stable:1.2.3', '-Json')
    Expect-Passthrough 'parameter names are case-insensitive' @($dll, '--config', $config, 'status', '--json') @('-config', $config, 'status', '-JSON')

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

    if ($script:failures -gt 0) {
        Write-Host "$($script:failures) PowerShell wrapper test(s) failed"
        exit 1
    }

    Write-Host 'All host-update PowerShell wrapper tests passed'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
