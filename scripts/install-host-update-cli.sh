#!/usr/bin/env bash
# Installs the signed, self-contained host-update recovery CLI and generates its host
# configuration (issue #3045). It never builds the CLI and never falls back to an unverified
# source. It is NOT rollout authorization: it neither enables nor starts an update.
#
#   install-host-update-cli.sh install --version <X.Y.Z[-insider.N]> [--asset-dir <abs-dir>] [--install-root <abs-dir>] [--runtime <linux-x64|linux-arm64>]
#   install-host-update-cli.sh write-config --env-file <abs-file> [--output <abs-file>] [--owner <user>]
#
# install       Downloads (or reads from --asset-dir) the runtime's archive, the checksum list and
#               its Cosign bundle; verifies the bundle against the release workflow identity for
#               the version's channel, the archive SHA-256, its members and package manifest;
#               proves the CLI launches; then places it at <install-root>/<version> (default
#               /opt/printfarmer/host-update-cli). Versions are immutable: an existing placement
#               identical to the verified archive is accepted, a differing one is refused and left
#               untouched. The install root must not be group- or world-writable. Requires cosign.
# write-config  Writes an owner-only (0600) host-update.json (default /etc/printfarmer/host-update.json)
#               from the deployment .env's HostUpdateExecution__*, HostUpdates__HostState__*,
#               DB_PROVIDER and ConnectionStrings__Default. The owner defaults to the owner of
#               HostUpdateExecution__RootDirectory when it is an absolute, non-link directory,
#               otherwise the current user.
#
# Exit codes: 0 done; 1 verification, validation or installation failed (nothing placed or
# written); 2 usage; 3 write-config only: HostUpdateExecution__RootDirectory is not configured,
# so nothing was written.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

readonly VERSION_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-insider\.(0|[1-9][0-9]*))?$'
readonly RELEASE_REPOSITORY='OlyForge3D/PrintFarmer'
readonly OIDC_ISSUER='https://token.actions.githubusercontent.com'
readonly DEFAULT_INSTALL_ROOT='/opt/printfarmer/host-update-cli'
readonly DEFAULT_CONFIG='/etc/printfarmer/host-update.json'

work_dir=""
stage_dir=""
cleanup() {
    [[ -z "$stage_dir" ]] || rm -rf -- "$stage_dir"
    [[ -z "$work_dir" ]] || rm -rf -- "$work_dir"
}
trap cleanup EXIT

usage() {
    sed -n '6,7p' "${BASH_SOURCE[0]}" | sed 's/^#   //' >&2
}

fail_usage() {
    log_error "$1" >&2
    usage
    exit 2
}

fail() {
    log_error "$1" >&2
    exit 1
}

is_absolute() {
    [[ "$1" == /* ]]
}

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum -- "$1" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 -- "$1" | awk '{print $1}'
    else
        fail "sha256sum or shasum is required"
    fi
}

detect_runtime() {
    local arch
    [[ "$(uname -s)" == "Linux" ]] ||
        fail "The packaged host-update CLI supports Linux and Windows hosts only; see docs/HOST_UPDATE_RUNBOOK.md"
    if ldd --version 2>&1 | grep -qi musl; then
        fail "musl/Alpine hosts are not supported by the packaged host-update CLI"
    fi
    arch="$(uname -m)"
    case "$arch" in
        x86_64|amd64) echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        *) fail "Unsupported host architecture for the packaged host-update CLI: $arch" ;;
    esac
}

# Copies (or downloads) one release asset into the private work directory, so every later check
# reads bytes nobody else can change.
fetch_asset() {
    local name="$1" asset_dir="$2" version="$3"
    if [[ -n "$asset_dir" ]]; then
        [[ -f "$asset_dir/$name" && ! -L "$asset_dir/$name" ]] || fail "Release asset not found: $asset_dir/$name"
        cp -- "$asset_dir/$name" "$work_dir/$name"
    else
        command -v curl >/dev/null 2>&1 || fail "curl is required to download release assets (or pass --asset-dir)"
        curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 \
            --output "$work_dir/$name" \
            "https://github.com/$RELEASE_REPOSITORY/releases/download/v$version/$name" ||
            fail "Could not download release asset: $name"
    fi
}

verify_members() {
    local archive="$1" listing
    listing="$work_dir/members.txt"
    tar -tzf "$archive" >"$listing" || fail "Host-update CLI archive is unreadable"
    [[ -s "$listing" ]] || fail "Host-update CLI archive is empty"
    if grep -Evq '^(\./)?([A-Za-z0-9._+-]+/)*[A-Za-z0-9._+-]*/?$' "$listing" ||
        grep -Eq '(^|/)\.\.(/|$)' "$listing"; then
        fail "Host-update CLI archive contains an unsafe member name"
    fi
    # Only regular files and directories: a link could redirect extraction or a later wrapper.
    if tar -tvzf "$archive" | grep -Evq '^[-d]' ||
        tar -tvzf "$archive" | grep -Eq ' -> | link to '; then
        fail "Host-update CLI archive contains a link or special file"
    fi
}

verify_manifest() {
    local manifest="$1" version="$2" runtime="$3" line
    [[ -f "$manifest" && ! -L "$manifest" ]] || fail "Host-update CLI package manifest is missing"
    for line in '  "package": "printfarmer-host-update-cli",' "  \"version\": \"$version\"," \
        "  \"runtime\": \"$runtime\"," '  "rolloutAuthorization": false'; do
        grep -Fxq -- "$line" "$manifest" ||
            fail "Host-update CLI package manifest does not match $version/$runtime"
    done
}

cmd_install() {
    local version="" asset_dir="" install_root="$DEFAULT_INSTALL_ROOT" runtime=""
    while [[ $# -gt 0 ]]; do
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --version) version="$2" ;;
            --asset-dir) asset_dir="$2" ;;
            --install-root) install_root="$2" ;;
            --runtime) runtime="$2" ;;
            *) fail_usage "Unknown install option: $1" ;;
        esac
        shift 2
    done
    [[ "$version" =~ $VERSION_RE ]] || fail_usage "--version must be X.Y.Z or X.Y.Z-insider.N"
    [[ -z "$asset_dir" ]] || is_absolute "$asset_dir" || fail_usage "--asset-dir must be an absolute path"
    is_absolute "$install_root" || fail_usage "--install-root must be an absolute path"
    if [[ -n "$runtime" ]]; then
        [[ "$runtime" == "linux-x64" || "$runtime" == "linux-arm64" ]] ||
            fail_usage "--runtime must be linux-x64 or linux-arm64 (use install-host-update-cli.ps1 on Windows)"
    else
        runtime="$(detect_runtime)"
    fi

    local branch="main"
    [[ "$version" != *-insider.* ]] || branch="development"
    command -v cosign >/dev/null 2>&1 || fail "cosign is required to verify the host-update CLI signature"
    command -v tar >/dev/null 2>&1 || fail "tar is required"

    local prefix="printfarmer-host-update-cli-v$version"
    local archive="$prefix-$runtime.tar.gz" sums="$prefix-SHA256SUMS"
    local bundle="$sums.sigstore.json"
    work_dir="$(mktemp -d "${TMPDIR:-/tmp}/printfarmer-host-update-cli.XXXXXX")"
    local name
    for name in "$archive" "$sums" "$bundle"; do
        fetch_asset "$name" "$asset_dir" "$version"
    done

    cosign verify-blob --bundle "$work_dir/$bundle" \
        --certificate-oidc-issuer "$OIDC_ISSUER" \
        --certificate-identity "https://github.com/$RELEASE_REPOSITORY/.github/workflows/consolidated-release.yml@refs/heads/$branch" \
        "$work_dir/$sums" >/dev/null ||
        fail "The host-update CLI checksum list is not signed by the $branch release workflow"

    [[ -s "$work_dir/$sums" ]] || fail "The host-update CLI checksum list is empty"
    if grep -Evq '^[0-9a-f]{64}  [A-Za-z0-9._+-]+$' "$work_dir/$sums"; then
        fail "The host-update CLI checksum list is malformed"
    fi
    local expected actual
    [[ "$(awk -v n="$archive" '$2 == n' "$work_dir/$sums" | wc -l | tr -d ' ')" == "1" ]] ||
        fail "The checksum list does not name $archive exactly once"
    expected="$(awk -v n="$archive" '$2 == n {print $1}' "$work_dir/$sums")"
    actual="$(sha256_of "$work_dir/$archive")"
    [[ "$actual" == "$expected" ]] || fail "SHA-256 mismatch for $archive"
    verify_members "$work_dir/$archive"

    umask 022
    if [[ -e "$install_root" || -L "$install_root" ]]; then
        [[ -d "$install_root" && ! -L "$install_root" ]] || fail "Install root is not a directory: $install_root"
    else
        mkdir -p -- "$install_root" || fail "Could not create install root: $install_root"
    fi
    if [[ -n "$(find "$install_root" -maxdepth 0 \( -perm -002 -o -perm -020 \) -print)" ]]; then
        fail "Install root is group- or world-writable: $install_root"
    fi

    # Staged on the install root's filesystem so placement is a rename.
    stage_dir="$(mktemp -d "$install_root/.staging.XXXXXX")"
    tar -xzf "$work_dir/$archive" -C "$stage_dir" --no-same-owner || fail "Could not extract $archive"
    if [[ "$(id -u)" == "0" ]]; then
        chown -R 0:0 -- "$stage_dir"
    fi
    chmod -R go-w -- "$stage_dir"
    chmod 0755 -- "$stage_dir"
    verify_manifest "$stage_dir/host-update-cli-package.json" "$version" "$runtime"

    local launcher="$stage_dir/cli/Farm.HostUpdate.Cli" help_text
    [[ -f "$launcher" && -x "$launcher" ]] || fail "The host-update CLI launcher is missing from $archive"
    help_text="$("$launcher" help 2>&1)" || fail "The host-update CLI does not run on this host ($runtime)"
    [[ "$help_text" == *"printfarmer-host-update status"* ]] ||
        fail "The host-update CLI does not run on this host ($runtime)"

    # Release versions are immutable, and replacing a directory is never atomic, so an existing
    # placement is only accepted when it is byte-identical to the verified archive.
    local target="$install_root/$version"
    if [[ -e "$target" || -L "$target" ]]; then
        [[ -d "$target" && ! -L "$target" ]] || fail "$target exists and is not a directory; nothing was changed"
        if ! diff -r --no-dereference -- "$stage_dir" "$target" >/dev/null 2>&1 ||
            [[ -n "$(find "$target" \( -perm -002 -o -perm -020 -o -type l \) -print -quit)" ]]; then
            fail "$target already exists and differs from the verified release; nothing was changed. Remove it (once no update or recovery needs it) and rerun"
        fi
        log_success "The verified host-update CLI $version ($runtime) is already installed at $target"
        echo "$target/printfarmer-host-update.sh"
        return 0
    fi
    mv -T -- "$stage_dir" "$target" || fail "Could not place the host-update CLI at $target"
    stage_dir=""
    log_success "Installed the verified host-update CLI $version ($runtime) at $target"
    echo "$target/printfarmer-host-update.sh"
}

# Emits nested JSON with string leaves from the selected .env keys, which the CLI's JSON
# provider reads exactly as it would the equivalent environment variables. Values are never
# printed in errors because they may hold credentials.
render_config() {
    LC_ALL=C awk '
        function failure(message) { print message > "/dev/stderr"; failed = 1; exit 1 }
        function quote(text,    i, c, out) {
            out = ""
            for (i = 1; i <= length(text); i++) {
                c = substr(text, i, 1)
                if (c == "\\" || c == "\"") out = out "\\"
                out = out c
            }
            return "\"" out "\""
        }
        function indent(level,    i, out) { out = ""; for (i = 0; i <= level; i++) out = out "  "; return out }
        function item(level, text) {
            body = body (count[level]++ ? ",\n" : "\n") indent(level) text
        }
        { sub(/\r$/, "") }
        /^[ \t]*#/ || index($0, "=") == 0 { next }
        {
            key = substr($0, 1, index($0, "=") - 1)
            value = substr($0, index($0, "=") + 1)
            if (key !~ /^(DB_PROVIDER|ConnectionStrings__Default|HostUpdateExecution__.+|HostUpdates__HostState__.+)$/) next
            if (index(value, "$") > 0) failure("Refusing " key ": values containing $ are ambiguous under compose interpolation")
            if (value ~ /[\001-\037\177]/) failure("Refusing " key ": value contains a control character")
            values[key] = value
        }
        END {
            if (failed) exit 1
            total = 0
            for (key in values) {
                segments = split(key, part, "__")
                path = ""
                for (i = 1; i <= segments; i++) {
                    if (part[i] !~ /^[A-Za-z0-9]+(_[A-Za-z0-9]+)*$/) failure("Refusing " key ": malformed configuration key")
                    path = (i == 1 ? part[i] : path "\001" part[i])
                    lower = tolower(path)
                    if (lower in spelling && spelling[lower] != path) failure("Refusing " key ": configuration key differs only in case from another key")
                    spelling[lower] = path
                    if (i < segments) parent[lower] = 1
                }
                leaf[tolower(path)] = 1
                sorted[++total] = path
                valueOf[path] = values[key]
            }
            if (failed) exit 1
            for (lower in leaf) if (lower in parent) failure("Refusing configuration: a key is both a value and a section")
            if (failed) exit 1
            if (!("hostupdateexecution\001rootdirectory" in leaf) || valueOf[spelling["hostupdateexecution\001rootdirectory"]] == "") exit 3
            for (i = 2; i <= total; i++) {
                current = sorted[i]
                for (j = i - 1; j >= 1 && sorted[j] > current; j--) sorted[j + 1] = sorted[j]
                sorted[j + 1] = current
            }
            depth = 0
            body = ""
            for (i = 1; i <= total; i++) {
                segments = split(sorted[i], part, "\001")
                common = 0
                while (common < depth && common < segments - 1 && open[common + 1] == part[common + 1]) common++
                while (depth > common) { body = body "\n" indent(depth - 1) "}"; delete count[depth]; depth-- }
                while (depth < segments - 1) {
                    item(depth, quote(part[depth + 1]) ": {")
                    depth++
                    open[depth] = part[depth]
                }
                item(depth, quote(part[segments]) ": " quote(valueOf[sorted[i]]))
            }
            while (depth > 0) { body = body "\n" indent(depth - 1) "}"; depth-- }
            printf "{%s\n}\n", body
        }
    ' "$1"
}

owner_of() {
    stat -c %U -- "$1" 2>/dev/null || stat -f %Su -- "$1"
}

cmd_write_config() {
    local env_file="" output="$DEFAULT_CONFIG" owner=""
    while [[ $# -gt 0 ]]; do
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --env-file) env_file="$2" ;;
            --output) output="$2" ;;
            --owner) owner="$2" ;;
            *) fail_usage "Unknown write-config option: $1" ;;
        esac
        shift 2
    done
    is_absolute "$env_file" || fail_usage "--env-file must be an absolute path"
    is_absolute "$output" || fail_usage "--output must be an absolute path"
    [[ -f "$env_file" ]] || fail "Environment file not found: $env_file"
    [[ ! -L "$output" && ! -d "$output" ]] || fail "Refusing to replace a link or directory: $output"

    local rendered status=0
    rendered="$(render_config "$env_file")" || status=$?
    if [[ $status -eq 3 ]]; then
        log_warn "HostUpdateExecution__RootDirectory is not set in $env_file; host-update.json not written" >&2
        exit 3
    fi
    [[ $status -eq 0 ]] || fail "host-update.json not written"

    if [[ -z "$owner" ]]; then
        local root
        root="$(LC_ALL=C awk '{ sub(/\r$/, "") } index($0, "=") > 0 && substr($0, 1, index($0, "=") - 1) == "HostUpdateExecution__RootDirectory" { value = substr($0, index($0, "=") + 1) } END { print value }' "$env_file")"
        if [[ "$root" == /* && -d "$root" && ! -L "$root" ]]; then
            owner="$(owner_of "$root")" || fail "Could not read the owner of $root"
        else
            [[ ! -e "$root" && ! -L "$root" ]] ||
                log_warn "HostUpdateExecution__RootDirectory is not an absolute, non-link directory; host-update.json is owned by the current user" >&2
            owner="$(id -un)"
        fi
    fi
    id -u "$owner" >/dev/null 2>&1 || fail "Unknown configuration owner: $owner"

    local directory temporary
    directory="$(dirname -- "$output")"
    umask 022
    mkdir -p -- "$directory" || fail "Could not create $directory"
    umask 077
    temporary="$(mktemp "$directory/.host-update.json.XXXXXX")" || fail "Could not create a file in $directory"
    work_dir="$temporary"
    printf '%s\n' "$rendered" >"$temporary"
    chmod 0600 -- "$temporary"
    if [[ "$owner" != "$(id -un)" ]]; then
        chown -- "$owner" "$temporary" || fail "Could not give $output to $owner"
    fi
    mv -f -- "$temporary" "$output" || fail "Could not write $output"
    work_dir=""
    log_success "Wrote owner-only host-update configuration $output (owner $owner)"
}

[[ $# -ge 1 ]] || fail_usage "A command is required"
command_name="$1"
shift
case "$command_name" in
    install) cmd_install "$@" ;;
    write-config) cmd_write_config "$@" ;;
    help|--help|-h) usage; exit 0 ;;
    *) fail_usage "Unknown command: $command_name" ;;
esac
