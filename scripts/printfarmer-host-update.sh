#!/usr/bin/env bash
# Host-local PrintFarmer host-update status/recovery wrapper (issue #2980, first slice).
#
# Runs the packaged Farm.HostUpdate.Cli without the API. Only three fixed operations are exposed;
# no arbitrary shell text, compose files, or credentials are accepted on the command line. This
# is NOT rollout authorization: it never starts a forward update.
#
#   printfarmer-host-update.sh --config /abs/host-update.json status [--release <id>] [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --preview [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --confirm <id> [--reapprove-drift <token>] [--printers-reconciled <token>] [--json]
#   printfarmer-host-update.sh import --bundle /abs/bundle.tar --channel <stable|insider> --version <v> --trusted-root /abs/trusted_root.json --staging /abs/new-dir --records /abs/records-dir --operator <id> [--prior-recovery-set /abs/dir] [--protected-backup /abs/reference.json]
#
# `import` (issue #3063) verifies a signed offline update bundle without network access, loads only
# its verified images into the local Docker engine and writes one durable, redacted decision record
# under --records. It needs no --config and never installs, activates or authorizes a rollout.
#
# --reapprove-drift takes the token printed by `recover --preview` when the host drifted since the
# recorded authorization (CLI exit 12). --printers-reconciled takes the physical-<32 hex> token
# printed by `recover --preview` once every listed printer is physically reconciled (CLI exit 13).
#
# Environment:
#   PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute directory containing the CLI (default: cli/ beside an
#                                    installed package's wrapper). A self-contained package launcher
#                                    (Farm.HostUpdate.Cli) runs directly; otherwise
#                                    Farm.HostUpdate.Cli.dll runs on the dotnet host.
#   PRINTFARMER_DOTNET               absolute path to the dotnet host (optional; default: dotnet on PATH;
#                                    refused for a self-contained package)
#   PRINTFARMER_NODE                 import only: absolute path to node (optional; default: node on PATH)
#   PRINTFARMER_OFFLINE_BUNDLE_TOOL  import only: absolute path to offline-update-bundle.mjs (default:
#                                    ci/offline-update-bundle.mjs beside this wrapper in a repository checkout)
#   PRINTFARMER_COSIGN               import only: absolute path to cosign (optional; default: cosign on PATH)
#   PRINTFARMER_DOCKER               import only: absolute path to docker (optional; default: docker on PATH)
#
# Exit codes are the CLI's (see docs/HOST_UPDATE_RUNBOOK.md); the wrapper itself only ever
# returns 2 for a usage or setup error, before the CLI runs.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

readonly RELEASE_RE='^(stable|insider):[0-9A-Za-z.+-]{1,128}$'
readonly REQUEST_RE='^[A-Za-z0-9._:-]{1,128}$'
readonly DRIFT_TOKEN_RE='^drift-[0-9a-f]{32}$'
readonly PHYSICAL_TOKEN_RE='^physical-[0-9a-f]{32}$'
readonly CHANNEL_RE='^(stable|insider)$'
readonly VERSION_RE='^[0-9A-Za-z.+-]{1,128}$'
readonly OPERATOR_RE='^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$'

usage() {
    sed -n '8,11p' "${BASH_SOURCE[0]}" | sed 's/^#   //' >&2
}

fail_usage() {
    log_error "$1" >&2
    usage
    exit 2
}

is_absolute() {
    [[ "$1" == /* ]]
}

optional_executable() {
    # $1 = environment variable name, $2 = default command name
    local value="${!1:-}"
    if [[ -z "$value" ]]; then
        printf '%s' "$2"
        return
    fi
    is_absolute "$value" && [[ -f "$value" && -x "$value" ]] || fail_usage "$1 must be an absolute executable path"
    printf '%s' "$value"
}

run_import() {
    local seen=" "
    local bundle="" channel="" version="" trusted_root="" staging="" records="" operator=""
    local prior="" backup=""
    while [[ $# -gt 0 ]]; do
        local option="$1"
        case "$option" in
            --bundle|--channel|--version|--trusted-root|--staging|--records|--operator|--prior-recovery-set|--protected-backup) ;;
            *) fail_usage "unsupported argument: $option" ;;
        esac
        [[ "$seen" != *" $option "* ]] || fail_usage "$option may be given only once"
        [[ $# -ge 2 && -n "$2" ]] || fail_usage "$option requires a value"
        seen+="$option "
        local value="$2"
        shift 2
        case "$option" in
            --channel)
                [[ "$value" =~ $CHANNEL_RE ]] || fail_usage "--channel must be stable or insider"
                channel="$value" ;;
            --version)
                [[ "$value" =~ $VERSION_RE ]] || fail_usage "--version requires [0-9A-Za-z.+-]{1,128}"
                version="$value" ;;
            --operator)
                [[ "$value" =~ $OPERATOR_RE ]] || fail_usage "--operator requires [A-Za-z0-9][A-Za-z0-9._@-]{0,63}"
                operator="$value" ;;
            *)
                is_absolute "$value" || fail_usage "$option must be an absolute path"
                case "$option" in
                    --bundle) bundle="$value" ;;
                    --trusted-root) trusted_root="$value" ;;
                    --staging) staging="$value" ;;
                    --records) records="$value" ;;
                    --prior-recovery-set) prior="$value" ;;
                    --protected-backup) backup="$value" ;;
                esac ;;
        esac
    done
    local option
    for option in --bundle --channel --version --trusted-root --staging --records --operator; do
        [[ "$seen" == *" $option "* ]] || fail_usage "import requires $option"
    done

    local node_host tool cosign_host docker_host
    node_host="$(optional_executable PRINTFARMER_NODE node)"
    cosign_host="$(optional_executable PRINTFARMER_COSIGN cosign)"
    docker_host="$(optional_executable PRINTFARMER_DOCKER docker)"
    tool="${PRINTFARMER_OFFLINE_BUNDLE_TOOL:-$SCRIPT_DIR/ci/offline-update-bundle.mjs}"
    is_absolute "$tool" && [[ -f "$tool" ]] || fail_usage "PRINTFARMER_OFFLINE_BUNDLE_TOOL must be an absolute path to offline-update-bundle.mjs"

    local -a tool_args=(import --bundle "$bundle" --channel "$channel" --version "$version"
        --trusted-root "$trusted_root" --staging "$staging" --records "$records" --operator "$operator")
    if [[ -n "$prior" ]]; then tool_args+=(--prior-recovery-set "$prior"); fi
    if [[ -n "$backup" ]]; then tool_args+=(--protected-backup "$backup"); fi
    if [[ -n "${PRINTFARMER_COSIGN:-}" ]]; then tool_args+=(--cosign "$cosign_host"); fi
    if [[ -n "${PRINTFARMER_DOCKER:-}" ]]; then tool_args+=(--docker "$docker_host"); fi

    exec "$node_host" "$tool" "${tool_args[@]}"
}

config=""
case "${1:-}" in
    help|--help|-h) usage; exit 0 ;;
    import) shift; run_import "$@" ;;
esac
if [[ "${1:-}" == "--config" ]]; then
    [[ $# -ge 2 ]] || fail_usage "--config requires a value"
    config="$2"
    shift 2
fi
[[ -n "$config" ]] || fail_usage "--config <absolute-json-path> must be the first argument"
is_absolute "$config" || fail_usage "--config must be an absolute path"
# Existence/readability is proven by the CLI (exit 3): a test here cannot tell denied from absent.

cli_dir="${PRINTFARMER_HOST_UPDATE_CLI_DIR:-}"
# An installed package (issue #3041) carries its self-contained CLI in cli/ beside this wrapper.
if [[ -z "$cli_dir" && -f "$SCRIPT_DIR/host-update-cli-package.json" ]]; then
    cli_dir="$SCRIPT_DIR/cli"
fi
[[ -n "$cli_dir" ]] && is_absolute "$cli_dir" || fail_usage "PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory"
cli_apphost="$cli_dir/Farm.HostUpdate.Cli"
if [[ -f "$cli_apphost" ]]; then
    # Self-contained package: the launcher carries its own runtime, so no dotnet host is used.
    [[ -x "$cli_apphost" ]] || fail_usage "Farm.HostUpdate.Cli in PRINTFARMER_HOST_UPDATE_CLI_DIR is not executable"
    [[ -z "${PRINTFARMER_DOTNET:-}" ]] || fail_usage "PRINTFARMER_DOTNET must not be set for a self-contained CLI package"
    launcher=("$cli_apphost")
else
    cli_dll="$cli_dir/Farm.HostUpdate.Cli.dll"
    [[ -f "$cli_dll" ]] || fail_usage "Farm.HostUpdate.Cli.dll not found in PRINTFARMER_HOST_UPDATE_CLI_DIR"

    dotnet_host="${PRINTFARMER_DOTNET:-dotnet}"
    if [[ -n "${PRINTFARMER_DOTNET:-}" ]]; then
        is_absolute "$dotnet_host" && [[ -x "$dotnet_host" ]] || fail_usage "PRINTFARMER_DOTNET must be an absolute executable path"
    fi
    launcher=("$dotnet_host" "$cli_dll")
fi

[[ $# -ge 1 ]] || fail_usage "missing command"
command="$1"
shift
case "$command" in
    status|recover) ;;
    help|--help|-h) usage; exit 0 ;;
    *) fail_usage "unknown command: $command" ;;
esac

# Pre-validate identifiers so nothing unexpected reaches the CLI. The CLI re-validates everything,
# including the canonical release grammar and the option combination rules.
args=("$command")
while [[ $# -gt 0 ]]; do
    case "$1" in
        --json|--preview)
            args+=("$1")
            shift
            ;;
        --release|--confirm)
            [[ $# -ge 2 && "$2" =~ $RELEASE_RE ]] || fail_usage "$1 requires a release id like stable:1.2.3"
            args+=("$1" "$2")
            shift 2
            ;;
        --request-id)
            [[ $# -ge 2 && "$2" =~ $REQUEST_RE ]] || fail_usage "--request-id requires [A-Za-z0-9._:-]{1,128}"
            args+=("$1" "$2")
            shift 2
            ;;
        --reapprove-drift)
            [[ -z "${seen_drift:-}" ]] || fail_usage "--reapprove-drift may be given only once"
            [[ $# -ge 2 && "$2" =~ $DRIFT_TOKEN_RE ]] || fail_usage "--reapprove-drift requires the drift-<32 hex> token printed by --preview"
            seen_drift=1
            args+=("$1" "$2")
            shift 2
            ;;
        --printers-reconciled)
            [[ -z "${seen_physical:-}" ]] || fail_usage "--printers-reconciled may be given only once"
            [[ $# -ge 2 && "$2" =~ $PHYSICAL_TOKEN_RE ]] || fail_usage "--printers-reconciled requires the physical-<32 hex> token printed by --preview"
            seen_physical=1
            args+=("$1" "$2")
            shift 2
            ;;
        *)
            fail_usage "unsupported argument: $1"
            ;;
    esac
done

exec "${launcher[@]}" --config "$config" "${args[@]}"
