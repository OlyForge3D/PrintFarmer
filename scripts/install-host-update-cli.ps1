#Requires -Version 7.0
<#
.SYNOPSIS
    Installs the signed, self-contained host-update recovery CLI and generates its host
    configuration (issue #3045).

.DESCRIPTION
    It never builds the CLI and never falls back to an unverified source. It is NOT rollout
    authorization: it neither enables nor starts an update.

      install-host-update-cli.ps1 install -Version <X.Y.Z[-insider.N]> [-AssetDir <abs-dir>] [-InstallRoot <abs-dir>] [-Runtime <win-x64|linux-x64|linux-arm64>]
      install-host-update-cli.ps1 write-config -EnvFile <abs-file> [-Output <abs-file>] [-Owner <account>]

    install       Downloads (or reads from -AssetDir) the runtime's archive, the checksum list and
                  its Cosign bundle; verifies the bundle against the release workflow identity for
                  the version's channel, the archive SHA-256, its members and package manifest;
                  proves the CLI launches; then places it at <InstallRoot>\<version> (default
                  C:\Program Files\PrintFarmer\HostUpdateCli, or /opt/printfarmer/host-update-cli).
                  Requires cosign on PATH.
    write-config  Writes host-update.json (default C:\ProgramData\PrintFarmer\host-update.json, or
                  /etc/printfarmer/host-update.json) from the deployment .env's
                  HostUpdateExecution__*, HostUpdates__HostState__*, DB_PROVIDER and
                  ConnectionStrings__Default. On Windows inheritance is removed and only SYSTEM,
                  Administrators (full control) and the owner account (read) have access; elsewhere
                  the file is mode 0600. The owner defaults to the owner of
                  HostUpdateExecution__RootDirectory when it exists.

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
$UsageText = @'
usage:
  install-host-update-cli.ps1 install -Version <X.Y.Z[-insider.N]> [-AssetDir <abs-dir>] [-InstallRoot <abs-dir>] [-Runtime <win-x64|linux-x64|linux-arm64>]
  install-host-update-cli.ps1 write-config -EnvFile <abs-file> [-Output <abs-file>] [-Owner <account>]
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

function Get-Options([string[]] $Arguments, [string[]] $Names) {
    $options = @{}
    for ($index = 0; $index -lt $Arguments.Count; $index += 2) {
        $name = $Arguments[$index].TrimStart('-')
        $match = $Names | Where-Object { $_ -ieq $name }
        if (-not $match) { Stop-Usage "Unknown option: $($Arguments[$index])" }
        if ($index + 1 -ge $Arguments.Count) { Stop-Usage "$($Arguments[$index]) requires a value" }
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
    $options = Get-Options $Arguments @('Version', 'AssetDir', 'InstallRoot', 'Runtime')
    $version = [string] $options['Version']
    if ($version -cnotmatch $VersionPattern) { Stop-Usage '-Version must be X.Y.Z or X.Y.Z-insider.N' }
    $assetDir = [string] $options['AssetDir']
    if ($assetDir -and -not (Test-FullyQualified $assetDir)) { Stop-Usage '-AssetDir must be an absolute path' }
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

        & $cosign.Source verify-blob --bundle $bundle --certificate-oidc-issuer $OidcIssuer `
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
        if (-not $IsWindows) {
            $mode = (Get-Item -LiteralPath $installRoot -Force).UnixFileMode
            if ($mode -band ([System.IO.UnixFileMode]::GroupWrite -bor [System.IO.UnixFileMode]::OtherWrite)) {
                Stop-Install "Install root is group- or world-writable: $installRoot"
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

        $target = Join-Path $installRoot $version
        $previous = $null
        if (Test-Path -LiteralPath $target) {
            $previous = Join-Path $installRoot ".$version.previous.$PID"
            Move-Item -LiteralPath $target -Destination $previous
        }
        try {
            Move-Item -LiteralPath $stage -Destination $target
        } catch {
            if ($previous) { Move-Item -LiteralPath $previous -Destination $target -ErrorAction SilentlyContinue }
            Stop-Install "Could not place the host-update CLI at $target"
        }
        $stage = $null
        if ($previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
        [Console]::Error.WriteLine("Installed the verified host-update CLI $version ($runtime) at $target")
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
        if (Test-Path -LiteralPath $config.RootDirectory) {
            $owner = Get-ItemOwner $config.RootDirectory
        } elseif (-not $IsWindows) {
            $owner = [string] (& id -un)
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

try {
    $rawArgs = @($args | ForEach-Object { [string] $_ })
    if ($rawArgs.Count -lt 1) { Stop-Usage 'A command is required' }
    $rest = [string[]] @($rawArgs | Select-Object -Skip 1)
    switch -Exact ($rawArgs[0].ToLowerInvariant()) {
        'install' { Invoke-Install $rest }
        'write-config' { Invoke-WriteConfig $rest }
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
