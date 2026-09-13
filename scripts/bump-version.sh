#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common-utils.sh"
ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

if [[ $# != 1 || ! "$1" =~ ^(major|minor|patch)$ ]]; then
  log_error "Usage: bump-version.sh <major|minor|patch>. Prerelease N is reserved by the release ledger."
  exit 2
fi

node --input-type=module - "$1" <<'JS'
import { readFileSync, writeFileSync } from 'node:fs';
import { parseVersionFile } from './scripts/ci/release-policy.mjs';
const version = parseVersionFile(readFileSync('VERSION', 'utf8')).split('.').map(BigInt);
const index = ['major', 'minor', 'patch'].indexOf(process.argv[2]);
version[index]++;
for (let next = index + 1; next < 3; next++) version[next] = 0n;
writeFileSync('VERSION', `v${version.join('.')}\n`);
JS
bash "$SCRIPT_DIR/sync-monorepo-version.sh"
log_info "VERSION and frontend package metadata updated. Review and commit; no tag or push was performed."
