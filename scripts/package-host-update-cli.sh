#!/usr/bin/env bash
# Package the host-local recovery CLI (Farm.HostUpdate.Cli) as versioned, self-contained,
# per-platform release archives plus one SHA256SUMS file (issue #2997).
#
# Each archive contains one top-level directory:
#   printfarmer-host-update-cli-<tag>-<rid>/
#     printfarmer-host-update.sh     fixed-operation Bash wrapper
#     printfarmer-host-update.ps1    fixed-operation PowerShell wrapper
#     common-utils.sh                logging helpers the Bash wrapper sources
#     cli/                           self-contained publish; Farm.HostUpdate.Cli[.exe] apphost
#     host-update-cli.json           package identity and per-file SHA-256 inventory
#     LICENSE, THIRD-PARTY-NOTICES.md
#
# Linux and macOS archives are .tar.gz; Windows archives are .zip. No .NET runtime is needed on
# the host. The package never authorizes rollout or runtime auto-update; it only carries the
# status/recover tool described in docs/HOST_UPDATE_RUNBOOK.md.
#
# Usage:
#   scripts/package-host-update-cli.sh --output <dir> --version <X.Y.Z[-insider.N]> \
#     --channel <stable|insider> --source-commit <40-hex> [--source <repo-root>] \
#     [--rid <rid>]... [--require-release-identity]
#
#   --source                    repository checkout to package (default: this script's repository)
#   --rid                       repeatable; default: every supported RID
#   --require-release-identity  fail unless <source>/src/ReleaseIdentity.props stamps --version
#                               (the release pipeline writes it before packaging)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

readonly SUPPORTED_RIDS=(linux-x64 linux-arm64 osx-arm64 win-x64)
readonly CLI_PROJECT='src/tools/Farm.HostUpdate.Cli/Farm.HostUpdate.Cli.csproj'
readonly CLI_NAME='Farm.HostUpdate.Cli'
readonly VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+(-insider\.[0-9]+)?$'
readonly SAFE_PATH_RE='^[A-Za-z0-9._+/-]+$'

usage() {
    sed -n '18,26p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

die() {
    log_error "package-host-update-cli: $1" >&2
    exit 1
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

source_dir="$(cd "$SCRIPT_DIR/.." && pwd)"
output=""
version=""
channel=""
source_commit=""
require_identity=false
rids=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source) [[ $# -ge 2 ]] || die "--source requires a value"; source_dir="$2"; shift 2 ;;
        --output) [[ $# -ge 2 ]] || die "--output requires a value"; output="$2"; shift 2 ;;
        --version) [[ $# -ge 2 ]] || die "--version requires a value"; version="$2"; shift 2 ;;
        --channel) [[ $# -ge 2 ]] || die "--channel requires a value"; channel="$2"; shift 2 ;;
        --source-commit) [[ $# -ge 2 ]] || die "--source-commit requires a value"; source_commit="$2"; shift 2 ;;
        --rid) [[ $# -ge 2 ]] || die "--rid requires a value"; rids+=("$2"); shift 2 ;;
        --require-release-identity) require_identity=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; die "unknown argument: $1" ;;
    esac
done

[[ -n "$output" ]] || die "--output is required"
[[ "$version" =~ $VERSION_RE ]] || die "--version must be X.Y.Z or X.Y.Z-insider.N"
case "$channel" in
    stable) [[ "$version" != *-insider.* ]] || die "stable packages cannot carry an insider version" ;;
    insider) [[ "$version" == *-insider.* ]] || die "insider packages require an X.Y.Z-insider.N version" ;;
    *) die "--channel must be stable or insider" ;;
esac
[[ "$source_commit" =~ ^[a-f0-9]{40}$ ]] || die "--source-commit must be a full lowercase commit SHA"
[[ ${#rids[@]} -gt 0 ]] || rids=("${SUPPORTED_RIDS[@]}")
for rid in "${rids[@]}"; do
    [[ " ${SUPPORTED_RIDS[*]} " == *" $rid "* ]] || die "unsupported RID '$rid' (supported: ${SUPPORTED_RIDS[*]})"
done

source_dir="$(cd "$source_dir" && pwd -P)" || die "source directory not found"
[[ -f "$source_dir/$CLI_PROJECT" ]] ||
    die "source has no $CLI_PROJECT; this source cannot produce a host-update CLI package"
for file in scripts/printfarmer-host-update.sh scripts/printfarmer-host-update.ps1 scripts/common-utils.sh \
    LICENSE THIRD-PARTY-NOTICES.md; do
    [[ -f "$source_dir/$file" ]] || die "source is missing $file"
done
head_commit="$(git -C "$source_dir" rev-parse HEAD 2>/dev/null)" || die "source is not a git checkout"
[[ "$head_commit" == "$source_commit" ]] || die "source HEAD $head_commit does not match --source-commit"
source_version="$(tr -d '[:space:]' < "$source_dir/VERSION")"
[[ "${source_version#v}" == "${version%%-*}" ]] ||
    die "--version base does not match the source VERSION file"
identity_props="$source_dir/src/ReleaseIdentity.props"
if [[ -f "$identity_props" ]]; then
    grep -qF "<Version>$version</Version>" "$identity_props" ||
        die "src/ReleaseIdentity.props stamps a different version than --version"
elif [[ "$require_identity" == true ]]; then
    die "src/ReleaseIdentity.props is required but missing"
else
    log_warn "package-host-update-cli: no src/ReleaseIdentity.props; assemblies carry the development version"
fi

command -v dotnet >/dev/null 2>&1 || die "dotnet SDK is required"
command -v zip >/dev/null 2>&1 || [[ " ${rids[*]} " != *" win-"* ]] || die "zip is required for Windows packages"

tag="v$version"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
sums_name="printfarmer-host-update-cli-$tag-SHA256SUMS"
[[ ! -e "$output/$sums_name" ]] || die "$output/$sums_name already exists; refusing to overwrite"

staging="$(mktemp -d "${TMPDIR:-/tmp}/pf-host-update-cli.XXXXXX")"
trap 'rm -rf "$staging"' EXIT

# Deterministic archive metadata: every entry takes the source commit's timestamp.
source_epoch="$(git -C "$source_dir" log -1 --format=%ct "$source_commit")"
# touch -t is interpreted in local time, so format the stamp in local time too.
touch_stamp="$(date -r "$source_epoch" +%Y%m%d%H%M.%S 2>/dev/null || date -d "@$source_epoch" +%Y%m%d%H%M.%S)"
tar_flags=()
if tar --version 2>/dev/null | grep -q 'GNU tar'; then
    tar_flags=(--sort=name --owner=0 --group=0 --numeric-owner --mtime="@$source_epoch")
fi

write_manifest() {
    local package_dir="$1" rid="$2" entrypoint="$3"
    local manifest="$package_dir/host-update-cli.json" first=true path
    {
        printf '{\n'
        printf '  "schema": 1,\n'
        printf '  "name": "printfarmer-host-update-cli",\n'
        printf '  "version": "%s",\n' "$version"
        printf '  "tag": "%s",\n' "$tag"
        printf '  "channel": "%s",\n' "$channel"
        printf '  "sourceCommit": "%s",\n' "$source_commit"
        printf '  "rid": "%s",\n' "$rid"
        printf '  "selfContained": true,\n'
        printf '  "cli": "cli/%s",\n' "$entrypoint"
        printf '  "wrappers": ["printfarmer-host-update.sh", "printfarmer-host-update.ps1"],\n'
        printf '  "authorizesRollout": false,\n'
        printf '  "documentation": "https://github.com/OlyForge3D/PrintFarmer/blob/%s/docs/HOST_UPDATE_RUNBOOK.md",\n' "$source_commit"
        printf '  "files": {'
        while IFS= read -r path; do
            [[ "$path" =~ $SAFE_PATH_RE ]] || die "unexpected file name in package: $path"
            if [[ "$first" == true ]]; then first=false; else printf ','; fi
            printf '\n    "%s": "%s"' "$path" "$(sha256_of "$package_dir/$path")"
        done < <(cd "$package_dir" && find . -type f ! -name host-update-cli.json | sed 's|^\./||' | LC_ALL=C sort)
        printf '\n  }\n}\n'
    } > "$manifest"
}

sums=()
for rid in "${rids[@]}"; do
    name="printfarmer-host-update-cli-$tag-$rid"
    package_dir="$staging/$name"
    entrypoint="$CLI_NAME"
    [[ "$rid" == win-* ]] && entrypoint="$CLI_NAME.exe"
    archive="$name.tar.gz"
    [[ "$rid" == win-* ]] && archive="$name.zip"
    [[ ! -e "$output/$archive" ]] || die "$output/$archive already exists; refusing to overwrite"

    log_info "Publishing $CLI_NAME for $rid"
    mkdir -p "$package_dir/cli"
    # Self-contained keeps the host free of a .NET runtime dependency. Trimming and single-file
    # are deliberately off: EF Core and native providers are not trim-safe.
    dotnet publish "$source_dir/$CLI_PROJECT" -c Release -r "$rid" --self-contained true \
        -p:UseAppHost=true -p:SatelliteResourceLanguages=en -p:DebugType=none \
        -p:ContinuousIntegrationBuild=true \
        -m:1 -nodeReuse:false -p:UseSharedCompilation=false \
        -o "$package_dir/cli" --nologo -v quiet
    [[ -f "$package_dir/cli/$entrypoint" ]] || die "publish for $rid produced no cli/$entrypoint apphost"
    [[ ! -e "$package_dir/cli/$CLI_NAME.pdb" ]] || die "publish for $rid unexpectedly included symbols"

    cp "$source_dir/scripts/printfarmer-host-update.sh" "$source_dir/scripts/printfarmer-host-update.ps1" \
        "$source_dir/scripts/common-utils.sh" "$source_dir/LICENSE" "$source_dir/THIRD-PARTY-NOTICES.md" \
        "$package_dir/"
    chmod -R u=rwX,go=rX "$package_dir"
    chmod 0755 "$package_dir/printfarmer-host-update.sh" "$package_dir/cli/$entrypoint"
    write_manifest "$package_dir" "$rid" "$entrypoint"
    chmod 0644 "$package_dir/host-update-cli.json"

    find "$package_dir" -exec touch -h -t "$touch_stamp" {} +
    if [[ "$rid" == win-* ]]; then
        (cd "$staging" && find "$name" -print | LC_ALL=C sort | TZ=UTC zip -X -q -@ "$output/$archive")
    else
        (cd "$staging" && tar ${tar_flags[@]+"${tar_flags[@]}"} -cf - "$name" | gzip -9 -n > "$output/$archive")
    fi
    sums+=("$(sha256_of "$output/$archive")  $archive")
    rm -rf "$package_dir"
    log_success "Packaged $archive"
done

printf '%s\n' "${sums[@]}" | LC_ALL=C sort -k2 > "$output/$sums_name"
log_success "Wrote $sums_name"
