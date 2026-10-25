#!/usr/bin/env bash
# Source this after Python 3 is available. Installation is local to the invoking user.

DOTNET_SDK_SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$DOTNET_SDK_SCRIPT_DIR/common-utils.sh"
DOTNET_SDK_PYTHON="$(resolve_python_cmd)" || return 1
DOTNET_SDK_VERSION="$("$DOTNET_SDK_PYTHON" "$DOTNET_SDK_SCRIPT_DIR/dotnet-sdk.py" version)" || return 1

ensure_dotnet_sdk() {
    local selected user_home
    if [[ "$(id -u)" == 0 && -n "${SUDO_USER:-}" && "$SUDO_USER" != root ]]; then
        user_home="$(sudo -H -u "$SUDO_USER" sh -c 'printf "%s" "$HOME"')" || return 1
        log_info "Ensuring approved .NET SDK $DOTNET_SDK_VERSION for $SUDO_USER"
        sudo -H -u "$SUDO_USER" bash "$DOTNET_SDK_SCRIPT_DIR/dotnet-install.sh" \
            --version "$DOTNET_SDK_VERSION" --install-dir "$user_home/.dotnet" || return 1
        export DOTNET_ROOT="$user_home/.dotnet"
        export PATH="$DOTNET_ROOT:$PATH"
        "$DOTNET_SDK_PYTHON" "$DOTNET_SDK_SCRIPT_DIR/dotnet-sdk.py" verify
        return
    fi
    selected="$(cd "$DOTNET_SDK_SCRIPT_DIR/.." && dotnet --version 2>/dev/null)" || selected=""
    if [[ "$selected" != "$DOTNET_SDK_VERSION" ]]; then
        log_info "Installing approved .NET SDK $DOTNET_SDK_VERSION from global.json"
        bash "$DOTNET_SDK_SCRIPT_DIR/dotnet-install.sh" \
            --version "$DOTNET_SDK_VERSION" --install-dir "$HOME/.dotnet" || return 1
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$DOTNET_ROOT:$PATH"
    fi
    "$DOTNET_SDK_PYTHON" "$DOTNET_SDK_SCRIPT_DIR/dotnet-sdk.py" verify
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    log_error "This library must be sourced before calling ensure_dotnet_sdk." >&2
    exit 1
fi
