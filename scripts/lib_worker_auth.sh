#!/usr/bin/env bash

# Shared worker-auth bootstrap key setup for local launch scripts.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    echo "This file is a shared library and must be sourced." >&2
    exit 1
fi

ensure_worker_auth_shared_key() {
    if [[ -n "${WorkerAuth__SharedKey:-}" ]]; then
        export WorkerAuth__SharedKey
        return 0
    fi

    local generated_key=""
    if command -v openssl >/dev/null 2>&1; then
        generated_key="$(openssl rand -hex 32)"
    elif [[ -r /dev/urandom ]] && command -v base64 >/dev/null 2>&1; then
        generated_key="$(head -c 32 /dev/urandom | base64 | tr -d '\r\n=')"
    else
        echo "Unable to generate WorkerAuth__SharedKey securely; install openssl or provide the variable." >&2
        return 1
    fi

    if [[ -z "$generated_key" ]]; then
        echo "Secure worker authentication key generation returned an empty value." >&2
        return 1
    fi

    export WorkerAuth__SharedKey="$generated_key"
}
