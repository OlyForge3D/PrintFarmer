#!/usr/bin/env bash
# Host-local PrintFarmer host-update status/recovery wrapper (issue #2980, first slice).
#
# Runs the packaged Farm.HostUpdate.Cli without the API. Only three fixed operations are exposed;
# no arbitrary shell text, compose files, or credentials are accepted on the command line. This
# is NOT rollout authorization: it never starts a forward update.
#
#   printfarmer-host-update.sh --config /abs/host-update.json status [--release <id>] [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --preview [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --confirm <id> [--reapprove-drift <token>] [--json]
#
# --reapprove-drift takes the token printed by `recover --preview` when the host drifted since the
# recorded authorization (CLI exit 12).
#
# Environment:
#   PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute directory containing Farm.HostUpdate.Cli.dll (required)
#   PRINTFARMER_DOTNET               absolute path to the dotnet host (optional; default: dotnet on PATH)
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

usage() {
    sed -n '8,10p' "${BASH_SOURCE[0]}" | sed 's/^#   //' >&2
}

fail_usage() {
    log_error "$1" >&2
    usage
    exit 2
}

is_absolute() {
    [[ "$1" == /* ]]
}

config=""
case "${1:-}" in
    help|--help|-h) usage; exit 0 ;;
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
[[ -n "$cli_dir" ]] && is_absolute "$cli_dir" || fail_usage "PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory"
cli_dll="$cli_dir/Farm.HostUpdate.Cli.dll"
[[ -f "$cli_dll" ]] || fail_usage "Farm.HostUpdate.Cli.dll not found in PRINTFARMER_HOST_UPDATE_CLI_DIR"

dotnet_host="${PRINTFARMER_DOTNET:-dotnet}"
if [[ -n "${PRINTFARMER_DOTNET:-}" ]]; then
    is_absolute "$dotnet_host" && [[ -x "$dotnet_host" ]] || fail_usage "PRINTFARMER_DOTNET must be an absolute executable path"
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
            [[ $# -ge 2 && "$2" =~ $DRIFT_TOKEN_RE ]] || fail_usage "--reapprove-drift requires the drift-<32 hex> token printed by --preview"
            args+=("$1" "$2")
            shift 2
            ;;
        *)
            fail_usage "unsupported argument: $1"
            ;;
    esac
done

exec "$dotnet_host" "$cli_dll" --config "$config" "${args[@]}"
