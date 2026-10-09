#!/usr/bin/env bash
# Installs the signed, self-contained host-update recovery CLI and generates its host
# configuration (issue #3045). It never builds the CLI and never falls back to an unverified
# source. It is NOT rollout authorization: it neither enables nor starts an update.
#
#   install-host-update-cli.sh install --version <X.Y.Z[-insider.N]> [--asset-dir <abs-dir>] [--install-root <abs-dir>] [--runtime <linux-x64|linux-arm64>] [--trusted-root <abs-file>]
#   install-host-update-cli.sh write-config --env-file <abs-file> [--output <abs-file>] [--owner <user>]
#   install-host-update-cli.sh install-service --cli-dir <abs-dir> [--config <abs-file>] [--service-user <user>] [--unit-dir <abs-dir>] [--enable]
#   install-host-update-cli.sh uninstall-service [--unit-dir <abs-dir>]
#   install-host-update-cli.sh prepare-state --env-file <abs-file>
#
# install       Downloads (or reads from --asset-dir) the runtime's archive, the checksum list and
#               its Cosign bundle; verifies the bundle against the release workflow identity for
#               the version's channel, the archive SHA-256, its members and package manifest;
#               proves the CLI launches; then places it at <install-root>/<version> (default
#               /opt/printfarmer/host-update-cli). Versions are immutable: an existing placement
#               identical to the verified archive is accepted, a differing one is refused and left
#               untouched. The install root must not be group- or world-writable. Requires cosign.
#               --trusted-root verifies offline against an operator-supplied Sigstore trusted
#               root (an absolute path to a regular, readable file). It is only ever taken
#               from this option, never from the environment or configuration, and is never
#               defaulted; without it cosign verifies against the public-good root.
# write-config  Writes an owner-only (0600) host-update.json (default /etc/printfarmer/host-update.json)
#               from the deployment .env's HostUpdateExecution__*, HostUpdates__HostState__*,
#               DB_PROVIDER and ConnectionStrings__Default. The owner defaults to the owner of
#               HostUpdateExecution__RootDirectory when it is an absolute, non-link directory,
#               otherwise the current user.
# install-service  Opt-in only (issue #3118): registers the enrolled host-update daemon as the
#               systemd unit printfarmer-host-update-daemon.service (default unit directory
#               /etc/systemd/system) running <cli-dir>/cli/Farm.HostUpdate.Cli --config <config>
#               daemon. The unit is installed disabled and stopped unless --enable is given, and a
#               rerun never changes whether it is enabled. It runs as --service-user, which
#               defaults to the owner of the owner-only config (default
#               /etc/printfarmer/host-update.json); root is refused unless named explicitly. The
#               unit carries no environment, credential or auto-update setting, and installing it
#               grants nothing: daemon execution stays disabled pending #2982.
# uninstall-service  Stops and disables the unit and removes it. It never deletes the config,
#               journal, identity storage or logs. Removing a unit that is not installed is a no-op.
# prepare-state Issue #3207: creates <HostUpdateExecution__RootDirectory>/state (mode 0755, owned by
#               the root directory's owner) before Compose bind-mounts it read-only into the
#               application containers, so Docker never creates it as root and the executor
#               account can still write admission.closed. The root must already be an absolute,
#               non-link directory; an existing state directory must be a non-link directory and
#               is left unchanged.
#
# Exit codes: 0 done; 1 verification, validation or installation failed (nothing placed or
# written); 2 usage; 3 write-config and prepare-state only: HostUpdateExecution__RootDirectory is
# not configured, so nothing was written.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

readonly VERSION_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-insider\.(0|[1-9][0-9]*))?$'
readonly RELEASE_REPOSITORY='OlyForge3D/PrintFarmer'
readonly OIDC_ISSUER='https://token.actions.githubusercontent.com'
readonly DEFAULT_INSTALL_ROOT='/opt/printfarmer/host-update-cli'
readonly DEFAULT_CONFIG='/etc/printfarmer/host-update.json'
readonly DEFAULT_UNIT_DIR='/etc/systemd/system'
readonly DAEMON_UNIT='printfarmer-host-update-daemon.service'
readonly DAEMON_UNIT_MARKER='# Managed by install-host-update-cli.sh install-service (issue #3118).'
# Paths and account names are written into the unit verbatim, so only characters that systemd
# neither splits, quotes nor expands (%, $, \, whitespace) are accepted.
readonly UNIT_PATH_RE='^/[A-Za-z0-9._/+-]+$'
readonly UNIT_USER_RE='^[A-Za-z_][A-Za-z0-9_.-]{0,31}$'

work_dir=""
stage_dir=""
cleanup() {
    [[ -z "$stage_dir" ]] || rm -rf -- "$stage_dir"
    [[ -z "$work_dir" ]] || rm -rf -- "$work_dir"
}
trap cleanup EXIT

usage() {
    sed -n '6,10p' "${BASH_SOURCE[0]}" | sed 's/^#   //' >&2
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

# True when an existing placement matches the verified staging tree in bytes, entry types,
# modes and ownership, and its own launcher runs.
placement_matches() {
    local expected="$1" actual="$2" help_text
    diff -r --no-dereference -- "$expected" "$actual" >/dev/null 2>&1 || return 1
    [[ "$(cd -- "$expected" && find . -printf '%y %m %u:%g %p\n' | LC_ALL=C sort)" == \
        "$(cd -- "$actual" && find . -printf '%y %m %u:%g %p\n' | LC_ALL=C sort)" ]] || return 1
    help_text="$("$actual/cli/Farm.HostUpdate.Cli" help 2>&1)" || return 1
    [[ "$help_text" == *"printfarmer-host-update status"* ]]
}

cmd_install() {
    local version="" asset_dir="" install_root="$DEFAULT_INSTALL_ROOT" runtime="" trusted_root="" trusted_root_set=0
    while [[ $# -gt 0 ]]; do
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --version) version="$2" ;;
            --asset-dir) asset_dir="$2" ;;
            --install-root) install_root="$2" ;;
            --runtime) runtime="$2" ;;
            --trusted-root)
                [[ "$trusted_root_set" == 0 ]] || fail_usage "--trusted-root may be given only once"
                trusted_root="$2"
                trusted_root_set=1
                ;;
            *) fail_usage "Unknown install option: $1" ;;
        esac
        shift 2
    done
    [[ "$version" =~ $VERSION_RE ]] || fail_usage "--version must be X.Y.Z or X.Y.Z-insider.N"
    [[ -z "$asset_dir" ]] || is_absolute "$asset_dir" || fail_usage "--asset-dir must be an absolute path"
    local -a offline_trust=()
    if [[ "$trusted_root_set" == 1 ]]; then
        is_absolute "$trusted_root" || fail_usage "--trusted-root must be an absolute path"
        [[ -f "$trusted_root" && ! -L "$trusted_root" ]] ||
            fail "Sigstore trusted root is not a regular file: $trusted_root"
        [[ -r "$trusted_root" && -s "$trusted_root" ]] ||
            fail "Sigstore trusted root is unreadable or empty: $trusted_root"
        offline_trust=(--trusted-root "$trusted_root")
    fi
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

    cosign verify-blob ${offline_trust[@]+"${offline_trust[@]}"} --bundle "$work_dir/$bundle" \
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
    local root_uid
    root_uid="$(stat -c %u -- "$install_root")" || fail "Could not read the owner of $install_root"
    [[ "$root_uid" == "0" || "$root_uid" == "$(id -u)" ]] ||
        fail "Install root is owned by another account (uid $root_uid): $install_root"

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
    # placement is only accepted when its bytes, types, modes and ownership match the verified
    # archive and its own launcher runs.
    local target="$install_root/$version"
    if [[ -e "$target" || -L "$target" ]]; then
        [[ -d "$target" && ! -L "$target" ]] || fail "$target exists and is not a directory; nothing was changed"
        if ! placement_matches "$stage_dir" "$target"; then
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

env_root_directory() {
    LC_ALL=C awk '{ sub(/\r$/, "") } index($0, "=") > 0 && substr($0, 1, index($0, "=") - 1) == "HostUpdateExecution__RootDirectory" { value = substr($0, index($0, "=") + 1) } END { print value }' "$1"
}

cmd_prepare_state() {
    local env_file=""
    while [[ $# -gt 0 ]]; do
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --env-file) env_file="$2" ;;
            *) fail_usage "Unknown prepare-state option: $1" ;;
        esac
        shift 2
    done
    [[ -n "$env_file" ]] || fail_usage "--env-file is required"
    is_absolute "$env_file" || fail_usage "--env-file must be an absolute path"
    [[ -f "$env_file" ]] || fail "Environment file not found: $env_file"

    local root state owner
    root="$(env_root_directory "$env_file")"
    if [[ -z "$root" ]]; then
        log_info "HostUpdateExecution__RootDirectory is not set in $env_file; no admission state directory to prepare" >&2
        exit 3
    fi
    [[ "$root" == /* ]] || fail "HostUpdateExecution__RootDirectory must be an absolute path: $root"
    root="${root%/}"
    [[ -n "$root" ]] || fail "HostUpdateExecution__RootDirectory must not be the filesystem root"
    [[ -d "$root" && ! -L "$root" ]] ||
        fail "HostUpdateExecution__RootDirectory must be an existing, non-link directory owned by the host-update account before deploying, or Docker creates its state directory as root: $root"
    state="$root/state"
    if [[ -e "$state" || -L "$state" ]]; then
        [[ -d "$state" && ! -L "$state" ]] || fail "The admission state path is not a non-link directory: $state"
        log_info "Admission state directory already exists: $state" >&2
        return 0
    fi
    owner="$(owner_of "$root")" || fail "Could not read the owner of $root"
    umask 022
    mkdir -m 0755 -- "$state" || fail "Could not create $state"
    if [[ "$owner" != "$(id -un)" ]]; then
        chown -- "$owner" "$state" || { rmdir -- "$state"; fail "Could not give $state to $owner"; }
    fi
    log_success "Created admission state directory $state (owner $owner)" >&2
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
        root="$(env_root_directory "$env_file")"
        if [[ "$root" == /* && -d "$root" && ! -L "$root" ]]; then
            owner="$(owner_of "$root")" || fail "Could not read the owner of $root"
        else
            log_warn "HostUpdateExecution__RootDirectory is not an existing absolute, non-link directory; host-update.json is owned by the current user" >&2
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

render_daemon_unit() {
    local launcher="$1" config="$2" user="$3" group="$4"
    cat <<UNIT
$DAEMON_UNIT_MARKER
# Rerun install-service to change it and uninstall-service to remove it; do not edit it by hand.
# Installing this unit grants nothing: daemon execution stays disabled pending #2982, and the
# unit carries no environment, credential or automatic-update setting.
[Unit]
Description=PrintFarmer host-update daemon (execution disabled pending #2982)
Documentation=https://github.com/OlyForge3D/PrintFarmer/blob/main/docs/HOST_UPDATE_RUNBOOK.md
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=600
StartLimitBurst=5

[Service]
Type=exec
User=$user
Group=$group
# The CLI refuses a RootDirectory under its working directory, and systemd's default is /.
WorkingDirectory=${launcher%/*}
ExecStart=$launcher --config $config daemon
Restart=on-failure
RestartSec=30
RestartPreventExitStatus=2 3 7
TimeoutStopSec=30
UMask=0077
NoNewPrivileges=yes
CapabilityBoundingSet=
AmbientCapabilities=
PrivateTmp=yes
PrivateDevices=yes
ProtectSystem=full
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
RestrictSUIDSGID=yes
RestrictRealtime=yes
LockPersonality=yes
StandardOutput=journal
StandardError=journal
SyslogIdentifier=printfarmer-host-update-daemon

[Install]
WantedBy=multi-user.target
UNIT
}

systemctl_or_fail() {
    systemctl "$@" || fail "systemctl $* failed"
}

# Succeeds when the service account can read and execute the CLI launcher and its directory,
# checked as that account because group membership and ACLs decide it, not the mode bits alone.
service_can_run_cli() {
    local user="$1" launcher="$2" cli="$3"
    local probe='test -r "$1" && test -x "$1" && test -r "$2" && test -x "$2"'
    if [[ "$(id -u -- "$user")" == "$(id -u)" ]]; then
        sh -c "$probe" sh "$launcher" "$cli"
    elif [[ "$(id -u)" == "0" ]] && command -v runuser >/dev/null 2>&1; then
        runuser -u "$user" -- sh -c "$probe" sh "$launcher" "$cli"
    elif command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
        sudo -n -u "$user" -- sh -c "$probe" sh "$launcher" "$cli"
    else
        fail "Could not check that $user can run $launcher; run install-service as root"
    fi
}

parse_unit_dir() {
    local unit_dir="$1"
    is_absolute "$unit_dir" || fail_usage "--unit-dir must be an absolute path"
    [[ -d "$unit_dir" && ! -L "$unit_dir" ]] || fail "Unit directory is not a directory: $unit_dir"
}

cmd_install_service() {
    local cli_dir="" config="$DEFAULT_CONFIG" user="" unit_dir="$DEFAULT_UNIT_DIR" enable=0
    while [[ $# -gt 0 ]]; do
        if [[ "$1" == "--enable" ]]; then
            [[ "$enable" == 0 ]] || fail_usage "--enable may be given only once"
            enable=1
            shift
            continue
        fi
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --cli-dir) cli_dir="$2" ;;
            --config) config="$2" ;;
            --service-user) user="$2" ;;
            --unit-dir) unit_dir="$2" ;;
            *) fail_usage "Unknown install-service option: $1" ;;
        esac
        shift 2
    done
    [[ -n "$cli_dir" ]] || fail_usage "--cli-dir is required"
    is_absolute "$cli_dir" || fail_usage "--cli-dir must be an absolute path"
    is_absolute "$config" || fail_usage "--config must be an absolute path"
    [[ -z "$user" || "$user" =~ $UNIT_USER_RE ]] || fail_usage "--service-user is not a valid account name"
    parse_unit_dir "$unit_dir"
    [[ "$(uname -s)" == "Linux" ]] || fail "install-service supports systemd Linux hosts only (use install-host-update-cli.ps1 on Windows)"
    command -v systemctl >/dev/null 2>&1 || fail "systemd (systemctl) is required to install the host-update daemon service"

    cli_dir="${cli_dir%/}"
    [[ "$cli_dir" =~ $UNIT_PATH_RE && "$config" =~ $UNIT_PATH_RE ]] ||
        fail "--cli-dir and --config may contain only letters, digits and . _ / + -"
    local launcher="$cli_dir/cli/Farm.HostUpdate.Cli" manifest="$cli_dir/host-update-cli-package.json" path
    for path in "$cli_dir" "$cli_dir/cli"; do
        [[ -d "$path" && ! -L "$path" ]] || fail "Not an installed host-update CLI directory: $cli_dir"
    done
    [[ -f "$launcher" && ! -L "$launcher" && -x "$launcher" ]] || fail "The host-update CLI launcher is missing: $launcher"
    if [[ ! -f "$manifest" || -L "$manifest" ]] ||
        ! grep -Fxq '  "package": "printfarmer-host-update-cli",' "$manifest" ||
        ! grep -Fxq '  "rolloutAuthorization": false' "$manifest"; then
        fail "Not an installed host-update CLI package (install it with the install command first): $cli_dir"
    fi
    # The service runs this binary, so nobody but root or the installing account may change it.
    for path in "$cli_dir" "$cli_dir/cli" "$launcher"; do
        if [[ -n "$(find "$path" -maxdepth 0 \( -perm -002 -o -perm -020 \) -print)" ]]; then
            fail "The host-update CLI is group- or world-writable: $path"
        fi
        local path_uid
        path_uid="$(stat -c %u -- "$path")" || fail "Could not read the owner of $path"
        [[ "$path_uid" == "0" || "$path_uid" == "$(id -u)" ]] ||
            fail "The host-update CLI is owned by another account (uid $path_uid): $path"
    done

    [[ -f "$config" && ! -L "$config" ]] ||
        fail "Host-update configuration not found (run write-config first): $config"
    [[ -z "$(find "$config" -maxdepth 0 -perm /077 -print)" ]] ||
        fail "Host-update configuration is readable or writable by group or others: $config"
    local config_owner
    config_owner="$(stat -c %U -- "$config")" || fail "Could not read the owner of $config"
    if [[ -z "$user" ]]; then
        user="$config_owner"
        [[ "$(id -u -- "$user" 2>/dev/null)" != "0" ]] ||
            fail "$config is owned by root; the daemon would run as root. Give HostUpdateExecution__RootDirectory and the config to a dedicated account, or pass --service-user root to accept running as root"
    fi
    [[ "$user" =~ $UNIT_USER_RE ]] || fail "The configuration owner is not a valid account name: $user"
    id -u -- "$user" >/dev/null 2>&1 || fail "Unknown service account: $user"
    [[ "$user" == "$config_owner" ]] ||
        fail "$config must be owned by the service account $user (it is owned by $config_owner)"
    local group
    group="$(id -gn -- "$user")" || fail "Could not read the primary group of $user"
    [[ "$group" =~ $UNIT_USER_RE ]] || fail "The service account's primary group is not a valid name: $group"
    service_can_run_cli "$user" "$launcher" "$cli_dir/cli" ||
        fail "The service account $user cannot read and execute the host-update CLI: $launcher"

    local unit="$unit_dir/$DAEMON_UNIT" rendered changed=1
    rendered="$(render_daemon_unit "$launcher" "$config" "$user" "$group")"
    if [[ -e "$unit" || -L "$unit" ]]; then
        [[ -f "$unit" && ! -L "$unit" ]] || fail "$unit exists and is not a regular file; nothing was changed"
        [[ "$(head -n 1 -- "$unit")" == "$DAEMON_UNIT_MARKER" ]] ||
            fail "$unit was not installed by install-service; nothing was changed"
        [[ "$(cat -- "$unit")" != "$rendered" ]] || changed=0
    fi
    if [[ "$changed" == 1 ]]; then
        local temporary backup=""
        umask 022
        if [[ -f "$unit" ]]; then
            backup="$(mktemp "$unit_dir/.$DAEMON_UNIT.previous.XXXXXX")" || fail "Could not create a file in $unit_dir"
            cp -p -- "$unit" "$backup" || { rm -f -- "$backup"; fail "Could not back up $unit"; }
        fi
        temporary="$(mktemp "$unit_dir/.$DAEMON_UNIT.XXXXXX")" || { [[ -z "$backup" ]] || rm -f -- "$backup"; fail "Could not create a file in $unit_dir"; }
        work_dir="$temporary"
        printf '%s\n' "$rendered" >"$temporary"
        chmod 0644 -- "$temporary"
        mv -f -- "$temporary" "$unit" || { [[ -z "$backup" ]] || rm -f -- "$backup"; fail "Could not write $unit"; }
        work_dir=""
        # A running daemon picks up the new unit; a stopped one stays stopped. If systemd rejects
        # the new unit, the previous one (or none) is put back so the host is left as it was.
        if ! systemctl daemon-reload || ! systemctl try-restart "$DAEMON_UNIT"; then
            if [[ -n "$backup" ]]; then mv -f -- "$backup" "$unit"; else rm -f -- "$unit"; fi
            systemctl daemon-reload || true
            [[ -z "$backup" ]] || systemctl try-restart "$DAEMON_UNIT" || true
            fail "systemctl could not load or restart $DAEMON_UNIT; the previous unit state was restored"
        fi
        [[ -z "$backup" ]] || rm -f -- "$backup"
        log_success "Installed $unit (runs as $user)"
    else
        log_success "$unit is already installed (runs as $user)"
    fi

    if [[ "$enable" == 1 ]]; then
        systemctl_or_fail enable --now "$DAEMON_UNIT"
        log_success "Enabled and started $DAEMON_UNIT (daemon execution remains disabled pending #2982)"
    else
        log_info "$DAEMON_UNIT was not enabled or started. Start it with: systemctl enable --now $DAEMON_UNIT"
    fi
}

cmd_uninstall_service() {
    local unit_dir="$DEFAULT_UNIT_DIR"
    while [[ $# -gt 0 ]]; do
        [[ $# -ge 2 ]] || fail_usage "$1 requires a value"
        case "$1" in
            --unit-dir) unit_dir="$2" ;;
            *) fail_usage "Unknown uninstall-service option: $1" ;;
        esac
        shift 2
    done
    parse_unit_dir "$unit_dir"
    command -v systemctl >/dev/null 2>&1 || fail "systemd (systemctl) is required to uninstall the host-update daemon service"

    local unit="$unit_dir/$DAEMON_UNIT"
    if [[ ! -e "$unit" && ! -L "$unit" ]]; then
        log_success "$DAEMON_UNIT is not installed; nothing to remove"
        return 0
    fi
    [[ -f "$unit" && ! -L "$unit" ]] || fail "$unit exists and is not a regular file; nothing was changed"
    [[ "$(head -n 1 -- "$unit")" == "$DAEMON_UNIT_MARKER" ]] ||
        fail "$unit was not installed by install-service; nothing was changed"
    systemctl_or_fail disable --now "$DAEMON_UNIT"
    rm -f -- "$unit" || fail "Could not remove $unit"
    systemctl_or_fail daemon-reload
    systemctl reset-failed "$DAEMON_UNIT" >/dev/null 2>&1 || true
    log_success "Removed $DAEMON_UNIT; the configuration, journal, identity storage and logs were kept"
}

[[ $# -ge 1 ]] || fail_usage "A command is required"
command_name="$1"
shift
case "$command_name" in
    install) cmd_install "$@" ;;
    write-config) cmd_write_config "$@" ;;
    prepare-state) cmd_prepare_state "$@" ;;
    install-service) cmd_install_service "$@" ;;
    uninstall-service) cmd_uninstall_service "$@" ;;
    help|--help|-h) usage; exit 0 ;;
    *) fail_usage "Unknown command: $command_name" ;;
esac
