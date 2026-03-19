#!/usr/bin/env bash
# generate-certs.sh — Generate self-signed TLS certificates for PrintFarmer nginx.
# Usage: ./scripts/generate-certs.sh [output-dir]
#
# Produces tls.crt and tls.key in the output directory (default: deploy/nginx/certs/).
# The certificate is valid for 365 days and covers localhost + common LAN addresses.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CERT_DIR="${1:-$REPO_ROOT/deploy/nginx/certs}"

mkdir -p "$CERT_DIR"

# Detect LAN IP for SAN (best-effort)
LAN_IP=""
if command -v hostname >/dev/null 2>&1; then
    LAN_IP=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
fi

# Detect Tailscale IP for SAN (best-effort)
TAILSCALE_IP=""
if command -v tailscale >/dev/null 2>&1; then
    TAILSCALE_IP=$(tailscale ip -4 2>/dev/null || true)
fi

# Build Subject Alternative Names
SAN="DNS:localhost,DNS:printfarmer.local"
if [[ -n "$LAN_IP" ]]; then
    SAN="${SAN},IP:${LAN_IP}"
fi
if [[ -n "$TAILSCALE_IP" && "$TAILSCALE_IP" != "$LAN_IP" ]]; then
    SAN="${SAN},IP:${TAILSCALE_IP}"
fi
SAN="${SAN},IP:127.0.0.1"

echo "Generating self-signed TLS certificate..."
echo "  Output:  $CERT_DIR/tls.{crt,key}"
echo "  SANs:    $SAN"
echo "  Valid:   365 days"

openssl req -x509 -nodes -newkey rsa:2048 \
    -days 365 \
    -keyout "$CERT_DIR/tls.key" \
    -out "$CERT_DIR/tls.crt" \
    -subj "/CN=PrintFarmer/O=PrintFarmer" \
    -addext "subjectAltName=${SAN}" \
    2>/dev/null

chmod 600 "$CERT_DIR/tls.key"
chmod 644 "$CERT_DIR/tls.crt"

echo "Done. Certificates written to $CERT_DIR/"
echo ""
echo "To use with Docker Compose, the certs directory is mounted automatically."
echo "Access PrintFarmer at https://<host>:${HTTPS_PORT:-8443}"
