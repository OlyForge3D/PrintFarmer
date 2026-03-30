#!/usr/bin/env bash
# generate-certs.sh — Generate Apple-compatible TLS certificates for PrintFarmer nginx.
# Usage: ./scripts/generate-certs.sh [output-dir]
#
# Produces:
#   tls.crt / tls.key        - server cert/key for nginx
#   tls-fullchain.crt        - concatenated server + CA chain
#   ca.crt / ca.cer / ca.key - private CA for device trust
#
# The server certificate is valid for 365 days, uses CA:FALSE, includes
# serverAuth EKU, and covers localhost + detected LAN/Tailscale IPs.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_CERT_DIR="$REPO_ROOT/deploy/nginx/certs"
RUNTIME_CERT_DIR="$REPO_ROOT/nginx/certs"
DEPLOY_CONFIG_FILE="$REPO_ROOT/.deploy-config"

if [[ -z "${HTTPS_PORT:-}" && -f "$DEPLOY_CONFIG_FILE" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$DEPLOY_CONFIG_FILE"
    set +a
fi

array_contains() {
    local needle="$1"
    shift || true
    local item
    for item in "$@"; do
        [[ "$item" == "$needle" ]] && return 0
    done
    return 1
}

dedupe_paths() {
    local unique=()
    local candidate
    for candidate in "$@"; do
        [[ -z "$candidate" ]] && continue
        if ! array_contains "$candidate" "${unique[@]:-}"; then
            unique+=("$candidate")
        fi
    done
    printf '%s\n' "${unique[@]:-}"
}

TARGET_CERT_DIRS=()
should_mirror_legacy_runtime_dir() {
    [[ -d "$(dirname "$RUNTIME_CERT_DIR")" ]]
}

if [[ $# -gt 0 && -n "${1:-}" ]]; then
    TARGET_CERT_DIRS+=("$1")
    case "$1" in
        "$REPO_CERT_DIR"|./deploy/nginx/certs|deploy/nginx/certs)
            if should_mirror_legacy_runtime_dir; then
                TARGET_CERT_DIRS+=("$RUNTIME_CERT_DIR")
            fi
            ;;
        "$RUNTIME_CERT_DIR"|./nginx/certs|nginx/certs)
            TARGET_CERT_DIRS+=("$REPO_CERT_DIR")
            ;;
    esac
else
    TARGET_CERT_DIRS+=("$REPO_CERT_DIR")
    if should_mirror_legacy_runtime_dir; then
        TARGET_CERT_DIRS+=("$RUNTIME_CERT_DIR")
    fi
fi

DEDUPED_TARGET_CERT_DIRS=()
while IFS= read -r line; do
    [[ -n "$line" ]] && DEDUPED_TARGET_CERT_DIRS+=("$line")
done < <(dedupe_paths "${TARGET_CERT_DIRS[@]}")
if [[ ${#DEDUPED_TARGET_CERT_DIRS[@]} -eq 0 ]]; then
    echo "❌ No certificate output directory could be determined."
    exit 1
fi
TARGET_CERT_DIRS=("${DEDUPED_TARGET_CERT_DIRS[@]}")
CERT_DIR="${TARGET_CERT_DIRS[0]}"

mkdir -p "$CERT_DIR"

if ! command -v openssl >/dev/null 2>&1; then
    echo "❌ openssl is required but was not found in PATH."
    exit 1
fi

is_ipv4() {
    [[ "$1" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]
}

detect_lan_ip() {
    local ip=""

    if command -v hostname >/dev/null 2>&1; then
        ip=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
    fi
    if [[ -n "$ip" ]]; then
        echo "$ip"
        return 0
    fi

    if command -v route >/dev/null 2>&1 && command -v ipconfig >/dev/null 2>&1; then
        local interface=""
        interface=$(route -n get default 2>/dev/null | awk '/interface:/{print $2; exit}' || true)
        if [[ -n "$interface" ]]; then
            ip=$(ipconfig getifaddr "$interface" 2>/dev/null || true)
        fi
    fi

    if [[ -n "$ip" ]]; then
        echo "$ip"
    fi
}

# Detect LAN IP for SAN (best-effort)
LAN_IP="$(detect_lan_ip)"

# Detect Tailscale IP for SAN (best-effort)
TAILSCALE_IP=""
if command -v tailscale >/dev/null 2>&1; then
    TAILSCALE_IP=$(tailscale ip -4 2>/dev/null | head -n1 || true)
fi

SAN_ENTRIES=("localhost" "printfarmer.local" "127.0.0.1")
if [[ -n "$LAN_IP" ]]; then
    SAN_ENTRIES+=("$LAN_IP")
fi
if [[ -n "$TAILSCALE_IP" && "$TAILSCALE_IP" != "$LAN_IP" ]]; then
    SAN_ENTRIES+=("$TAILSCALE_IP")
fi

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

CA_CONFIG="$TEMP_DIR/ca.cnf"
SERVER_CONFIG="$TEMP_DIR/server.cnf"

cat > "$CA_CONFIG" <<EOF
[ req ]
distinguished_name = dn
x509_extensions = v3_ca
prompt = no

[ dn ]
CN = PrintFarmer Local CA
O = PrintFarmer

[ v3_ca ]
basicConstraints = critical,CA:TRUE
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always,issuer
EOF

{
    echo "[ req ]"
    echo "distinguished_name = dn"
    echo "req_extensions = v3_req"
    echo "prompt = no"
    echo
    echo "[ dn ]"
    echo "CN = PrintFarmer"
    echo "O = PrintFarmer"
    echo
    echo "[ v3_req ]"
    echo "basicConstraints = critical,CA:FALSE"
    echo "keyUsage = critical,digitalSignature,keyEncipherment"
    echo "extendedKeyUsage = serverAuth"
    echo "subjectAltName = @alt_names"
    echo "subjectKeyIdentifier = hash"
    echo
    echo "[ alt_names ]"

    dns_index=1
    ip_index=1
    for san in "${SAN_ENTRIES[@]}"; do
        if is_ipv4 "$san"; then
            echo "IP.${ip_index} = ${san}"
            ip_index=$((ip_index + 1))
        else
            echo "DNS.${dns_index} = ${san}"
            dns_index=$((dns_index + 1))
        fi
    done
} > "$SERVER_CONFIG"

echo "Generating local CA and TLS server certificate..."
echo "  Output:  $CERT_DIR/{tls.crt,tls.key,ca.crt,ca.cer}"
echo "  Valid:   server 365 days | CA 825 days"
echo "  SANs:"
for san in "${SAN_ENTRIES[@]}"; do
    echo "    - $san"
done

openssl genrsa -out "$CERT_DIR/ca.key" 4096 >/dev/null 2>&1
openssl req -x509 -new -key "$CERT_DIR/ca.key" -sha256 -days 825 \
    -out "$CERT_DIR/ca.crt" -config "$CA_CONFIG" >/dev/null 2>&1

openssl genrsa -out "$CERT_DIR/tls.key" 2048 >/dev/null 2>&1
openssl req -new -key "$CERT_DIR/tls.key" -out "$TEMP_DIR/tls.csr" \
    -config "$SERVER_CONFIG" >/dev/null 2>&1
openssl x509 -req -in "$TEMP_DIR/tls.csr" -CA "$CERT_DIR/ca.crt" \
    -CAkey "$CERT_DIR/ca.key" -CAcreateserial -out "$CERT_DIR/tls.crt" \
    -days 365 -sha256 -extfile "$SERVER_CONFIG" -extensions v3_req >/dev/null 2>&1

cat "$CERT_DIR/tls.crt" "$CERT_DIR/ca.crt" > "$CERT_DIR/tls-fullchain.crt"
openssl x509 -in "$CERT_DIR/ca.crt" -outform der -out "$CERT_DIR/ca.cer"

chmod 600 "$CERT_DIR/ca.key" "$CERT_DIR/tls.key"
chmod 644 "$CERT_DIR/ca.crt" "$CERT_DIR/ca.cer" "$CERT_DIR/tls.crt" "$CERT_DIR/tls-fullchain.crt"

if [[ ${#TARGET_CERT_DIRS[@]} -gt 1 ]]; then
    for mirror_dir in "${TARGET_CERT_DIRS[@]:1}"; do
        mkdir -p "$mirror_dir"
        cp "$CERT_DIR/ca.key" "$mirror_dir/ca.key"
        cp "$CERT_DIR/ca.crt" "$mirror_dir/ca.crt"
        cp "$CERT_DIR/ca.cer" "$mirror_dir/ca.cer"
        cp "$CERT_DIR/tls.key" "$mirror_dir/tls.key"
        cp "$CERT_DIR/tls.crt" "$mirror_dir/tls.crt"
        cp "$CERT_DIR/tls-fullchain.crt" "$mirror_dir/tls-fullchain.crt"
        if [[ -f "$CERT_DIR/ca.srl" ]]; then
            cp "$CERT_DIR/ca.srl" "$mirror_dir/ca.srl"
        fi
        chmod 600 "$mirror_dir/ca.key" "$mirror_dir/tls.key"
        chmod 644 "$mirror_dir/ca.crt" "$mirror_dir/ca.cer" "$mirror_dir/tls.crt" "$mirror_dir/tls-fullchain.crt"
    done
fi

echo "Done. Certificates written to $CERT_DIR/"
if [[ ${#TARGET_CERT_DIRS[@]} -gt 1 ]]; then
    echo "Mirrored certificates to:"
    for mirror_dir in "${TARGET_CERT_DIRS[@]:1}"; do
        echo "  - $mirror_dir"
    done
fi
echo ""
echo "Use tls-fullchain.crt + tls.key for nginx."
echo "Install ca.cer on iPhone/iPad and enable trust in:"
echo "  Settings > General > About > Certificate Trust Settings"
echo "Do not install tls.crt on iPhone/iPad."
echo "If Settings shows a trusted certificate named 'PrintFarmer', remove it and trust 'PrintFarmer Local CA' instead."
echo "Users can open http://<host>/install-ca to download the CA."
echo "Access PrintFarmer at https://<host>:${HTTPS_PORT:-8443}"
