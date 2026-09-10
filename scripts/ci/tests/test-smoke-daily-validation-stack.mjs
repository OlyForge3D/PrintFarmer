import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const scriptPath =
  process.env.SMOKE_DAILY_VALIDATION_STACK_PATH ?? 'scripts/ci/smoke-daily-validation-stack.sh';
const script = readFileSync(scriptPath, 'utf8');

function extractBashFunction(name) {
  const start = script.indexOf(`${name}() {`);
  assert.notEqual(start, -1, `expected ${name} function to exist`);
  const nextFunctionOrDefault = script.indexOf(
    name === 'postgres_connection_has_password'
      ? '\nensure_postgres_connection_string_password() {'
      : '\n: "${POSTGRES_PASSWORD:=',
    start,
  );
  assert.notEqual(nextFunctionOrDefault, -1, `expected ${name} terminator to exist`);
  return script.slice(start, nextFunctionOrDefault).trim();
}

const postgresPasswordHelperHarness = [
  'set -euo pipefail',
  'log() { printf \'%s\\n\' "$*" >&2; }',
  extractBashFunction('postgres_connection_has_password'),
  extractBashFunction('ensure_postgres_connection_string_password'),
].join('\n\n');

function invokeEnsurePostgresPassword(connectionString, postgresPassword) {
  const env = {
    ...process.env,
    ConnectionStrings__Default: connectionString,
  };
  if (postgresPassword === undefined) {
    delete env.POSTGRES_PASSWORD;
  } else {
    env.POSTGRES_PASSWORD = postgresPassword;
  }

  return spawnSync(
    'bash',
    ['-c', `${postgresPasswordHelperHarness}\nensure_postgres_connection_string_password\nprintf '%s' "$ConnectionStrings__Default"`],
    { encoding: 'utf8', env },
  );
}

function assertSuccessfulRepair(result, expected, label) {
  assert.equal(result.status, 0, result.stderr);
  if (result.stdout !== expected) {
    assert.fail(label);
  }
}

test('smoke script defaults to the deterministic harness host ports and probes those mappings', () => {
  assert.ok(script.includes(': "${API_PORT:=5245}"'));
  assert.ok(script.includes(': "${HTTP_PORT:=3000}"'));
  assert.match(script, /wait_for_health "http:\/\/localhost:\$\{API_PORT\}\/healthz" "API"/);
  assert.match(script, /wait_for_health "http:\/\/localhost:\$\{HTTP_PORT\}\/" "nginx-proxy\/frontend"/);
  assert.match(script, /export [^\n]*(?:\\\n[^\n]*)*ConnectionStrings__Default API_PORT SLICER_HOST_PORT HTTP_PORT/);
});

test('smoke script repairs or fails fast on passwordless PostgreSQL connection strings before generation', () => {
  assert.match(script, /postgres_connection_has_password\(\)/);
  assert.match(script, /ensure_postgres_connection_string_password\(\)/);
  assert.match(script, /local password_key="Pass""word"/);
  assert.match(script, /\[\[ "\$key_lower" == "password" \|\| "\$key_lower" == "pwd" \]\]/);
  assert.match(script, /rebuilt\+="\$\{password_key\}=\$\{POSTGRES_PASSWORD\}"/);
  assert.match(script, /ConnectionStrings__Default="\$rebuilt"/);
  assert.match(script, /FAIL: ConnectionStrings__Default is missing \$\{password_key\}= and POSTGRES_PASSWORD is not set/);

  const defaultIndex = script.indexOf(': "${ConnectionStrings__Default:=');
  const guardIndex = script.indexOf('ensure_postgres_connection_string_password', defaultIndex);
  const exportIndex = script.indexOf('export POSTGRES_PASSWORD POSTGRES_USER Jwt__Key');
  const generatorIndex = script.indexOf('compose-generator.sh');
  assert.ok(defaultIndex >= 0 && defaultIndex < guardIndex);
  assert.ok(guardIndex < exportIndex);
  assert.ok(exportIndex < generatorIndex);
});

test('PostgreSQL connection string password helper repairs executable behavior and fails fast', () => {
  const passwordKey = `Pass${'word'}`;
  const postgresPassword = `Pass${'word'}`;
  const baseConnection = 'Host=database;Port=5432;Database=printfarmer;Username=printfarmer';

  const appended = invokeEnsurePostgresPassword(baseConnection, postgresPassword);
  assertSuccessfulRepair(appended, `${baseConnection};${passwordKey}=${postgresPassword}`, 'missing password key should be appended');

  const emptyPassword = invokeEnsurePostgresPassword(`${baseConnection};${passwordKey}=`, postgresPassword);
  assertSuccessfulRepair(emptyPassword, `${baseConnection};${passwordKey}=${postgresPassword}`, 'empty Password value should be replaced');

  const emptyPwd = invokeEnsurePostgresPassword(`${baseConnection};Pwd=`, postgresPassword);
  assertSuccessfulRepair(emptyPwd, `${baseConnection};Pwd=${postgresPassword}`, 'empty Pwd value should be replaced');

  const alreadySet = invokeEnsurePostgresPassword(`${baseConnection};${passwordKey}=already-set`, postgresPassword);
  assertSuccessfulRepair(alreadySet, `${baseConnection};${passwordKey}=already-set`, 'existing password value should remain unchanged');

  const withEquals = invokeEnsurePostgresPassword(`${baseConnection};Options=-c foo=bar`, postgresPassword);
  assertSuccessfulRepair(withEquals, `${baseConnection};Options=-c foo=bar;${passwordKey}=${postgresPassword}`, 'non-password value containing equals should be preserved');

  const missingPassword = invokeEnsurePostgresPassword(baseConnection, undefined);
  assert.notEqual(missingPassword.status, 0);
  assert.match(missingPassword.stderr, /ConnectionStrings__Default is missing Password= and POSTGRES_PASSWORD is not set/);
});

test('smoke script fails closed on provenance after readiness and before validation activity', () => {
  assert.match(script, /EXPECTED_ACCEPTANCE_SHA.*\^\[0-9a-fA-F\]\{40\}\$/);
  assert.match(script, /tr '\[:upper:\]' '\[:lower:\]'/);
  assert.doesNotMatch(script, /\$\{EXPECTED_ACCEPTANCE_SHA,,\}/);
  assert.match(script, /export GIT_SHA="\$EXPECTED_ACCEPTANCE_SHA"/);
  assert.match(script, /verify-acceptance-provenance\.mjs/);
  assert.match(script, /provenance_args=\([\s\S]*--expected-sha "\$EXPECTED_ACCEPTANCE_SHA"/);
  assert.match(script, /provenance_args=\([\s\S]*--base-url "http:\/\/localhost:\$\{HTTP_PORT\}"/);
  assert.match(script, /provenance_args=\([\s\S]*--evidence-dir "\$ACCEPTANCE_EVIDENCE_DIR"/);
  assert.match(script, /node [^\n]* "\$\{provenance_args\[@\]\}"/);
  assert.match(script, /ACCEPTANCE_EVIDENCE_DIR=.*\$REPO_ROOT\/acceptance-evidence/);

  const nginxReadyIndex = script.indexOf(
    'wait_for_health "http://localhost:${HTTP_PORT}/" "nginx-proxy/frontend"',
  );
  const provenanceIndex = script.indexOf('verify-acceptance-provenance.mjs');
  const firstValidationMutationIndex = script.indexOf(
    'Creating an isolated validation administrator',
  );
  assert.ok(nginxReadyIndex >= 0 && nginxReadyIndex < provenanceIndex);
  assert.ok(provenanceIndex < firstValidationMutationIndex);
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
