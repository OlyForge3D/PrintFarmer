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
CERT_DIR="${1:-$REPO_ROOT/deploy/nginx/certs}"

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

echo "Done. Certificates written to $CERT_DIR/"
echo ""
echo "Use tls.crt + tls.key for nginx."
echo "Install ca.cer on iPhone/iPad and enable trust in:"
echo "  Settings > General > About > Certificate Trust Settings"
echo "Users can open http://<host>/install-ca to download the CA."
echo "Access PrintFarmer at https://<host>:${HTTPS_PORT:-8443}"
