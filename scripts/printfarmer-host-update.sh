#!/usr/bin/env bash
# Host-local PrintFarmer host-update status/recovery wrapper (issues #2980, #2997).
#
# Runs Farm.HostUpdate.Cli without the API. Only three fixed operations are exposed; no arbitrary
# shell text, compose files, or credentials are accepted on the command line. This is NOT rollout
# authorization: it never starts a forward update.
#
#   printfarmer-host-update.sh --config /abs/host-update.json status [--release <id>] [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --preview [--json]
#   printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --confirm <id> [--json]
#   printfarmer-host-update.sh help
#
# Environment:
#   PRINTFARMER_HOST_UPDATE_CLI_DIR  absolute CLI directory. Optional in a release package, where it
#                                    defaults to the package's cli/ directory beside this script.
#   PRINTFARMER_DOTNET               absolute dotnet host; only for a framework-dependent CLI
#                                    directory (no Farm.HostUpdate.Cli apphost). Default: dotnet on PATH.
#
# Grammar and validation match scripts/printfarmer-host-update.ps1 (option names differ only in
# spelling). Exit codes are the CLI's (see docs/HOST_UPDATE_RUNBOOK.md); the wrapper itself only
# ever returns 2 for a usage or setup error, before the CLI runs.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common-utils.sh
source "$SCRIPT_DIR/common-utils.sh"

readonly RELEASE_RE='^(stable|insider):[0-9A-Za-z.+-]{1,128}$'
readonly REQUEST_RE='^[A-Za-z0-9._:-]{1,128}$'
readonly CLI_NAME='Farm.HostUpdate.Cli'

usage() {
    cat <<'USAGE'
usage:
  printfarmer-host-update.sh --config /abs/host-update.json status [--release <id>] [--json]
  printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --preview [--json]
  printfarmer-host-update.sh --config /abs/host-update.json recover --release <id> [--request-id <id>] --confirm <id> [--json]
  printfarmer-host-update.sh help
USAGE
}

fail_usage() {
    log_error "printfarmer-host-update: $1" >&2
    usage >&2
    exit 2
}

is_absolute() {
    [[ "$1" == /* ]]
}

case "${1:-}" in
    help|--help|-h) usage; exit 0 ;;
esac

config=""
command=""
release=""
request_id=""
confirm=""
preview=false
json=false
config_set=false

while [[ $# -gt 0 ]]; do
    token="$1"
    has_value=false
    [[ $# -ge 2 ]] && has_value=true
    case "$token" in
        --config)
            [[ "$config_set" == false ]] || fail_usage "--config may only be given once"
            [[ "$has_value" == true ]] || fail_usage "--config requires a value"
            config="$2"; config_set=true; shift 2 ;;
        --release)
            [[ -z "$release" ]] || fail_usage "--release may only be given once"
            [[ "$has_value" == true && "$2" =~ $RELEASE_RE ]] || fail_usage "--release requires a release id like stable:1.2.3"
            release="$2"; shift 2 ;;
        --request-id)
            [[ -z "$request_id" ]] || fail_usage "--request-id may only be given once"
            [[ "$has_value" == true && "$2" =~ $REQUEST_RE ]] || fail_usage "--request-id requires [A-Za-z0-9._:-]{1,128}"
            request_id="$2"; shift 2 ;;
        --confirm)
            [[ -z "$confirm" ]] || fail_usage "--confirm may only be given once"
            [[ "$has_value" == true && "$2" =~ $RELEASE_RE ]] || fail_usage "--confirm requires the release id retyped exactly"
            confirm="$2"; shift 2 ;;
        --preview)
            [[ "$preview" == false ]] || fail_usage "--preview may only be given once"
            preview=true; shift ;;
        --json)
            [[ "$json" == false ]] || fail_usage "--json may only be given once"
            json=true; shift ;;
        status|recover)
            [[ -z "$command" ]] || fail_usage "only one command may be given"
            command="$token"; shift ;;
        *)
            fail_usage "unsupported argument: $token" ;;
    esac
done

[[ "$config_set" == true ]] || fail_usage "--config <absolute-json-path> is required"
[[ -n "$command" ]] || fail_usage "missing command (status or recover)"
# Existence/readability is proven by the CLI (exit 3): a test here cannot tell denied from absent.
is_absolute "$config" || fail_usage "--config must be an absolute JSON file path"

cli_dir="${PRINTFARMER_HOST_UPDATE_CLI_DIR:-}"
if [[ -z "$cli_dir" && -d "$SCRIPT_DIR/cli" ]]; then
    # Release package layout: the wrapper sits beside the self-contained cli/ directory.
    cli_dir="$SCRIPT_DIR/cli"
fi
[[ -n "$cli_dir" ]] && is_absolute "$cli_dir" || fail_usage "PRINTFARMER_HOST_UPDATE_CLI_DIR must be an absolute directory"

launcher=()
if [[ -f "$cli_dir/$CLI_NAME" && -x "$cli_dir/$CLI_NAME" ]]; then
    # Self-contained package: run the apphost directly; no dotnet install is required.
    [[ -z "${PRINTFARMER_DOTNET:-}" ]] || fail_usage "PRINTFARMER_DOTNET applies only to a framework-dependent CLI directory"
    launcher=("$cli_dir/$CLI_NAME")
else
    [[ -f "$cli_dir/$CLI_NAME.dll" ]] || fail_usage "$CLI_NAME not found in the CLI directory"
    dotnet_host="${PRINTFARMER_DOTNET:-dotnet}"
    if [[ -n "${PRINTFARMER_DOTNET:-}" ]]; then
        is_absolute "$dotnet_host" && [[ -f "$dotnet_host" && -x "$dotnet_host" ]] || fail_usage "PRINTFARMER_DOTNET must be an absolute executable path"
    fi
    launcher=("$dotnet_host" "$cli_dir/$CLI_NAME.dll")
fi

# The CLI re-validates everything, including the canonical release grammar and option combinations.
args=("$command")
[[ -z "$release" ]] || args+=(--release "$release")
[[ -z "$request_id" ]] || args+=(--request-id "$request_id")
[[ "$preview" == false ]] || args+=(--preview)
[[ -z "$confirm" ]] || args+=(--confirm "$confirm")
[[ "$json" == false ]] || args+=(--json)

exec "${launcher[@]}" --config "$config" "${args[@]}"
