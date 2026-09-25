#Requires -Version 7.0
# Packaged host-update CLI smoke test for Windows (issue #3041). Builds the self-contained win-x64
# archive with the release packaging code, verifies it against its checksum list, extracts it the
# way the runbook installs it, and runs the real CLI through the packaged wrapper with any dotnet
# on PATH poisoned, proving the package needs no source checkout, API or .NET runtime.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'The PowerShell packaged CLI smoke test supports Windows hosts only' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$rid = 'win-x64'
$version = '0.0.0-package-test'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("printfarmer-host-update-package-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$savedPath = $env:PATH
$savedCliDir = $env:PRINTFARMER_HOST_UPDATE_CLI_DIR
$savedDotnet = $env:PRINTFARMER_DOTNET

try {
    $script:failures = 0
    function Pass([string] $Name) { Write-Host "[PASS] $Name" }
    function Fail([string] $Name) { Write-Host "[FAIL] $Name"; $script:failures++ }

    $out = Join-Path $testRoot 'out'
    $archive = "printfarmer-host-update-cli-v$version-$rid.tar.gz"
    $sums = "printfarmer-host-update-cli-v$version-SHA256SUMS"
    & node (Join-Path $repoRoot 'scripts/ci/host-update-cli-package.mjs') --version $version --runtime $rid --output $out --source $repoRoot
    if ($LASTEXITCODE -ne 0) { throw "packaging failed with exit $LASTEXITCODE" }

    $lines = @(Get-Content -LiteralPath (Join-Path $out $sums))
    $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $out $archive)).Hash.ToLowerInvariant()
    if ($lines.Count -eq 1 -and $lines[0] -ceq "$expectedHash  $archive") { Pass 'archive matches its checksum list' }
    else { Fail "archive matches its checksum list ($($lines -join ' | '))" }

    $install = Join-Path $testRoot "PrintFarmer\HostUpdateCli\$version"
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    & "$env:SystemRoot\System32\tar.exe" -xzf (Join-Path $out $archive) -C $install
    if ($LASTEXITCODE -ne 0) { throw "extraction failed with exit $LASTEXITCODE" }

    $manifest = Get-Content -LiteralPath (Join-Path $install 'host-update-cli-package.json') -Raw | ConvertFrom-Json
    if ($manifest.runtime -ceq $rid -and $manifest.version -ceq $version -and $manifest.selfContained -and -not $manifest.rolloutAuthorization) {
        Pass 'package manifest records version, runtime and no rollout authorization'
    } else { Fail 'package manifest records version, runtime and no rollout authorization' }

    if (Test-Path -LiteralPath (Join-Path $install 'cli\Farm.HostUpdate.Cli.exe') -PathType Leaf) { Pass 'self-contained launcher is packaged' }
    else { Fail 'self-contained launcher is packaged' }

    # Any dotnet the wrapper might reach is poisoned: the package must run on its own runtime.
    $poison = Join-Path $testRoot 'poison'
    New-Item -ItemType Directory -Path $poison | Out-Null
    Set-Content -LiteralPath (Join-Path $poison 'dotnet.cmd') -Value '@exit /b 99'
    $config = Join-Path $testRoot 'host-update.json'
    Set-Content -LiteralPath $config -Value '{}'
    $pwshPath = (Get-Process -Id $PID).Path
    $wrapper = Join-Path $install 'printfarmer-host-update.ps1'

    $env:PATH = "$poison;$env:SystemRoot\System32;$env:SystemRoot"
    $env:PRINTFARMER_HOST_UPDATE_CLI_DIR = $null
    $env:PRINTFARMER_DOTNET = $null

    $null = & $pwshPath -NoProfile -NonInteractive -File $wrapper help
    if ($LASTEXITCODE -eq 0) { Pass 'packaged wrapper help' } else { Fail "packaged wrapper help (exit $LASTEXITCODE)" }

    $stdout = (& $pwshPath -NoProfile -NonInteractive -File $wrapper -Config $config status -Json 2>&1) -join "`n"
    $code = $LASTEXITCODE
    if ($code -eq 3 -and $stdout -match '"exitCode": 3' -and $stdout -match 'root_directory_not_visible') {
        Pass 'packaged CLI runs without dotnet and fails closed on an unconfigured root (exit 3)'
    } else { Fail "packaged CLI run (exit $code): $stdout" }

    $null = & $pwshPath -NoProfile -NonInteractive -File $wrapper -Config 'relative.json' status 2>$null
    if ($LASTEXITCODE -eq 2) { Pass 'packaged wrapper still refuses a relative config' }
    else { Fail "packaged wrapper still refuses a relative config (exit $LASTEXITCODE)" }

    if ($script:failures -gt 0) {
        Write-Host "$($script:failures) packaged CLI test(s) failed"
        exit 1
    }

    Write-Host "All packaged host-update CLI tests passed ($rid)"
}
finally {
    $env:PATH = $savedPath
    $env:PRINTFARMER_HOST_UPDATE_CLI_DIR = $savedCliDir
    $env:PRINTFARMER_DOTNET = $savedDotnet
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
