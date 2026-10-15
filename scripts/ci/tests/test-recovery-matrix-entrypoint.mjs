import assert from 'node:assert/strict';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const repoRoot = path.resolve(import.meta.dirname, '../../..');
const script = path.join(repoRoot, 'scripts/ci/recovery-matrix/run-cell.sh');

function toBashPath(value) {
  return value.replace(/^([A-Za-z]):\\/, (_, drive) => `/${drive.toLowerCase()}/`).replaceAll('\\', '/');
}

function createHarness() {
  const root = mkdtempSync(path.join(repoRoot, '.recovery-matrix-test-'));
  const bin = path.join(root, 'bin');
  const log = path.join(root, 'docker.log');
  const ran = path.join(root, 'ran');
  mkdirSync(bin);
  writeExecutable(path.join(bin, 'docker'), `#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "${toBashPath(log)}"
case "\${1:-}" in
  network)
    case "\${2:-}" in
      create)
        if [[ "\${PF_TEST_LOCK_HELD:-0}" == 1 ]]; then exit 1; fi
        ;;
      inspect)
        if [[ "\${PF_TEST_LOCK_HELD:-0}" == 1 ]]; then
          printf '%s\n' 'fixture-run-123'
        else
          printf '%s\n' "\${PF_RECOVERY_MATRIX_RUN_ID:-recovery-matrix-test}"
        fi
        ;;
      rm) ;;
    esac
    ;;
  ps|volume) ;;
esac
`);
  return {
    root,
    bin,
    log,
    ran,
    cleanup: () => rmSync(root, { recursive: true, force: true }),
  };
}

function hasBash() {
  return spawnSync('bash', ['-lc', 'true'], { stdio: 'ignore' }).status === 0;
}

function writeExecutable(file, content) {
  writeFileSync(file, content.replaceAll('\r\n', '\n'));
  chmodSync(file, 0o755);
}

function run(harness, args, env = {}) {
  return spawnSync('bash', [toBashPath(script), ...args], {
    cwd: repoRoot,
    env: {
      ...process.env,
      PATH: `${harness.bin}${path.delimiter}${process.env.PATH}`,
      PF_RECOVERY_MATRIX_RUN_ID: 'test-run-123',
      ...env,
    },
    encoding: 'utf8',
  });
}

test('run-cell.sh serializes a command and releases only its owned lock', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const result = run(harness, ['--', 'bash', '-c', `printf '%s' ok > '${toBashPath(harness.ran)}'`]);
    assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
    assert.equal(readFileSync(harness.ran, 'utf8'), 'ok');
    const log = readFileSync(harness.log, 'utf8');
    assert.match(log, /network create/);
    assert.match(log, /network rm/);
  } finally {
    harness.cleanup();
  }
});

test('run-cell.sh fails fast with status 75 when another run owns the daemon', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const result = run(harness, ['--', 'bash', '-c', 'exit 99'], { PF_TEST_LOCK_HELD: '1' });
    assert.equal(result.status, 75, `${result.stderr}\n${result.stdout}`);
    assert.match(result.stderr, /exclusive Docker daemon.*fixture-run-123/);
    assert.doesNotMatch(readFileSync(harness.log, 'utf8'), /network rm/);
  } finally {
    harness.cleanup();
  }
});

test('run-cell.sh remains directly executable', () => {
  const result = spawnSync('git', [
    'ls-files', '--stage', '--', 'scripts/ci/recovery-matrix/run-cell.sh',
  ], { cwd: repoRoot, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /^100755 /m);
});
