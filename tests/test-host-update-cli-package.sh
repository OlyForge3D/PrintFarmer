#!/usr/bin/env bash
# Smoke-tests one packaged host-update CLI archive (issue #2997) on the current Linux/macOS host.
#
#   tests/test-host-update-cli-package.sh --assets <dir> --tag <vX.Y.Z[-insider.N]> --rid <rid>
#
# Verifies the archive against the package SHA256SUMS and the per-file inventory in
# host-update-cli.json, then runs the packaged Bash wrapper (and the PowerShell wrapper when pwsh
# is available) against a throwaway host-update root. No .NET SDK or runtime is used: the
# package's self-contained apphost must run on its own. Nothing is started or rolled back: the
# root has no journal, and the fake docker/sqlite3 files are never executed.
#
# The root lives under $RUNNER_TEMP (CI) or $HOME, because the CLI refuses a root inside the OS
# temp directory or the working directory.
set -euo pipefail

assets=""
tag=""
rid=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --assets) assets="$2"; shift 2 ;;
        --tag) tag="$2"; shift 2 ;;
        --rid) rid="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
[[ -d "$assets" && -n "$tag" && -n "$rid" ]] || { echo "usage: $0 --assets <dir> --tag <tag> --rid <rid>" >&2; exit 2; }
[[ "$rid" != win-* ]] || { echo "use tests/test-host-update-cli-package.ps1 for Windows packages" >&2; exit 2; }

assets="$(cd "$assets" && pwd)"
name="printfarmer-host-update-cli-$tag-$rid"
archive="$name.tar.gz"
sums="printfarmer-host-update-cli-$tag-SHA256SUMS"
pass=0
fail=0

ok() { echo "[PASS] $1"; pass=$((pass + 1)); }
bad() { echo "[FAIL] $1" >&2; fail=$((fail + 1)); }
sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi
}

work="$(mktemp -d "${TMPDIR:-/tmp}/pf-host-update-pkg.XXXXXX")"
root_parent="${RUNNER_TEMP:-$HOME}"
root="$(mktemp -d "$root_parent/pf-host-update-root.XXXXXX")"
trap 'rm -rf "$work" "$root"' EXIT

# 1. Checksum and archive shape.
expected="$(awk -v f="$archive" '$2 == f { print $1 }' "$assets/$sums")"
if [[ -n "$expected" && "$(sha256_of "$assets/$archive")" == "$expected" ]]; then
    ok "archive matches $sums"
else
    bad "archive does not match $sums"
fi
tar -xzf "$assets/$archive" -C "$work"
[[ "$(ls "$work")" == "$name" ]] && ok "archive has one top-level $name directory" || bad "unexpected archive top level: $(ls "$work")"
package="$work/$name"

# 2. Package identity and per-file inventory.
manifest="$package/host-update-cli.json"
grep -q "\"tag\": \"$tag\"" "$manifest" && grep -q "\"rid\": \"$rid\"" "$manifest" &&
    grep -q '"authorizesRollout": false' "$manifest" && ok "host-update-cli.json identifies $tag $rid" ||
    bad "host-update-cli.json identity mismatch"
listed="$(sed -n 's/^    "\([^"]*\)": "\([a-f0-9]\{64\}\)",\{0,1\}$/\1 \2/p' "$manifest")"
inventory_ok=true
while read -r path hash; do
    [[ -f "$package/$path" && "$(sha256_of "$package/$path")" == "$hash" ]] || { inventory_ok=false; echo "  bad entry: $path" >&2; }
done <<< "$listed"
actual="$(cd "$package" && find . -type f ! -name host-update-cli.json | sed 's|^\./||' | LC_ALL=C sort)"
[[ "$actual" == "$(awk '{print $1}' <<< "$listed" | LC_ALL=C sort)" ]] || { inventory_ok=false; echo "  file list differs from inventory" >&2; }
[[ "$inventory_ok" == true ]] && ok "every packaged file matches the inventory" || bad "package inventory mismatch"
[[ -x "$package/printfarmer-host-update.sh" && -x "$package/cli/Farm.HostUpdate.Cli" && -f "$package/common-utils.sh" &&
    -f "$package/printfarmer-host-update.ps1" && -f "$package/LICENSE" && -f "$package/THIRD-PARTY-NOTICES.md" ]] &&
    ok "wrappers, apphost and notices present" || bad "package layout incomplete"

# 3. Throwaway host root that satisfies the namespace proof.
mkdir -p "$root/state" "$root/tools"
owned=(app-data model-uploads gcode-storage slicer-profiles data-protection-keys)
for dir in "${owned[@]}"; do mkdir -p "$root/owned/$dir"; done
echo 'services: {}' > "$root/docker-compose.yml"
: > "$root/tools/docker"
: > "$root/tools/sqlite3"
: > "$root/farm.db"
config="$root/host-update.json"
{
    printf '{ "DB_PROVIDER": "sqlite", "ConnectionStrings": { "Default": "Data Source=%s/farm.db" },\n' "$root"
    printf '  "HostUpdateExecution": { "RootDirectory": "%s", "ComposeFiles": ["%s/docker-compose.yml"],\n' "$root" "$root"
    printf '    "HostExecutablePaths": { "docker": "%s/tools/docker", "sqlite3": "%s/tools/sqlite3" },\n' "$root" "$root"
    printf '    "OwnedDirectories": {'
    sep=""
    for dir in "${owned[@]}"; do printf '%s "%s": "%s/owned/%s"' "$sep" "$dir" "$root" "$dir"; sep=","; done
    printf ' } } }\n'
} > "$config"

# The CLI must not depend on an SDK or runtime on PATH, nor on a caller-supplied CLI directory.
run_wrapper() {
    (cd "$work" && env -u PRINTFARMER_HOST_UPDATE_CLI_DIR -u PRINTFARMER_DOTNET -u DOTNET_ROOT \
        PATH="/usr/bin:/bin" "$package/printfarmer-host-update.sh" "$@")
}
expect_exit() {
    local want="$1" label="$2"; shift 2
    local got=0 output
    output="$(run_wrapper "$@" 2>&1)" || got=$?
    if [[ "$got" == "$want" ]]; then ok "$label (exit $got)"; else bad "$label (exit $got, want $want)"; echo "$output" >&2; fi
}

expect_exit 0 "bash wrapper help" help
expect_exit 0 "bash wrapper status on an empty root" --config "$config" status --json
expect_exit 5 "bash wrapper recover --preview reports no history" --config "$config" recover --release stable:1.2.3 --preview --json
expect_exit 2 "bash wrapper refuses a relative config" --config host-update.json status
rm "$root/docker-compose.yml"
# --confirm runs the same-namespace proof before reading any state, so this exercises the real
# proof in the packaged binary without anything to roll back.
expect_exit 3 "namespace proof refuses --confirm without the compose file" --config "$config" recover --release stable:1.2.3 --confirm stable:1.2.3 --json
echo 'services: {}' > "$root/docker-compose.yml"

if command -v pwsh >/dev/null 2>&1; then
    got=0
    (cd "$work" && env -u PRINTFARMER_HOST_UPDATE_CLI_DIR -u PRINTFARMER_DOTNET \
        pwsh -NoProfile -NonInteractive -File "$package/printfarmer-host-update.ps1" -Config "$config" status -Json) >/dev/null 2>&1 || got=$?
    [[ "$got" == 0 ]] && ok "PowerShell wrapper status (exit 0)" || bad "PowerShell wrapper status (exit $got, want 0)"
fi

echo "$pass passed, $fail failed"
[[ "$fail" == 0 ]]
