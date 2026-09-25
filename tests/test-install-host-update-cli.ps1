#Requires -Version 7.0
# Host-update CLI installer tests for scripts/install-host-update-cli.ps1 (issue #3045). Builds
# fixture release assets whose launcher only answers `help`, puts a recording cosign stub on PATH,
# and proves install verifies the signature identity, checksum, members and manifest, proves the
# CLI runs, and places it side by side. Proves write-config emits owner-only JSON (a protected
# ACL on Windows, mode 0600 elsewhere) from only the host-update keys of a deployment .env.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repoRoot 'scripts/install-host-update-cli.ps1'
$pwshPath = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('printfarmer-install-host-update-cli-ps-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$savedPath = $env:PATH

$script:failures = 0
function Pass([string] $Name) { Write-Host "[PASS] $Name" }
function Fail([string] $Name) { Write-Host "[FAIL] $Name"; $script:failures++ }
function Check([string] $Name, [bool] $Condition) { if ($Condition) { Pass $Name } else { Fail $Name } }

try {
    $runtime = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
    $tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32\tar.exe' } else { 'tar' }

    # Fake CLI: exits 0 with the usage text only for `help`, unless FAKE_CLI_BROKEN is set.
    $fakeCli = Join-Path $testRoot 'fake-cli'
    New-Item -ItemType Directory -Path $fakeCli | Out-Null
    if ($IsWindows) {
        # A real apphost is needed on Windows; built outside the repository so its global.json
        # and build props do not apply.
        $project = Join-Path $testRoot 'fake-cli-src'
        New-Item -ItemType Directory -Path $project | Out-Null
        Set-Content -LiteralPath (Join-Path $project 'Farm.HostUpdate.Cli.csproj') -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
'@
        Set-Content -LiteralPath (Join-Path $project 'Program.cs') -Value @'
if (args.Length != 1 || args[0] != "help") return 2;
if (Environment.GetEnvironmentVariable("FAKE_CLI_BROKEN") is { Length: > 0 }) return 1;
Console.WriteLine("Usage:\n  printfarmer-host-update status [--release <releaseId>] [--json]");
return 0;
'@
        Push-Location $project
        try {
            & dotnet build -c Release -o $fakeCli -nologo -v q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not build the fake CLI' }
        } finally { Pop-Location }
    } else {
        $launcher = Join-Path $fakeCli 'Farm.HostUpdate.Cli'
        Set-Content -LiteralPath $launcher -Value "#!/bin/sh`n[ `"`$1`" = help ] || exit 2`n[ -n `"`${FAKE_CLI_BROKEN:-}`" ] && exit 1`necho 'printfarmer-host-update status [--release <releaseId>] [--json]'" -NoNewline
        & chmod 0755 $launcher
    }

    # Recording cosign stub; COSIGN_FAIL=1 makes verification fail.
    $bin = Join-Path $testRoot 'bin'
    New-Item -ItemType Directory -Path $bin | Out-Null
    $cosignLog = Join-Path $testRoot 'cosign.log'
    $stub = Join-Path $bin 'cosign-stub.ps1'
    Set-Content -LiteralPath $stub -Value @"
Add-Content -LiteralPath '$cosignLog' -Value (`$args -join ' ')
if (`$env:COSIGN_FAIL -eq '1') { exit 1 }
exit 0
"@
    if ($IsWindows) {
        Set-Content -LiteralPath (Join-Path $bin 'cosign.cmd') -Value "@`"$pwshPath`" -NoProfile -NonInteractive -File `"$stub`" %*"
    } else {
        Set-Content -LiteralPath (Join-Path $bin 'cosign') -Value "#!/bin/sh`nexec `"$pwshPath`" -NoProfile -NonInteractive -File `"$stub`" `"`$@`"" -NoNewline
        & chmod 0755 (Join-Path $bin 'cosign')
    }
    $env:PATH = "$bin$([System.IO.Path]::PathSeparator)$savedPath"

    function New-Release([string] $Version, [string] $Directory, [string] $Variant = 'good') {
        $prefix = "printfarmer-host-update-cli-v$Version"
        $stage = Join-Path $testRoot ('stage-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage, $Directory -Force | Out-Null
        Copy-Item -Path $fakeCli -Destination (Join-Path $stage 'cli') -Recurse
        foreach ($file in 'printfarmer-host-update.ps1', 'printfarmer-host-update.sh', 'common-utils.sh') {
            Copy-Item -LiteralPath (Join-Path $repoRoot "scripts/$file") -Destination $stage
        }
        $manifestVersion = if ($Variant -eq 'manifest') { '9.9.9' } else { $Version }
        [System.IO.File]::WriteAllText((Join-Path $stage 'host-update-cli-package.json'), @"
{
  "schema": 1,
  "package": "printfarmer-host-update-cli",
  "version": "$manifestVersion",
  "runtime": "$runtime",
  "selfContained": true,
  "rolloutAuthorization": false
}

"@.Replace("`r`n", "`n"))
        $archive = Join-Path $Directory "$prefix-$runtime.tar.gz"
        if ($Variant -eq 'traversal') {
            New-Item -ItemType Directory -Path (Join-Path $stage 'x') | Out-Null
            Set-Content -LiteralPath (Join-Path $stage 'evil') -Value 'evil'
            Push-Location (Join-Path $stage 'x')
            try { & $tar -czPf $archive ../evil } finally { Pop-Location }
        } else {
            & $tar -czf $archive -C $stage .
        }
        $hash = if ($Variant -eq 'mismatch') { '0' * 64 } else { (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
        $named = if ($Variant -eq 'missing-entry') { "$prefix-linux-arm64.tar.gz" } else { "$prefix-$runtime.tar.gz" }
        [System.IO.File]::WriteAllText((Join-Path $Directory "$prefix-SHA256SUMS"), "$hash  $named`n")
        [System.IO.File]::WriteAllText((Join-Path $Directory "$prefix-SHA256SUMS.sigstore.json"), "{}`n")
        Remove-Item -LiteralPath $stage -Recurse -Force
    }

    function Invoke-Installer([string[]] $Arguments, [hashtable] $Environment = @{}) {
        $saved = @{}
        foreach ($key in $Environment.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $Environment[$key])
        }
        try {
            $output = & $pwshPath -NoProfile -NonInteractive -File $installer @Arguments 2>&1
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
        } finally {
            foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
        }
    }

    $root = Join-Path $testRoot 'HostUpdateCli'
    $assets = Join-Path $testRoot 'assets'
    New-Release '1.2.3' $assets
    Set-Content -LiteralPath $cosignLog -Value ''
    $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $root)
    Check 'stable install succeeds' ($result.ExitCode -eq 0)
    $log = Get-Content -LiteralPath $cosignLog -Raw
    Check 'stable identity is the main-branch release workflow' ($log -match [regex]::Escape('--certificate-identity https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main '))
    Check 'cosign pins the GitHub OIDC issuer' ($log -match [regex]::Escape('--certificate-oidc-issuer https://token.actions.githubusercontent.com '))
    Check 'cosign verifies a private copy, not the asset directory' (-not $log.Contains($assets))
    $launcherName = if ($IsWindows) { 'cli/Farm.HostUpdate.Cli.exe' } else { 'cli/Farm.HostUpdate.Cli' }
    Check 'CLI is placed under the version directory' (Test-Path -LiteralPath (Join-Path $root "1.2.3/$launcherName"))
    $wrapperName = if ($IsWindows) { 'printfarmer-host-update.ps1' } else { 'printfarmer-host-update.sh' }
    Check 'install prints the wrapper path' ($result.Output.Contains((Join-Path (Join-Path $root '1.2.3') $wrapperName)))

    New-Release '1.2.4-insider.5' (Join-Path $testRoot 'insider')
    Set-Content -LiteralPath $cosignLog -Value ''
    $result = Invoke-Installer @('install', '-Version', '1.2.4-insider.5', '-Runtime', $runtime, '-AssetDir', (Join-Path $testRoot 'insider'), '-InstallRoot', $root)
    Check 'insider install succeeds side by side' ($result.ExitCode -eq 0 -and (Test-Path (Join-Path $root '1.2.3')) -and (Test-Path (Join-Path $root '1.2.4-insider.5')))
    Check 'insider identity is the development-branch release workflow' ((Get-Content -LiteralPath $cosignLog -Raw) -match [regex]::Escape('consolidated-release.yml@refs/heads/development '))

    $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $root)
    Check 'same-version reinstall of an identical placement succeeds' ($result.ExitCode -eq 0 -and (Test-Path (Join-Path $root "1.2.3/$launcherName")))
    $tamperedFile = Join-Path $root '1.2.3/host-update-cli-package.json'
    Add-Content -LiteralPath $tamperedFile -Value 'tampered'
    $tampered = Get-FileHash -LiteralPath $tamperedFile
    $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $root)
    Check 'a differing same-version placement is refused and left untouched' ($result.ExitCode -eq 1 -and
        (Get-FileHash -LiteralPath $tamperedFile).Hash -eq $tampered.Hash -and $result.Output.Contains('differs from the verified release'))
    Remove-Item -LiteralPath (Join-Path $root '1.2.3') -Recurse -Force
    $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $root)
    Check 'a removed placement can be reinstalled' ($result.ExitCode -eq 0 -and (Test-Path (Join-Path $root "1.2.3/$launcherName")))

    if ($IsWindows) {
        $openRoot = Join-Path $testRoot 'OpenRoot'
        New-Item -ItemType Directory -Path $openRoot | Out-Null
        $openAcl = Get-Acl -LiteralPath $openRoot
        $openAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $openRoot -AclObject $openAcl
        $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $openRoot)
        Check 'an install root writable by Users is refused and nothing is placed' ($result.ExitCode -eq 1 -and
            $result.Output.Contains('writable by S-1-5-32-545') -and @(Get-ChildItem -LiteralPath $openRoot -Force).Count -eq 0)
    }

    New-Release '2.0.0' (Join-Path $testRoot 'v2')
    $result = Invoke-Installer @('install', '-Version', '2.0.0', '-Runtime', $runtime, '-AssetDir', (Join-Path $testRoot 'v2'), '-InstallRoot', $root) @{ COSIGN_FAIL = '1' }
    Check 'signature failure exits 1 and places nothing' ($result.ExitCode -eq 1 -and -not (Test-Path (Join-Path $root '2.0.0')) -and $result.Output.Contains('not signed by the main release workflow'))

    $env:PATH = $savedPath
    if (-not (Get-Command cosign -ErrorAction SilentlyContinue)) {
        $result = Invoke-Installer @('install', '-Version', '2.0.0', '-Runtime', $runtime, '-AssetDir', (Join-Path $testRoot 'v2'), '-InstallRoot', $root)
        Check 'missing cosign fails closed' ($result.ExitCode -eq 1 -and -not (Test-Path (Join-Path $root '2.0.0')))
    }
    $env:PATH = "$bin$([System.IO.Path]::PathSeparator)$savedPath"

    foreach ($case in @(@('mismatch', 'SHA-256 mismatch'), @('missing-entry', 'does not name'),
            @('manifest', 'manifest does not match'), @('traversal', 'unsafe member name'))) {
        $directory = Join-Path $testRoot $case[0]
        New-Release '3.0.0' $directory $case[0]
        $result = Invoke-Installer @('install', '-Version', '3.0.0', '-Runtime', $runtime, '-AssetDir', $directory, '-InstallRoot', $root)
        Check "$($case[0]) archive is refused for the right reason and places nothing" ($result.ExitCode -eq 1 -and -not (Test-Path (Join-Path $root '3.0.0')) -and $result.Output.Contains($case[1]))
    }

    $result = Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', $root) @{ FAKE_CLI_BROKEN = '1' }
    Check 'a CLI that cannot run keeps the previous placement' ($result.ExitCode -eq 1 -and (Test-Path (Join-Path $root "1.2.3/$launcherName")))
    Check 'no staging or previous directories are left behind' (@(Get-ChildItem -LiteralPath $root -Force | Where-Object Name -like '.*').Count -eq 0)

    Check 'invalid version is a usage error' ((Invoke-Installer @('install', '-Version', '1.2', '-AssetDir', $assets, '-InstallRoot', $root)).ExitCode -eq 2)
    Check 'relative install root is a usage error' ((Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', $runtime, '-AssetDir', $assets, '-InstallRoot', 'rel')).ExitCode -eq 2)
    Check 'unsupported runtime is a usage error' ((Invoke-Installer @('install', '-Version', '1.2.3', '-Runtime', 'osx-arm64', '-AssetDir', $assets)).ExitCode -eq 2)
    Check 'unknown option is a usage error' ((Invoke-Installer @('install', '-Bogus', 'x')).ExitCode -eq 2)

    # write-config
    $envFile = Join-Path $testRoot 'deploy.env'
    $config = Join-Path $testRoot 'etc/host-update.json'
    [System.IO.File]::WriteAllText($envFile, "DB_PROVIDER=postgres`nPOSTGRES_PASSWORD=unrelated`n")
    $result = Invoke-Installer @('write-config', '-EnvFile', $envFile, '-Output', $config)
    Check 'write-config exits 3 and writes nothing when the root is not configured' ($result.ExitCode -eq 3 -and -not (Test-Path $config))

    $stateRoot = Join-Path $testRoot 'state'
    New-Item -ItemType Directory -Path $stateRoot | Out-Null
    [System.IO.File]::WriteAllText($envFile, (@(
        '# deployment settings', 'DB_PROVIDER=sqlite', 'POSTGRES_PASSWORD=unrelated',
        'ConnectionStrings__Default=Host=db;Password=p"w\d', "HostUpdateExecution__RootDirectory=$stateRoot",
        'HostUpdateExecution__ComposeFiles__0=/srv/pf/docker-compose.yml', 'HostUpdateExecution__ActiveServiceIds__0=api',
        'HostUpdateExecution__ActiveServiceIds__0=api-last', 'HostUpdates__HostState__Namespace=farm-a'
    ) -join "`n") + "`nDB_PROVIDER=postgres`r`n")
    $result = Invoke-Installer @('write-config', '-EnvFile', $envFile, '-Output', $config)
    Check 'write-config succeeds' ($result.ExitCode -eq 0)
    $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -AsHashtable
    Check 'config holds exactly the host-update keys, last value wins' (
        ($parsed.Keys | Sort-Object) -join ',' -eq 'ConnectionStrings,DB_PROVIDER,HostUpdateExecution,HostUpdates' -and
        $parsed.DB_PROVIDER -ceq 'postgres' -and $parsed.ConnectionStrings.Default -ceq 'Host=db;Password=p"w\d' -and
        $parsed.HostUpdateExecution.RootDirectory -ceq $stateRoot -and $parsed.HostUpdateExecution.ActiveServiceIds['0'] -ceq 'api-last' -and
        $parsed.HostUpdateExecution.ComposeFiles['0'] -ceq '/srv/pf/docker-compose.yml' -and $parsed.HostUpdates.HostState.Namespace -ceq 'farm-a')
    Check 'unrelated secrets are not copied' (-not (Get-Content -LiteralPath $config -Raw).Contains('unrelated'))
    if ($IsWindows) {
        $acl = Get-Acl -LiteralPath $config
        $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        $expectedOwner = [System.Security.Principal.NTAccount]::new((Get-Acl -LiteralPath $stateRoot).Owner).Translate([System.Security.Principal.SecurityIdentifier]).Value
        $bySid = @{}
        foreach ($rule in $rules) { $bySid[$rule.IdentityReference.Value] = $rule }
        Check 'config ACL does not inherit' ($acl.AreAccessRulesProtected -and @($rules | Where-Object IsInherited).Count -eq 0)
        Check 'config ACL grants only SYSTEM, Administrators and the owner' (
            (@($bySid.Keys | Sort-Object) -join ',') -eq ((@('S-1-5-18', 'S-1-5-32-544', $expectedOwner) | Sort-Object -Unique) -join ','))
        Check 'SYSTEM and Administrators have full control, the owner reads' ([bool](
            ($bySid['S-1-5-18'].FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and
            ($bySid['S-1-5-32-544'].FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -eq [System.Security.AccessControl.FileSystemRights]::FullControl -and
            ($expectedOwner -in 'S-1-5-18', 'S-1-5-32-544' -or
                ($bySid[$expectedOwner].FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Write) -eq 0)))
    } else {
        $mode = (Get-Item -LiteralPath $config).UnixFileMode
        Check 'config is mode 0600' ($mode -eq ([System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite))
    }
    Check 'no temporary config files are left behind' (@(Get-ChildItem -LiteralPath (Split-Path $config) -Force -Filter '.host-update.json.*').Count -eq 0)

    foreach ($case in @(
            @('a value with $', 'ConnectionStrings__Default=Password=SECRET$x'),
            @('a case-only duplicate key', 'HostUpdateExecution__rootdirectory=/SECRET', 'differs only in case'),
            @('a key that is both value and section', 'HostUpdateExecution__RootDirectory__Child=SECRET', 'both a value and a section'),
            @('a malformed key segment', 'HostUpdateExecution__Bad___Key=SECRET', 'malformed configuration key'))) {
        [System.IO.File]::WriteAllText($envFile, "HostUpdateExecution__RootDirectory=$stateRoot`n$($case[1])`n")
        $before = [System.IO.File]::ReadAllBytes($config)
        $result = Invoke-Installer @('write-config', '-EnvFile', $envFile, '-Output', $config)
        Check "$($case[0]) is refused and the existing config is kept" ($result.ExitCode -eq 1 -and
            ($case.Count -lt 3 -or $result.Output.Contains($case[2])) -and
            [System.Linq.Enumerable]::SequenceEqual([byte[]] $before, [byte[]] [System.IO.File]::ReadAllBytes($config)))
        Check "$($case[0]) error does not print the value" (-not $result.Output.Contains('SECRET'))
    }

    [System.IO.File]::WriteAllText($envFile, "HostUpdateExecution__RootDirectory=$stateRoot`n")
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'dir.json') | Out-Null
    Check 'a directory output is refused' ((Invoke-Installer @('write-config', '-EnvFile', $envFile, '-Output', (Join-Path $testRoot 'dir.json'))).ExitCode -eq 1)
    Check 'a relative env file is a usage error' ((Invoke-Installer @('write-config', '-EnvFile', 'deploy.env', '-Output', $config)).ExitCode -eq 2)
    $stateLink = Join-Path $testRoot 'state-link'
    if ($IsWindows) { New-Item -ItemType Junction -Path $stateLink -Target $stateRoot | Out-Null } else { New-Item -ItemType SymbolicLink -Path $stateLink -Target $stateRoot | Out-Null }
    [System.IO.File]::WriteAllText($envFile, "HostUpdateExecution__RootDirectory=$stateLink`n")
    $result = Invoke-Installer @('write-config', '-EnvFile', $envFile, '-Output', $config)
    $linkOwnerSafe = if ($IsWindows) {
        (@((Get-Acl -LiteralPath $config).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
            ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique) -join ',') -eq
            ((@('S-1-5-18', 'S-1-5-32-544', [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) | Sort-Object -Unique) -join ',')
    } else { $true }
    Check 'a linked state root is not trusted as the config owner' ($result.ExitCode -eq 0 -and $linkOwnerSafe -and
        $result.Output.Contains('not an absolute, non-link directory'))

    # deploy-docker.ps1 opt-in hook: run the real function against a recording stub installer.
    $hookDir = Join-Path $testRoot 'hook'
    New-Item -ItemType Directory -Path $hookDir | Out-Null
    Set-Content -LiteralPath (Join-Path $hookDir 'install-host-update-cli.ps1') -Value @'
Add-Content -LiteralPath $env:HOOK_LOG -Value ($args -join ' ')
if ($args[0] -eq 'write-config') { exit [int]$env:HOOK_WRITE_RC }
exit [int]$env:HOOK_INSTALL_RC
'@
    $deployAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts/deploy-docker.ps1'), [ref]$null, [ref]$null)
    $hookFn = $deployAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Install-HostUpdateCliIfRequested' }, $true).Extent.Text
    Set-Content -LiteralPath (Join-Path $hookDir 'run.ps1') -Value (@(
            'function Write-Info([string]$m) { Write-Host $m }; function Write-Success([string]$m) { Write-Host $m }; function Write-ErrorMsg([string]$m) { Write-Host "ERR $m" }',
            '$HostUpdateCliVersion = $env:HOOK_VERSION; $HostUpdateCliAssets = $env:HOOK_ASSETS',
            $hookFn,
            "Set-Location -LiteralPath '$hookDir'; Install-HostUpdateCliIfRequested; exit 0") -join "`n")
    $env:HOOK_LOG = Join-Path $hookDir 'log'
    function Invoke-Hook([string]$Version, [string]$Assets = '', [int]$InstallRc = 0, [int]$WriteRc = 0) {
        Remove-Item -LiteralPath $env:HOOK_LOG -ErrorAction SilentlyContinue
        $env:HOOK_VERSION = $Version; $env:HOOK_ASSETS = $Assets; $env:HOOK_INSTALL_RC = $InstallRc; $env:HOOK_WRITE_RC = $WriteRc
        $output = (& $pwshPath -NoProfile -File (Join-Path $hookDir 'run.ps1') 2>&1 | Out-String)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output
            Log = @(if (Test-Path -LiteralPath $env:HOOK_LOG) { Get-Content -LiteralPath $env:HOOK_LOG }) }
    }
    $hook = Invoke-Hook ''
    Check 'deploy hook is a no-op without a version' ($hook.ExitCode -eq 0 -and $hook.Log.Count -eq 0)
    $assets = Join-Path $testRoot 'assets'
    $hook = Invoke-Hook '1.2.3' $assets
    Check 'deploy hook installs then writes config from the absolute env file' ($hook.ExitCode -eq 0 -and
        ($hook.Log -join '|') -ceq "install -Version 1.2.3 -AssetDir $assets|write-config -EnvFile $(Join-Path $hookDir '.env')")
    $hook = Invoke-Hook '1.2.3' 'offline'
    Check 'deploy hook makes a relative asset directory absolute' ($hook.ExitCode -eq 0 -and
        $hook.Log[0] -ceq "install -Version 1.2.3 -AssetDir $(Join-Path $hookDir 'offline')")
    $hook = Invoke-Hook '1.2.3' -WriteRc 3
    Check 'deploy hook warns and continues when the root is not configured' ($hook.ExitCode -eq 0 -and $hook.Output.Contains('was not written'))
    $hook = Invoke-Hook '1.2.3' -InstallRc 1
    Check 'deploy hook fails the deployment when install fails' ($hook.ExitCode -eq 1 -and $hook.Log.Count -eq 1)
    Check 'deploy hook fails the deployment when write-config fails' ((Invoke-Hook '1.2.3' -WriteRc 1).ExitCode -eq 1)
} finally {
    $env:PATH = $savedPath
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:failures -gt 0) {
    Write-Host "$script:failures host-update CLI installer test(s) failed"
    exit 1
}
Write-Host 'All host-update CLI installer tests passed'
