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

    # Issue #3118: the packaged CLI as a real Windows service. Runs only elevated and when no
    # PrintFarmerHostUpdateDaemon service exists (GitHub-hosted Windows runners are both).
    $env:PATH = $savedPath
    $serviceName = 'PrintFarmerHostUpdateDaemon'
    $isAdmin = ([System.Security.Principal.WindowsPrincipal] [System.Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [System.Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host '[SKIP] Windows service lifecycle needs an elevated session'
    } elseif (Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'") {
        Write-Host "[SKIP] Windows service lifecycle: $serviceName already exists on this host"
    } else {
        $script:lifecycleInstalled = $true
        $installer = Join-Path $repoRoot 'scripts\install-host-update-cli.ps1'
        function Invoke-ServiceInstaller([string[]] $Arguments) {
            $output = (& $pwshPath -NoProfile -NonInteractive -File $installer @Arguments 2>&1) -join "`n"
            return [pscustomobject] @{ ExitCode = $LASTEXITCODE; Output = $output }
        }
        function Get-LifecycleService { Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" }
        function Wait-ServiceState([string] $State, [int] $Seconds) {
            $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
            do {
                $current = Get-LifecycleService
                if ($current -and $current.State -eq $State) { return $current }
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $deadline)
            return $null
        }
        function Check([string] $Name, [bool] $Condition) { if ($Condition) { Pass $Name } else { Fail $Name } }

        # Program Files grants Users read/execute, as a real install does; the state root is
        # outside the OS temp directory and readable only by SYSTEM and Administrators.
        $script:lifecycleCli = Join-Path $env:ProgramFiles ("PrintFarmer\HostUpdateCli-lifecycle-" + [guid]::NewGuid().ToString('N'))
        $cliDir = Join-Path $script:lifecycleCli $version
        New-Item -ItemType Directory -Path $script:lifecycleCli | Out-Null
        Copy-Item -LiteralPath $install -Destination $cliDir -Recurse
        $script:lifecycleRoot = Join-Path $env:SystemDrive ("pf-lifecycle-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $script:lifecycleRoot | Out-Null
        $rootAcl = [System.Security.AccessControl.DirectorySecurity]::new()
        $rootAcl.SetAccessRuleProtection($true, $false)
        foreach ($admin in @('S-1-5-18', 'S-1-5-32-544')) {
            $rootAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                [System.Security.Principal.SecurityIdentifier]::new($admin), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        }
        [System.IO.FileSystemAclExtensions]::SetAccessControl([System.IO.DirectoryInfo]::new($script:lifecycleRoot), $rootAcl)
        $stateRoot = Join-Path $script:lifecycleRoot 'root'
        New-Item -ItemType Directory -Path $stateRoot | Out-Null
        $envFile = Join-Path $script:lifecycleRoot 'deploy.env'
        Set-Content -LiteralPath $envFile -Value "HostUpdateExecution__RootDirectory=$stateRoot"
        $serviceConfig = Join-Path $script:lifecycleRoot 'host-update.json'
        $result = Invoke-ServiceInstaller @('write-config', '-EnvFile', $envFile, '-Output', $serviceConfig)
        Check 'write-config for the service lifecycle succeeds' ($result.ExitCode -eq 0)

        $result = Invoke-ServiceInstaller @('install-service', '-CliDir', $cliDir, '-Config', $serviceConfig)
        $service = Get-LifecycleService
        Check "install-service registers the packaged daemon disabled and stopped ($($result.Output))" ($result.ExitCode -eq 0 -and
            $service -and $service.StartMode -eq 'Disabled' -and $service.State -eq 'Stopped')
        $null = & sc.exe start $serviceName 2>&1
        Check 'a disabled service cannot be started' ($LASTEXITCODE -eq 1058 -and (Get-LifecycleService).State -eq 'Stopped')

        $result = Invoke-ServiceInstaller @('install-service', '-CliDir', $cliDir, '-Config', $serviceConfig, '-Enable')
        $service = Wait-ServiceState 'Running' 30
        Check "install-service -Enable starts the daemon under SCM ($($result.Output))" ($result.ExitCode -eq 0 -and $service -and
            $service.StartMode -eq 'Auto' -and $service.StartName -eq "NT SERVICE\$serviceName")
        Start-Sleep -Seconds 5
        $service = Get-LifecycleService
        Check 'the daemon keeps running under its virtual account' ($service.State -eq 'Running')
        $log = Join-Path $env:ProgramData 'PrintFarmer\host\daemon\logs\daemon.log'
        Check 'the daemon writes its status lines to the service log' (
            (Test-Path -LiteralPath $log) -and (Get-Content -LiteralPath $log -Raw).Contains('daemon_cycle_completed'))

        Stop-Service -Name $serviceName
        $service = Wait-ServiceState 'Stopped' 40
        Check 'an operator stop stops the daemon cleanly' ($service -and $service.ExitCode -eq 0)
        Start-Sleep -Seconds 40
        Check 'an operator stop is not followed by a recovery restart' ((Get-LifecycleService).State -eq 'Stopped')

        # A nonzero self-exit (the state directory vanished: exit 3) must trigger service recovery,
        # which restarts the daemon after 30 s; by then the state directory is back.
        $stateDirectory = Join-Path $stateRoot 'state'
        $stateAcl = Get-Acl -LiteralPath $stateDirectory
        Remove-Item -LiteralPath $stateDirectory -Recurse -Force
        $null = & sc.exe start $serviceName 2>&1
        $service = Wait-ServiceState 'Stopped' 40
        Check 'the daemon stops itself with exit 3 when its state directory is missing' ($service -and $service.ExitCode -eq 3)
        New-Item -ItemType Directory -Path $stateDirectory | Out-Null
        Set-Acl -LiteralPath $stateDirectory -AclObject $stateAcl
        $service = Wait-ServiceState 'Running' 90
        Check 'service recovery restarts the daemon after a nonzero self-exit' ($null -ne $service)

        Set-Service -Name $serviceName -StartupType Disabled
        Stop-Service -Name $serviceName
        Check 'the daemon can be disabled and stopped' ($null -ne (Wait-ServiceState 'Stopped' 40) -and (Get-LifecycleService).StartMode -eq 'Disabled')
        $result = Invoke-ServiceInstaller @('uninstall-service')
        Check 'uninstall-service removes the packaged daemon service' ($result.ExitCode -eq 0 -and $null -eq (Get-LifecycleService))
        $script:lifecycleInstalled = $false
    }

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
    if ((Get-Variable -Name lifecycleInstalled -Scope Script -ErrorAction SilentlyContinue) -and $script:lifecycleInstalled) {
        & sc.exe stop PrintFarmerHostUpdateDaemon *> $null
        Start-Sleep -Seconds 2
        & sc.exe delete PrintFarmerHostUpdateDaemon *> $null
    }
    foreach ($name in 'lifecycleCli', 'lifecycleRoot') {
        $path = Get-Variable -Name $name -Scope Script -ValueOnly -ErrorAction SilentlyContinue
        if ($path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
