import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const script = readFileSync('scripts/ci/smoke-daily-validation-stack.sh', 'utf8');

test('smoke script defaults to the deterministic harness host ports and probes those mappings', () => {
  assert.ok(script.includes(': "${API_PORT:=5245}"'));
  assert.ok(script.includes(': "${HTTP_PORT:=3000}"'));
  assert.match(script, /wait_for_health "http:\/\/localhost:\$\{API_PORT\}\/healthz" "API"/);
  assert.match(script, /wait_for_health "http:\/\/localhost:\$\{HTTP_PORT\}\/" "nginx-proxy\/frontend"/);
  assert.match(script, /export [^\n]*(?:\\\n[^\n]*)*ConnectionStrings__Default API_PORT SLICER_HOST_PORT HTTP_PORT/);
});

test('smoke script is bash-strict and makes Docker-unavailable behavior explicit', () => {
  assert.match(script, /^#!\/usr\/bin\/env bash/);
  assert.match(script, /^set -euo pipefail/m);

  // Docker-unavailable / daemon-unreachable must be an explicit, non-fatal SKIP
  // (exit 0) so this script can be wired into local unit test suites without
  // failing environments that have no Docker daemon.
  assert.match(script, /if ! command -v docker >\/dev\/null 2>&1; then/);
  assert.match(script, /SKIP: docker is not installed/);
  assert.match(script, /if ! docker info >\/dev\/null 2>&1; then/);
  assert.match(script, /SKIP: docker daemon is not reachable/);

  // Both skip branches must exit 0, not fail the command.
  const skipBlocks = script.match(/SKIP:[^\n]*\n\s*exit 0/g) ?? [];
  assert.equal(skipBlocks.length, 2, 'both Docker-unavailable branches must exit 0');
});

test('smoke script boots the four-instance emulator topology via the generator', () => {
  assert.match(script, /source "\$REPO_ROOT\/scripts\/docker\/container-versions\.conf"/);
  assert.match(
    script,
    /export PRINTFARMER_BUILD_CONTEXT="\$\{PRINTFARMER_BUILD_CONTEXT:-\$REPO_ROOT\}"/,
  );
  assert.match(
    script,
    /PRINTFARMER_DOCKERFILE.*scripts\/docker\/dockerfiles\/Dockerfile\.multistage/,
    'local builds must use the tracked canonical Dockerfile rather than an ignored generated root copy',
  );
  assert.match(script, /compose-generator\.sh/);
  assert.match(script, /--architecture microservices/);
  assert.match(script, /--include-discovery/);
  assert.match(script, /--include-moonraker-emulator/);
  assert.match(script, /--enable-orca-worker yes/);
  assert.match(script, /docker-compose\.daily-validation\.yml/);
  assert.match(script, /docker-compose\.daily-registry\.yml/);
  assert.match(script, /PRINTFARMER_MOONRAKER_EMULATOR_IMAGE/);
  assert.match(script, /up -d --scale orcaslicer-worker=1/);
  assert.match(script, /export ENABLE_ORCA_WORKER_PREVIOUS=no/);

  // Cleanup must always run and must be scoped to the generated stack dir
  // and its own compose project, never a shared/default project.
  assert.match(script, /trap cleanup EXIT/);
  assert.match(script, /down --volumes --remove-orphans/);
  assert.match(script, /compose images -q api/);
  assert.match(script, /-v "\$STACK_DIR:\/cleanup"/);
  assert.match(script, /rm -rf \/cleanup\/\.volumes/);
  assert.match(script, /rm -rf "\$STACK_DIR"/);
});

test('smoke script asserts all four emulator instances, real Moonraker printers, offline unreachability, discovery fixtures, and exactly one worker', () => {
  // Four distinct loopback ports, one per running emulator instance.
  for (const portVar of [
    'MOONRAKER_EMULATOR_PORT',
    'MOONRAKER_EMULATOR_PRINTING_PORT',
    'MOONRAKER_EMULATOR_PAUSED_PORT',
    'MOONRAKER_EMULATOR_SHUTDOWN_PORT',
  ]) {
    assert.match(script, new RegExp(`\\$\\{${portVar}\\}/healthz`));
  }
  assert.match(script, /api\/printers/);
  assert.match(script, /api\/setup\/initial-admin/);
  assert.match(script, /api\/auth\/login/);
  assert.match(script, /Authorization: Bearer \$smoke_auth_token/);
  assert.match(script, /\.backend == "Moonraker"/);
  assert.match(script, /for _ in \{1\.\.30\}; do[\s\S]*?moonraker_count[\s\S]*?sleep 2/);
  assert.match(script, /moonraker_count.*-lt 4/);
  assert.match(script, /\.backend == "TestEmulator"/);
  assert.match(script, /test_emulator_count.*-ne 0/);

  // The seeded "Moonraker Offline" printer has no running listener and must
  // report isOnline == false rather than being silently ignored.
  assert.match(script, /Moonraker Offline/);
  assert.match(script, /\.isOnline/);
  assert.match(script, /if length == 0 then "missing" else \(\.\[0\]\.isOnline \| tostring\) end/);
  assert.doesNotMatch(script, /\.isOnline \/\/ "missing"/);
  assert.match(script, /offline_is_online.*!= "false"/);

  // Deterministic fixture discovery bypasses physical probing: the scan
  // proves the discovery contract (fixture entries with expected
  // hostname/backend fields) without contacting the emulator or performing
  // any Moonraker handshake. DiscoveryController.ScanAsync maps
  // DiscoveredPrinterDto into the local DiscoveryResult type, serialized
  // camelCase as .hostname and .printerBackend (backend explicitly
  // lowercased via ToLowerInvariant()), not .name / .backend.
  assert.match(script, /printer-discovery curl/);
  assert.match(script, /api\/discovery\/scan\?autoRegister=false/);
  assert.match(script, /Discovered Voron V2\.4/);
  assert.match(script, /Discovered Prusa MK4S/);
  assert.match(script, /\.hostname == "Discovered Voron V2\.4" and \.printerBackend == "moonraker"/);
  assert.match(script, /\.hostname == "Discovered Prusa MK4S" and \.printerBackend == "moonraker"/);
  assert.match(script, /"\$voron_found" -lt 1 \|\| "\$prusa_found" -lt 1/);
  // Must not overstate the scan as a live connection/handshake proof.
  assert.doesNotMatch(script, /discovery scan.*handshake/i);
  assert.doesNotMatch(script, /real Moonraker connection/i);

  assert.match(script, /orcaslicer-worker/);
  assert.match(script, /\^orcaslicer-worker\(-previous\)\?\$/);
  assert.match(script, /worker_count.*-ne 1/);
  assert.match(script, /worker_services.*!= "orcaslicer-worker"/);

  // Assertion failures must be fatal, not swallowed.
  assert.match(script, /FAIL:.*\n\s*(printf|compose logs|compose ps)/);
  const exitOnFail = script.match(/FAIL:[^\n]*\n(?:[^\n]*\n)?\s*exit 1/g) ?? [];
  assert.ok(exitOnFail.length >= 5, 'every assertion failure branch must exit 1');
});
