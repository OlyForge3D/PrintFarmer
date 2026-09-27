#Requires -Version 7.0
<#
.SYNOPSIS
    Installs the signed, self-contained host-update recovery CLI and generates its host
    configuration (issue #3045).

.DESCRIPTION
    It never builds the CLI and never falls back to an unverified source. It is NOT rollout
    authorization: it neither enables nor starts an update.

      install-host-update-cli.ps1 install -Version <X.Y.Z[-insider.N]> [-AssetDir <abs-dir>] [-InstallRoot <abs-dir>] [-Runtime <win-x64|linux-x64|linux-arm64>] [-TrustedRoot <abs-file>]
      install-host-update-cli.ps1 write-config -EnvFile <abs-file> [-Output <abs-file>] [-Owner <account>]
      install-host-update-cli.ps1 install-service -CliDir <abs-dir> [-Config <abs-file>] [-Enable]
      install-host-update-cli.ps1 uninstall-service

    install       Downloads (or reads from -AssetDir) the runtime's archive, the checksum list and
                  its Cosign bundle; verifies the bundle against the release workflow identity for
                  the version's channel, the archive SHA-256, its members and package manifest;
                  proves the CLI launches; then places it at <InstallRoot>\<version> (default
                  C:\Program Files\PrintFarmer\HostUpdateCli, or /opt/printfarmer/host-update-cli).
                  Versions are immutable: an existing placement identical to the verified archive
                  is accepted, a differing one is refused and left untouched. The install root must
                  not be writable by anyone but SYSTEM, Administrators, TrustedInstaller and the
                  installing account (group/world-writable elsewhere). Requires cosign on PATH.
                  -TrustedRoot verifies offline against an operator-supplied Sigstore trusted
                  root (an absolute path to a regular, readable file). It is only ever taken from
                  this option, never from the environment or configuration, and is never
                  defaulted; without it cosign verifies against the public-good root.
    write-config  Writes host-update.json (default C:\ProgramData\PrintFarmer\host-update.json, or
                  /etc/printfarmer/host-update.json) from the deployment .env's
                  HostUpdateExecution__*, HostUpdates__HostState__*, DB_PROVIDER and
                  ConnectionStrings__Default. On Windows inheritance is removed and only SYSTEM,
                  Administrators (full control) and the owner account (read) have access; elsewhere
                  the file is mode 0600. The owner defaults to the owner of
                  HostUpdateExecution__RootDirectory when it is an absolute, non-link directory,
                  otherwise the current account.
    install-service  Windows only, opt-in (issue #3118); run elevated. Registers the enrolled
                  host-update daemon as the service PrintFarmerHostUpdateDaemon running
                  "<CliDir>\cli\Farm.HostUpdate.Cli.exe" --config <Config> daemon --windows-service
                  (default config C:\ProgramData\PrintFarmer\host-update.json). It runs as the
                  virtual account NT SERVICE\PrintFarmerHostUpdateDaemon with only
                  SeChangeNotifyPrivilege, read access to the config and RootDirectory, and modify
                  access to <RootDirectory>\state and the log directory
                  C:\ProgramData\PrintFarmer\host\daemon\logs. The service is created Disabled and
                  stopped unless -Enable is given, and a rerun never changes its startup type. It
                  carries no environment, credential or auto-update setting, and installing it
                  grants nothing: daemon execution stays disabled pending #2982. On Linux use
                  install-host-update-cli.sh install-service (systemd).
    uninstall-service  Stops and deletes the service and removes its access entries. It never
                  deletes the config, journal, identity storage or logs; removing a service that is
                  not installed is a no-op.

    Exit codes: 0 done; 1 verification, validation or installation failed (nothing placed or
    written); 2 usage; 3 write-config only: HostUpdateExecution__RootDirectory is not configured,
    so nothing was written. Arguments are parsed by the script, so a usage error never prompts.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$VersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-insider\.(0|[1-9][0-9]*))?$'
$ReleaseRepository = 'OlyForge3D/PrintFarmer'
$OidcIssuer = 'https://token.actions.githubusercontent.com'
$Runtimes = @('win-x64', 'linux-x64', 'linux-arm64')
$DaemonServiceName = 'PrintFarmerHostUpdateDaemon'
$DaemonServiceAccount = "NT SERVICE\$DaemonServiceName"
$DaemonServiceMarker = '(Managed by install-host-update-cli install-service, issue #3118.)'
$UsageText = @'
usage:
  install-host-update-cli.ps1 install -Version <X.Y.Z[-insider.N]> [-AssetDir <abs-dir>] [-InstallRoot <abs-dir>] [-Runtime <win-x64|linux-x64|linux-arm64>] [-TrustedRoot <abs-file>]
  install-host-update-cli.ps1 write-config -EnvFile <abs-file> [-Output <abs-file>] [-Owner <account>]
  install-host-update-cli.ps1 install-service -CliDir <abs-dir> [-Config <abs-file>] [-Enable]
  install-host-update-cli.ps1 uninstall-service
'@

class InstallerFailure : System.Exception {
    [int] $ExitCode
    InstallerFailure([string] $Message, [int] $ExitCode) : base($Message) { $this.ExitCode = $ExitCode }
}

function Stop-Usage([string] $Message) {
    throw [InstallerFailure]::new("$Message`n$UsageText", 2)
}

function Stop-Install([string] $Message) {
    throw [InstallerFailure]::new($Message, 1)
}

function Test-FullyQualified([string] $Path) {
    return -not [string]::IsNullOrWhiteSpace($Path) -and [System.IO.Path]::IsPathFullyQualified($Path)
}

function Test-Link([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return $null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
}

# True when both trees hold the same relative paths, no reparse points, identical file bytes and
# (off Windows) identical Unix modes.
function Test-TreeEqual([string] $Expected, [string] $Actual) {
    function Get-Tree([string] $Root) {
        $tree = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
        foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $null }
            $relative = [System.IO.Path]::GetRelativePath($Root, $item.FullName)
            $value = if ($item.PSIsContainer) { '<dir>' } else { (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
            if (-not $IsWindows) { $value += " $([int] $item.UnixFileMode)" }
            $tree[$relative] = $value
        }
        return , $tree
    }
    $left = Get-Tree $Expected
    $right = Get-Tree $Actual
    if ($null -eq $left -or $null -eq $right -or $left.Count -ne $right.Count) { return $false }
    foreach ($entry in $left.GetEnumerator()) {
        $value = $null
        if (-not $right.TryGetValue($entry.Key, [ref] $value) -or $value -cne $entry.Value) { return $false }
    }
    if (-not $IsWindows) {
        # find also covers the version directory itself, which Get-ChildItem does not list.
        $owners = foreach ($root in $Expected, $Actual) { (& find $root -printf '%y %m %u:%g %P\n' | Sort-Object -CaseSensitive) -join "`n" }
        if ($owners[0] -cne $owners[1]) { return $false }
    }
    return $true
}

# Windows counterpart of the Unix ownership and group/world-writable checks: only SYSTEM,
# Administrators, TrustedInstaller, the creator-owner placeholder and the installing account may
# own the path or be able to modify it or anything that inherits from it. Returns the first
# untrusted SID, or $null.
function Get-UntrustedWriter([string] $Path) {
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-3-0',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    # WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles,
    # WriteAttributes, Delete, ChangePermissions, TakeOwnership, GENERIC_ALL, GENERIC_WRITE.
    $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $trusted) { return $owner }
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (([int64] $rule.FileSystemRights -band $writeMask) -eq 0) { continue }
        if ($rule.IdentityReference.Value -notin $trusted) { return $rule.IdentityReference.Value }
    }
    return $null
}

function Assert-InstallRootAcl([string] $Path) {
    $untrusted = Get-UntrustedWriter $Path
    if ($untrusted) { Stop-Install "Install root is owned or writable by ${untrusted}: $Path" }
}

function Get-Options([string[]] $Arguments, [string[]] $Names) {
    $options = @{}
    for ($index = 0; $index -lt $Arguments.Count; $index += 2) {
        $name = $Arguments[$index].TrimStart('-')
        $match = $Names | Where-Object { $_ -ieq $name }
        if (-not $match) { Stop-Usage "Unknown option: $($Arguments[$index])" }
        if ($index + 1 -ge $Arguments.Count) { Stop-Usage "$($Arguments[$index]) requires a value" }
        if ($options.ContainsKey($match)) { Stop-Usage "-$match may be given only once" }
        $options[$match] = $Arguments[$index + 1]
    }
    return $options
}

function Get-HostRuntime {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($IsWindows) {
        if ($architecture -eq 'X64') { return 'win-x64' }
    } elseif ($IsLinux) {
        if ((& ldd --version 2>&1 | Out-String) -match 'musl') {
            Stop-Install 'musl/Alpine hosts are not supported by the packaged host-update CLI'
        }
        if ($architecture -eq 'X64') { return 'linux-x64' }
        if ($architecture -eq 'Arm64') { return 'linux-arm64' }
    } else {
        Stop-Install 'The packaged host-update CLI supports Linux and Windows hosts only; see docs/HOST_UPDATE_RUNBOOK.md'
    }
    Stop-Install "Unsupported host architecture for the packaged host-update CLI: $architecture"
}

function Get-Tar {
    if ($IsWindows) {
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (Test-Path -LiteralPath $tar) { return $tar }
    }
    $command = Get-Command tar -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { Stop-Install 'tar is required' }
    return $command.Source
}

function Copy-Asset([string] $Name, [string] $AssetDir, [string] $Version, [string] $WorkDir) {
    $destination = Join-Path $WorkDir $Name
    if ($AssetDir) {
        $source = Join-Path $AssetDir $Name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Link $source)) {
            Stop-Install "Release asset not found: $source"
        }
        Copy-Item -LiteralPath $source -Destination $destination
    } else {
        $uri = "https://github.com/$ReleaseRepository/releases/download/v$Version/$Name"
        try {
            Invoke-WebRequest -Uri $uri -OutFile $destination -MaximumRedirection 5 -UseBasicParsing | Out-Null
        } catch {
            Stop-Install "Could not download release asset: $Name"
        }
    }
    return $destination
}

function Assert-Members([string] $Tar, [string] $Archive) {
    $names = @(& $Tar -tzf $Archive 2>$null)
    if ($LASTEXITCODE -ne 0 -or $names.Count -eq 0) { Stop-Install 'Host-update CLI archive is unreadable or empty' }
    foreach ($name in $names) {
        if ($name -cnotmatch '^(\./)?([A-Za-z0-9._+-]+/)*[A-Za-z0-9._+-]*/?$' -or $name -match '(^|/)\.\.(/|$)') {
            Stop-Install 'Host-update CLI archive contains an unsafe member name'
        }
    }
    # Only regular files and directories: a link could redirect extraction or a later wrapper.
    foreach ($line in @(& $Tar -tvzf $Archive 2>$null)) {
        if ($line -cnotmatch '^[-d]' -or $line -match ' -> | link to ') {
            Stop-Install 'Host-update CLI archive contains a link or special file'
        }
    }
}

function Assert-Manifest([string] $Path, [string] $Version, [string] $Runtime) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Test-Link $Path)) {
        Stop-Install 'Host-update CLI package manifest is missing'
    }
    $lines = [System.IO.File]::ReadAllLines($Path)
    foreach ($expected in @('  "package": "printfarmer-host-update-cli",', "  `"version`": `"$Version`",",
            "  `"runtime`": `"$Runtime`",", '  "rolloutAuthorization": false')) {
        if ($lines -cnotcontains $expected) { Stop-Install "Host-update CLI package manifest does not match $Version/$Runtime" }
    }
}

function Invoke-Install([string[]] $Arguments) {
    $options = Get-Options $Arguments @('Version', 'AssetDir', 'InstallRoot', 'Runtime', 'TrustedRoot')
    $version = [string] $options['Version']
    if ($version -cnotmatch $VersionPattern) { Stop-Usage '-Version must be X.Y.Z or X.Y.Z-insider.N' }
    $assetDir = [string] $options['AssetDir']
    if ($assetDir -and -not (Test-FullyQualified $assetDir)) { Stop-Usage '-AssetDir must be an absolute path' }
    $offlineTrust = @()
    if ($options.ContainsKey('TrustedRoot')) {
        $trustedRoot = [string] $options['TrustedRoot']
        if (-not (Test-FullyQualified $trustedRoot)) { Stop-Usage '-TrustedRoot must be an absolute path' }
        if (-not (Test-Path -LiteralPath $trustedRoot -PathType Leaf) -or (Test-Link $trustedRoot)) {
            Stop-Install "Sigstore trusted root is not a regular file: $trustedRoot"
        }
        try {
            $stream = [System.IO.File]::OpenRead($trustedRoot)
            $trustedRootLength = $stream.Length
            $stream.Dispose()
        } catch {
            Stop-Install "Sigstore trusted root is unreadable or empty: $trustedRoot"
        }
        if ($trustedRootLength -le 0) { Stop-Install "Sigstore trusted root is unreadable or empty: $trustedRoot" }
        $offlineTrust = @('--trusted-root', $trustedRoot)
    }
    $defaultRoot = if ($IsWindows) { Join-Path $env:ProgramFiles 'PrintFarmer\HostUpdateCli' } else { '/opt/printfarmer/host-update-cli' }
    $installRoot = if ($options.ContainsKey('InstallRoot')) { [string] $options['InstallRoot'] } else { $defaultRoot }
    if (-not (Test-FullyQualified $installRoot)) { Stop-Usage '-InstallRoot must be an absolute path' }
    $runtime = [string] $options['Runtime']
    if ($runtime) {
        if ($Runtimes -cnotcontains $runtime) { Stop-Usage '-Runtime must be win-x64, linux-x64 or linux-arm64' }
    } else {
        $runtime = Get-HostRuntime
    }

    $branch = if ($version.Contains('-insider.')) { 'development' } else { 'main' }
    $cosign = Get-Command cosign -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cosign) { Stop-Install 'cosign is required to verify the host-update CLI signature' }
    $tar = Get-Tar

    $prefix = "printfarmer-host-update-cli-v$version"
    $archiveName = "$prefix-$runtime.tar.gz"
    $sumsName = "$prefix-SHA256SUMS"
    $workDir = Join-Path ([System.IO.Path]::GetTempPath()) ('printfarmer-host-update-cli-' + [guid]::NewGuid().ToString('N'))
    $stage = $null
    New-Item -ItemType Directory -Path $workDir | Out-Null
    try {
        $archive = Copy-Asset $archiveName $assetDir $version $workDir
        $sums = Copy-Asset $sumsName $assetDir $version $workDir
        $bundle = Copy-Asset "$sumsName.sigstore.json" $assetDir $version $workDir

        & $cosign.Source verify-blob @offlineTrust --bundle $bundle --certificate-oidc-issuer $OidcIssuer `
            --certificate-identity "https://github.com/$ReleaseRepository/.github/workflows/consolidated-release.yml@refs/heads/$branch" `
            $sums *> $null
        if ($LASTEXITCODE -ne 0) { Stop-Install "The host-update CLI checksum list is not signed by the $branch release workflow" }

        $text = [System.IO.File]::ReadAllText($sums)
        if (-not $text.EndsWith("`n")) { Stop-Install 'The host-update CLI checksum list is malformed' }
        $entries = @($text.Substring(0, $text.Length - 1).Split("`n"))
        if ($entries | Where-Object { $_ -cnotmatch '^[0-9a-f]{64}  [A-Za-z0-9._+-]+$' }) {
            Stop-Install 'The host-update CLI checksum list is malformed'
        }
        $matching = @($entries | Where-Object { $_.Substring(66) -ceq $archiveName })
        if ($matching.Count -ne 1) { Stop-Install "The checksum list does not name $archiveName exactly once" }
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $matching[0].Substring(0, 64)) { Stop-Install "SHA-256 mismatch for $archiveName" }
        Assert-Members $tar $archive

        if (Test-Path -LiteralPath $installRoot) {
            if (-not (Test-Path -LiteralPath $installRoot -PathType Container) -or (Test-Link $installRoot)) {
                Stop-Install "Install root is not a directory: $installRoot"
            }
        } else {
            New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
        }
        if ($IsWindows) {
            Assert-InstallRootAcl $installRoot
        } else {
            $mode = (Get-Item -LiteralPath $installRoot -Force).UnixFileMode
            if ($mode -band ([System.IO.UnixFileMode]::GroupWrite -bor [System.IO.UnixFileMode]::OtherWrite)) {
                Stop-Install "Install root is group- or world-writable: $installRoot"
            }
            $rootUid = [string] (& stat -c %u -- $installRoot)
            if ($LASTEXITCODE -ne 0 -or ($rootUid -ne '0' -and $rootUid -ne [string] (& id -u))) {
                Stop-Install "Install root is owned by another account (uid $rootUid): $installRoot"
            }
        }

        # Staged on the install root's volume so placement is a rename.
        $stage = Join-Path $installRoot (".staging." + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        if ($IsWindows) {
            & $tar -xzf $archive -C $stage
        } else {
            & $tar -xzf $archive -C $stage --no-same-owner
        }
        if ($LASTEXITCODE -ne 0) { Stop-Install "Could not extract $archiveName" }
        if (-not $IsWindows) {
            if ((& id -u) -eq '0') { & chown -R 0:0 -- $stage }
            & chmod -R go-w -- $stage
            & chmod 0755 -- $stage
        }
        Assert-Manifest (Join-Path $stage 'host-update-cli-package.json') $version $runtime

        $launcherName = if ($runtime.StartsWith('win-')) { 'Farm.HostUpdate.Cli.exe' } else { 'Farm.HostUpdate.Cli' }
        $launcher = Join-Path $stage "cli/$launcherName"
        if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { Stop-Install "The host-update CLI launcher is missing from $archiveName" }
        $helpText = try { (& $launcher help 2>&1 | Out-String) } catch { '' }
        if ($LASTEXITCODE -ne 0 -or -not $helpText.Contains('printfarmer-host-update status')) {
            Stop-Install "The host-update CLI does not run on this host ($runtime)"
        }

        # Release versions are immutable, and replacing a directory is never atomic, so an
        # existing placement is only accepted when it matches the verified archive, no entry is
        # owned or writable by an untrusted account, and its own launcher runs.
        $target = Join-Path $installRoot $version
        if (Test-Path -LiteralPath $target) {
            $accepted = (Test-Path -LiteralPath $target -PathType Container) -and -not (Test-Link $target) -and
                (Test-TreeEqual $stage $target)
            if ($accepted -and $IsWindows) {
                foreach ($entry in @(Get-Item -LiteralPath $target -Force) + @(Get-ChildItem -LiteralPath $target -Recurse -Force)) {
                    if (Get-UntrustedWriter $entry.FullName) { $accepted = $false; break }
                }
            }
            if ($accepted) {
                $targetHelp = try { (& (Join-Path $target "cli/$launcherName") help 2>&1 | Out-String) } catch { '' }
                $accepted = $LASTEXITCODE -eq 0 -and $targetHelp.Contains('printfarmer-host-update status')
            }
            if (-not $accepted) {
                Stop-Install "$target already exists and differs from the verified release; nothing was changed. Remove it (once no update or recovery needs it) and rerun"
            }
            [Console]::Error.WriteLine("The verified host-update CLI $version ($runtime) is already installed at $target")
        } else {
            try {
                [System.IO.Directory]::Move($stage, $target)
            } catch {
                Stop-Install "Could not place the host-update CLI at $target"
            }
            $stage = $null
            [Console]::Error.WriteLine("Installed the verified host-update CLI $version ($runtime) at $target")
        }
        $wrapper = if ($runtime.StartsWith('win-')) { 'printfarmer-host-update.ps1' } else { 'printfarmer-host-update.sh' }
        [Console]::Out.WriteLine((Join-Path $target $wrapper))
    } finally {
        if ($stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Nested JSON with string leaves, read by the CLI's JSON provider exactly as the equivalent
# environment variables. Values never appear in errors because they may hold credentials.
function ConvertTo-HostUpdateConfig([string] $EnvFile) {
    # Ordinal: [ordered]@{} is case-insensitive and would silently merge case-only duplicates.
    $values = [System.Collections.Specialized.OrderedDictionary]::new([System.StringComparer]::Ordinal)
    foreach ($raw in [System.IO.File]::ReadAllLines($EnvFile)) {
        $line = $raw.TrimEnd("`r")
        if ($line -match '^[ \t]*#' -or -not $line.Contains('=')) { continue }
        $separator = $line.IndexOf('=')
        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        if ($key -cnotmatch '^(DB_PROVIDER|ConnectionStrings__Default|HostUpdateExecution__.+|HostUpdates__HostState__.+)$') { continue }
        if ($value.Contains('$')) { Stop-Install "Refusing ${key}: values containing `$ are ambiguous under compose interpolation" }
        if ($value -match '[\x00-\x1f\x7f]') { Stop-Install "Refusing ${key}: value contains a control character" }
        $values[$key] = $value
    }

    $spelling = @{}
    $parents = @{}
    $leaves = @{}
    foreach ($key in $values.Keys) {
        $segments = $key -csplit '__'
        $path = ''
        for ($i = 0; $i -lt $segments.Count; $i++) {
            if ($segments[$i] -cnotmatch '^[A-Za-z0-9]+(_[A-Za-z0-9]+)*$') { Stop-Install "Refusing ${key}: malformed configuration key" }
            $path = if ($i -eq 0) { $segments[$i] } else { "$path`u{1}$($segments[$i])" }
            $lower = $path.ToLowerInvariant()
            if ($spelling.ContainsKey($lower) -and $spelling[$lower] -cne $path) {
                Stop-Install "Refusing ${key}: configuration key differs only in case from another key"
            }
            $spelling[$lower] = $path
            if ($i -lt $segments.Count - 1) { $parents[$lower] = $true }
        }
        $leaves[$path.ToLowerInvariant()] = $key
    }
    foreach ($lower in $leaves.Keys) {
        if ($parents.ContainsKey($lower)) { Stop-Install 'Refusing configuration: a key is both a value and a section' }
    }
    $rootKey = "hostupdateexecution`u{1}rootdirectory"
    if (-not $leaves.ContainsKey($rootKey) -or [string]::IsNullOrEmpty($values[$leaves[$rootKey]])) {
        throw [InstallerFailure]::new('HostUpdateExecution__RootDirectory is not configured', 3)
    }

    $document = [ordered]@{}
    foreach ($key in ([string[]] @($values.Keys) | Sort-Object { $_ -creplace '__', "`u{1}" } -CaseSensitive)) {
        $segments = $key -csplit '__'
        $node = $document
        for ($i = 0; $i -lt $segments.Count - 1; $i++) {
            if (-not $node.Contains($segments[$i])) { $node[$segments[$i]] = [ordered]@{} }
            $node = $node[$segments[$i]]
        }
        $node[$segments[-1]] = [string] $values[$key]
    }
    return [pscustomobject]@{
        Json = ($document | ConvertTo-Json -Depth 32) + "`n"
        RootDirectory = [string] $values[$leaves[$rootKey]]
    }
}

function Get-ItemOwner([string] $Path) {
    if ($IsWindows) { return (Get-Acl -LiteralPath $Path).Owner }
    $owner = & stat -c %U -- $Path 2>$null
    if ($LASTEXITCODE -ne 0) { $owner = & stat -f %Su -- $Path }
    return [string] $owner
}

function Write-ProtectedFile([string] $Path, [string] $Content, [string] $Owner) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = Join-Path $directory (".host-update.json." + [guid]::NewGuid().ToString('N'))
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Content)
    try {
        if ($IsWindows) {
            $security = [System.Security.AccessControl.FileSecurity]::new()
            $security.SetAccessRuleProtection($true, $false)
            $full = [System.Security.AccessControl.FileSystemRights]::FullControl
            foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
                $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                    [System.Security.Principal.SecurityIdentifier]::new($sid), $full, 'Allow'))
            }
            if ($Owner) {
                $account = [System.Security.Principal.NTAccount]::new($Owner).Translate([System.Security.Principal.SecurityIdentifier])
                $security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
                    $account, [System.Security.AccessControl.FileSystemRights]::Read, 'Allow'))
            }
            # Created with its final ACL, so the secret is never readable through an inherited ACE.
            $stream = [System.IO.FileSystemAclExtensions]::Create([System.IO.FileInfo]::new($temporary),
                [System.IO.FileMode]::CreateNew, [System.Security.AccessControl.FileSystemRights]::Write,
                [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::None, $security)
        } else {
            $streamOptions = [System.IO.FileStreamOptions]::new()
            $streamOptions.Mode = [System.IO.FileMode]::CreateNew
            $streamOptions.Access = [System.IO.FileAccess]::Write
            $streamOptions.UnixCreateMode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
            $stream = [System.IO.FileStream]::new($temporary, $streamOptions)
        }
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        if (-not $IsWindows) {
            & chmod 0600 -- $temporary
            if ($Owner -and $Owner -ne (& id -un)) {
                & chown -- $Owner $temporary
                if ($LASTEXITCODE -ne 0) { Stop-Install "Could not give $Path to $Owner" }
            }
        }
        [System.IO.File]::Move($temporary, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-WriteConfig([string[]] $Arguments) {
    $options = Get-Options $Arguments @('EnvFile', 'Output', 'Owner')
    $envFile = [string] $options['EnvFile']
    if (-not (Test-FullyQualified $envFile)) { Stop-Usage '-EnvFile must be an absolute path' }
    $defaultOutput = if ($IsWindows) { Join-Path $env:ProgramData 'PrintFarmer\host-update.json' } else { '/etc/printfarmer/host-update.json' }
    $output = if ($options.ContainsKey('Output')) { [string] $options['Output'] } else { $defaultOutput }
    if (-not (Test-FullyQualified $output)) { Stop-Usage '-Output must be an absolute path' }
    if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) { Stop-Install "Environment file not found: $envFile" }
    if ((Test-Link $output) -or (Test-Path -LiteralPath $output -PathType Container)) {
        Stop-Install "Refusing to replace a link or directory: $output"
    }

    $config = ConvertTo-HostUpdateConfig $envFile
    $owner = [string] $options['Owner']
    if (-not $owner) {
        $rootDirectory = [string] $config.RootDirectory
        if ((Test-FullyQualified $rootDirectory) -and (Test-Path -LiteralPath $rootDirectory -PathType Container) -and
            -not (Test-Link $rootDirectory)) {
            $owner = Get-ItemOwner $rootDirectory
        } else {
            [Console]::Error.WriteLine('HostUpdateExecution__RootDirectory is not an existing absolute, non-link directory; host-update.json is owned by the current account')
            $owner = if ($IsWindows) { [System.Security.Principal.WindowsIdentity]::GetCurrent().Name } else { [string] (& id -un) }
        }
    }
    if (-not $IsWindows -and $owner) {
        & id -u -- $owner *> $null
        if ($LASTEXITCODE -ne 0) { Stop-Install "Unknown configuration owner: $owner" }
    }
    Write-ProtectedFile $output $config.Json $owner
    $shown = if ($owner) { $owner } else { 'SYSTEM and Administrators only' }
    [Console]::Error.WriteLine("Wrote owner-only host-update configuration $output (owner $shown)")
}

function Assert-ServiceHost([string] $Command) {
    if (-not $IsWindows) {
        Stop-Install "$Command manages a Windows service; on systemd Linux hosts use install-host-update-cli.sh $Command"
    }
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Stop-Install "$Command must run from an elevated (Administrator) PowerShell"
    }
}

function Get-DaemonService {
    return Get-CimInstance -ClassName Win32_Service -Filter "Name='$DaemonServiceName'" -ErrorAction SilentlyContinue
}

function Assert-ManagedDaemonService($Service) {
    if (-not ([string] $Service.Description).Contains($DaemonServiceMarker)) {
        Stop-Install "The $DaemonServiceName service was not installed by install-service; nothing was changed"
    }
}

function Invoke-ServiceControl([string[]] $Arguments) {
    $output = & sc.exe @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Stop-Install "sc.exe $($Arguments[0]) $DaemonServiceName failed: $($output.Trim())" }
}

function Get-DaemonServiceSid {
    return [System.Security.Principal.NTAccount]::new($DaemonServiceAccount).Translate([System.Security.Principal.SecurityIdentifier])
}

# True when a broad principal (Everyone, Authenticated Users, Users, Guests, Interactive,
# Anonymous) can read the path: the Windows counterpart of a group/other-readable mode.
function Test-BroadlyReadable([string] $Path) {
    $broad = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-32-546', 'S-1-5-4', 'S-1-5-7')
    $readMask = 0x1 -bor 0x10000000 -bor 0x80000000
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl([System.IO.FileInfo]::new($Path), 'Access')
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (([int64] $rule.FileSystemRights -band $readMask) -ne 0 -and $rule.IdentityReference.Value -in $broad) { return $true }
    }
    return $false
}

# Replaces (or, with $null rights, removes) the service account's explicit ACE on a path; the
# rest of the ACL, its owner and its inheritance are left as they are.
function Set-DaemonAccess([string] $Path, $Sid, $Rights, [bool] $Inherit) {
    $info = if (Test-Path -LiteralPath $Path -PathType Container) { [System.IO.DirectoryInfo]::new($Path) } else { [System.IO.FileInfo]::new($Path) }
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl($info, 'Access')
    $acl.PurgeAccessRules($Sid)
    if ($null -ne $Rights) {
        $flags = if ($Inherit) { 'ContainerInherit, ObjectInherit' } else { 'None' }
        $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($Sid, $Rights, $flags, 'None', 'Allow'))
    }
    [System.IO.FileSystemAclExtensions]::SetAccessControl($info, $acl)
}

function Get-DaemonLogDirectory { return Join-Path $env:ProgramData 'PrintFarmer\host\daemon\logs' }

function Invoke-InstallService([string[]] $Arguments) {
    $enable = @($Arguments | Where-Object { $_ -ieq '-Enable' }).Count
    if ($enable -gt 1) { Stop-Usage '-Enable may be given only once' }
    $options = Get-Options ([string[]] @($Arguments | Where-Object { $_ -ine '-Enable' })) @('CliDir', 'Config')
    $cliDir = [string] $options['CliDir']
    if (-not $cliDir) { Stop-Usage '-CliDir is required' }
    if (-not (Test-FullyQualified $cliDir)) { Stop-Usage '-CliDir must be an absolute path' }
    $config = [string] $options['Config']
    if ($options.ContainsKey('Config') -and -not (Test-FullyQualified $config)) { Stop-Usage '-Config must be an absolute path' }
    Assert-ServiceHost 'install-service'
    if (-not $config) { $config = Join-Path $env:ProgramData 'PrintFarmer\host-update.json' }

    $cliDir = $cliDir.TrimEnd('\', '/')
    $launcher = Join-Path $cliDir 'cli\Farm.HostUpdate.Cli.exe'
    foreach ($path in $cliDir, (Join-Path $cliDir 'cli')) {
        if (-not (Test-Path -LiteralPath $path -PathType Container) -or (Test-Link $path)) {
            Stop-Install "Not an installed host-update CLI directory: $cliDir"
        }
    }
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) -or (Test-Link $launcher)) {
        Stop-Install "The host-update CLI launcher is missing: $launcher"
    }
    $manifest = Join-Path $cliDir 'host-update-cli-package.json'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or (Test-Link $manifest)) {
        Stop-Install "Not an installed host-update CLI package (install it with the install command first): $cliDir"
    }
    $manifestLines = [System.IO.File]::ReadAllLines($manifest)
    foreach ($expected in @('  "package": "printfarmer-host-update-cli",', '  "rolloutAuthorization": false')) {
        if ($manifestLines -cnotcontains $expected) {
            Stop-Install "Not an installed host-update CLI package (install it with the install command first): $cliDir"
        }
    }
    # The service runs this binary, so nobody but SYSTEM, Administrators or the installer may change it.
    foreach ($path in $cliDir, (Join-Path $cliDir 'cli'), $launcher) {
        $untrusted = Get-UntrustedWriter $path
        if ($untrusted) { Stop-Install "The host-update CLI is owned or writable by ${untrusted}: $path" }
    }

    if (-not (Test-Path -LiteralPath $config -PathType Leaf) -or (Test-Link $config)) {
        Stop-Install "Host-update configuration not found (run write-config first): $config"
    }
    $untrustedConfig = Get-UntrustedWriter $config
    if ($untrustedConfig) { Stop-Install "Host-update configuration is owned or writable by ${untrustedConfig}: $config" }
    if (Test-BroadlyReadable $config) { Stop-Install "Host-update configuration is readable by a broad group: $config" }
    $rootDirectory = try {
        [string] (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -AsHashtable)['HostUpdateExecution']['RootDirectory']
    } catch { '' }
    if (-not (Test-FullyQualified $rootDirectory) -or -not (Test-Path -LiteralPath $rootDirectory -PathType Container) -or
        (Test-Link $rootDirectory)) {
        Stop-Install "HostUpdateExecution:RootDirectory in $config is not an existing absolute, non-link directory"
    }
    $stateDirectory = Join-Path $rootDirectory 'state'
    if (Test-Link $stateDirectory) { Stop-Install "Refusing a link as the daemon state directory: $stateDirectory" }
    if (-not (Test-Path -LiteralPath $stateDirectory)) { New-Item -ItemType Directory -Path $stateDirectory | Out-Null }
    if (-not (Test-Path -LiteralPath $stateDirectory -PathType Container)) { Stop-Install "The daemon state directory is not a directory: $stateDirectory" }

    $binaryPath = "`"$launcher`" --config `"$config`" daemon --windows-service"
    $description = "Supervises the enrolled PrintFarmer host-update daemon. Installing it grants nothing: daemon execution stays disabled pending #2982. $DaemonServiceMarker"
    $service = Get-DaemonService
    $changed = $true
    if ($service) {
        Assert-ManagedDaemonService $service
        $changed = ([string] $service.PathName) -cne $binaryPath -or ([string] $service.StartName) -ine $DaemonServiceAccount
        if (([string] $service.PathName) -cne $binaryPath) {
            $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $binaryPath }
            if ($result.ReturnValue -ne 0) { Stop-Install "Could not update the $DaemonServiceName service (error $($result.ReturnValue))" }
        }
    } else {
        # Created disabled and stopped; only -Enable (or an operator) ever starts it.
        New-Service -Name $DaemonServiceName -BinaryPathName $binaryPath -DisplayName 'PrintFarmer host-update daemon' `
            -Description $description -StartupType Disabled | Out-Null
    }
    try {
        # A per-service virtual account: no password, no interactive logon, no group membership
        # beyond the built-in service identity, and only the ACEs granted below.
        Invoke-ServiceControl @('config', $DaemonServiceName, 'obj=', $DaemonServiceAccount)
        Invoke-ServiceControl @('description', $DaemonServiceName, $description)
        Invoke-ServiceControl @('sidtype', $DaemonServiceName, 'unrestricted')
        Invoke-ServiceControl @('privs', $DaemonServiceName, 'SeChangeNotifyPrivilege')
        Invoke-ServiceControl @('failure', $DaemonServiceName, 'reset=', '86400', 'actions=', 'restart/30000/restart/30000//')
    } catch {
        if (-not $service) { & sc.exe delete $DaemonServiceName *> $null }
        throw
    }

    $sid = Get-DaemonServiceSid
    $logDirectory = Get-DaemonLogDirectory
    if (Test-Link $logDirectory) { Stop-Install "Refusing a link as the daemon log directory: $logDirectory" }
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    $logSecurity = [System.Security.AccessControl.DirectorySecurity]::new()
    $logSecurity.SetAccessRuleProtection($true, $false)
    foreach ($admin in @('S-1-5-18', 'S-1-5-32-544')) {
        $logSecurity.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new($admin), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    $logSecurity.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    [System.IO.FileSystemAclExtensions]::SetAccessControl([System.IO.DirectoryInfo]::new($logDirectory), $logSecurity)
    Set-DaemonAccess $config $sid ([System.Security.AccessControl.FileSystemRights]::Read) $false
    Set-DaemonAccess $rootDirectory $sid ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute) $false
    Set-DaemonAccess $stateDirectory $sid ([System.Security.AccessControl.FileSystemRights]::Modify) $true

    if ($changed) {
        # A running daemon picks up the new configuration; a stopped one stays stopped.
        if ($service -and $service.State -eq 'Running') { Restart-Service -Name $DaemonServiceName -Force }
        [Console]::Error.WriteLine("Installed the $DaemonServiceName service (runs as $DaemonServiceAccount)")
    } else {
        [Console]::Error.WriteLine("The $DaemonServiceName service is already installed (runs as $DaemonServiceAccount)")
    }
    if ($enable -eq 1) {
        Set-Service -Name $DaemonServiceName -StartupType Automatic
        Start-Service -Name $DaemonServiceName
        [Console]::Error.WriteLine("Enabled and started $DaemonServiceName (daemon execution remains disabled pending #2982)")
    } else {
        [Console]::Error.WriteLine("$DaemonServiceName was not enabled or started. Start it with: Set-Service $DaemonServiceName -StartupType Automatic; Start-Service $DaemonServiceName")
    }
}

function Invoke-UninstallService([string[]] $Arguments) {
    if ($Arguments.Count -gt 0) { Stop-Usage "Unknown uninstall-service option: $($Arguments[0])" }
    Assert-ServiceHost 'uninstall-service'
    $service = Get-DaemonService
    if (-not $service) {
        [Console]::Error.WriteLine("$DaemonServiceName is not installed; nothing to remove")
        return
    }
    Assert-ManagedDaemonService $service
    # Resolved before deletion: the virtual account stops translating once the service is gone.
    $sid = Get-DaemonServiceSid
    $config = if (([string] $service.PathName) -match '--config "([^"]+)"') { $Matches[1] } else { $null }
    if ($service.State -ne 'Stopped') { Stop-Service -Name $DaemonServiceName -Force }
    Invoke-ServiceControl @('delete', $DaemonServiceName)
    $paths = @(Get-DaemonLogDirectory)
    if ($config -and (Test-Path -LiteralPath $config -PathType Leaf) -and -not (Test-Link $config)) {
        $paths += $config
        $rootDirectory = try {
            [string] (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -AsHashtable)['HostUpdateExecution']['RootDirectory']
        } catch { '' }
        if ((Test-FullyQualified $rootDirectory) -and -not (Test-Link $rootDirectory)) {
            $paths += @($rootDirectory, (Join-Path $rootDirectory 'state'))
        }
    }
    foreach ($path in $paths) {
        if ((Test-Path -LiteralPath $path) -and -not (Test-Link $path)) { Set-DaemonAccess $path $sid $null $false }
    }
    [Console]::Error.WriteLine("Removed $DaemonServiceName; the configuration, journal, identity storage and logs were kept")
}

try {
    $rawArgs = @($args | ForEach-Object { [string] $_ })
    if ($rawArgs.Count -lt 1) { Stop-Usage 'A command is required' }
    $rest = [string[]] @($rawArgs | Select-Object -Skip 1)
    switch -Exact ($rawArgs[0].ToLowerInvariant()) {
        'install' { Invoke-Install $rest }
        'write-config' { Invoke-WriteConfig $rest }
        'install-service' { Invoke-InstallService $rest }
        'uninstall-service' { Invoke-UninstallService $rest }
        { $_ -in @('help', '-help', '--help', '-h', '-?') } { [Console]::Out.WriteLine($UsageText) }
        default { Stop-Usage "Unknown command: $($rawArgs[0])" }
    }
    exit 0
} catch [InstallerFailure] {
    [Console]::Error.WriteLine("install-host-update-cli: $($_.Exception.Message)")
    exit $_.Exception.ExitCode
} catch {
    [Console]::Error.WriteLine("install-host-update-cli: $($_.Exception.Message)")
    exit 1
}
