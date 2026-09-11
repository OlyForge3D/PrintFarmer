[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('probe', 'init', 'run', 'deploy', 'phase-a', 'phase-b', 'status', 'read', 'cleanup')]
    [string] $Command,
    [string] $RunId,
    [string] $Evidence
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'wsl-transport.ps1')
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine harness revision' }
$dirty = @(& git -C $repo status --porcelain -- scripts/ci/daily-validation* scripts/ci/wsl-transport.ps1).Count -gt 0
$files = @{}
foreach ($name in @('daily-validation.py', 'daily-validation-reporter.mjs')) {
    $files[$name] = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $name)))
}
$bundle = @{ files = $files; revision = $revision.Trim(); dirty = $dirty } | ConvertTo-Json -Compress
$bundle64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bundle))
$script = @'
set -euo pipefail
exec /usr/bin/python3 - "$@" <<'PY'
import base64, hashlib, json, os, pathlib, sys
bundle = json.loads(base64.b64decode(sys.argv[1], validate=True))
files = {k: base64.b64decode(v, validate=True) for k, v in bundle["files"].items()}
identity = hashlib.sha256(b"".join(k.encode() + files[k] for k in sorted(files))).hexdigest()
root = pathlib.Path.home() / ".local/share/printfarmer-daily/harness" / identity
os.umask(0o077)
root.mkdir(parents=True, exist_ok=True)
if root.resolve() != root or root.stat().st_uid != os.getuid():
    raise RuntimeError("Unsafe harness directory")
for name, content in files.items():
    target = root / name
    try:
        with target.open("xb") as stream:
            stream.write(content)
    except FileExistsError:
        if target.is_symlink() or target.read_bytes() != content:
            raise RuntimeError("Harness content mismatch")
os.environ["PF_DAILY_HARNESS_REVISION"] = bundle["revision"]
os.environ["PF_DAILY_HARNESS_DIRTY"] = str(bundle["dirty"]).lower()
os.environ["PF_DAILY_HARNESS_HASH"] = identity
os.execv("/usr/bin/python3", ["python3", str(root / "daily-validation.py"), *sys.argv[2:]])
PY
'@
$arguments = @($bundle64, $Command)
if ($RunId) { $arguments += @('--run-id', $RunId) }
if ($Evidence) { $arguments += @('--evidence', $Evidence) }
exit (Invoke-NativeWsl -Script $script.Replace("`r`n", "`n") -Arguments $arguments)
