import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, chmodSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

import { cellIds } from '../recovery-matrix/cells.mjs';
import { faultCellIds } from '../recovery-matrix/fault-cells.mjs';
import { importCellIds } from '../recovery-matrix/import-cells.mjs';

const repoRoot = path.resolve(import.meta.dirname, '../../..');
const script = path.join(repoRoot, 'scripts/ci/recovery-matrix/run-cell.sh');
const powershellScript = path.join(repoRoot, 'scripts/ci/recovery-matrix/run-cell.ps1');

function toBashPath(value) {
  return value.replace(/^([A-Za-z]):\\/, (_, drive) => `/${drive.toLowerCase()}/`).replaceAll('\\', '/');
}

function hasBash() {
  return spawnSync('bash', ['-lc', 'true'], { stdio: 'ignore' }).status === 0;
}

function writeExecutable(file, content) {
  writeFileSync(file, content.replaceAll('\r\n', '\n'));
  chmodSync(file, 0o755);
}

function createHarness() {
  const scratchRoot = path.join(repoRoot, '.recovery-matrix-work');
  mkdirSync(scratchRoot, { recursive: true });
  const root = mkdtempSync(path.join(scratchRoot, 'test-entrypoint-'));
  const bin = path.join(root, 'bin');
  const work = path.join(root, 'work');
  const composePlugin = path.join(root, 'docker-compose');
  mkdirSync(bin);
  mkdirSync(work);
  writeExecutable(composePlugin, '#!/usr/bin/env bash\nexit 0\n');
  const runLog = path.join(root, 'node-run.log');
  const dockerLog = path.join(root, 'docker.log');
  writeExecutable(path.join(bin, 'node'), `#!/usr/bin/env bash
set -euo pipefail
if [[ " $* " == *" --input-type=module "* ]]; then
  case "$*" in
    *import-cells.mjs*) printf '%s\n' "${importCellIds.join(' ')}" ;;
    *fault-cells.mjs*) printf '%s\n' "${faultCellIds.join(' ')}" ;;
    *) printf '%s\n' "${cellIds.join(' ')}" ;;
  esac
  exit 0
fi
printf '%s\n' "$*" >> "${toBashPath(runLog)}"
exit 0
`);
  writeExecutable(path.join(bin, 'docker'), `#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "${toBashPath(dockerLog)}"
case "\${1:-}" in
  inspect) printf '%s\n' '172.30.50.10' ;;
  info) printf '%s\n' "${toBashPath(composePlugin)}" ;;
esac
exit 0
`);
  writeExecutable(path.join(bin, 'jq'), '#!/usr/bin/env bash\nexit 0\n');
  writeExecutable(path.join(bin, 'cosign'), '#!/usr/bin/env bash\nexit 0\n');
  return {
    root,
    bin,
    work,
    runLog,
    dockerLog,
    cosign: path.join(bin, 'cosign'),
    cleanup: () => rmSync(root, { recursive: true, force: true }),
  };
}

test('run-cell.sh expands all cells with executable cosign and distinct evidence files', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const evidence = path.join(harness.work, 'matrix.json');
    const result = spawnSync('bash', [
      toBashPath(script),
      '--cell', 'all',
      '--work-dir', toBashPath(harness.work),
      '--evidence', toBashPath(evidence),
      '--cosign', toBashPath(harness.cosign),
      '--keep-work',
    ], {
      cwd: repoRoot,
      env: { ...process.env, PATH: `${harness.bin}${path.delimiter}${process.env.PATH}` },
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
    const invocations = readFileSync(harness.runLog, 'utf8').trim().split(/\r?\n/);
    assert.equal(invocations.length, cellIds.length);
    for (const cellId of cellIds) {
      const line = invocations.find((candidate) => candidate.includes(`--cell ${cellId}`));
      assert.ok(line, `expected invocation for ${cellId}`);
      assert.match(line, new RegExp(`--evidence .*matrix-${cellId}\\.json`));
    }
  } finally {
    harness.cleanup();
  }
});

test('run-cell.sh expands the imports group to every live import cell', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const evidence = path.join(harness.work, 'imports.json');
    const result = spawnSync('bash', [
      toBashPath(script),
      '--cell', 'imports',
      '--work-dir', toBashPath(harness.work),
      '--evidence', toBashPath(evidence),
      '--cosign', toBashPath(harness.cosign),
      '--keep-work',
    ], {
      cwd: repoRoot,
      env: { ...process.env, PATH: `${harness.bin}${path.delimiter}${process.env.PATH}` },
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
    const invocations = readFileSync(harness.runLog, 'utf8').trim().split(/\r?\n/);
    assert.equal(invocations.length, importCellIds.length);
    for (const cellId of importCellIds) {
      const line = invocations.find((candidate) => candidate.includes(`--cell ${cellId}`));
      assert.ok(line, `expected invocation for ${cellId}`);
      assert.match(line, new RegExp(`--evidence .*imports-${cellId}\\.json`));
    }
  } finally {
    harness.cleanup();
  }
});

test('run-cell.sh rejects unknown cells before Docker work', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const result = spawnSync('bash', [
      toBashPath(script),
      '--cell', 'does-not-exist',
      '--work-dir', toBashPath(harness.work),
      '--cosign', toBashPath(harness.cosign),
    ], {
      cwd: repoRoot,
      env: { ...process.env, PATH: `${harness.bin}${path.delimiter}${process.env.PATH}` },
      encoding: 'utf8',
    });
    assert.equal(result.status, 2);
    assert.match(result.stderr, /Unknown recovery matrix cell/);
    assert.throws(() => readFileSync(harness.dockerLog, 'utf8'), /ENOENT/);
  } finally {
    harness.cleanup();
  }
});

test('run-cell.sh rejects unsupported entry points before Docker work', { skip: !hasBash() }, () => {
  const harness = createHarness();
  try {
    const result = spawnSync('bash', [
      toBashPath(script),
      '--cell', 'c2',
      '--entry-point', 'windows',
      '--work-dir', toBashPath(harness.work),
      '--cosign', toBashPath(harness.cosign),
    ], {
      cwd: repoRoot,
      env: { ...process.env, PATH: `${harness.bin}${path.delimiter}${process.env.PATH}` },
      encoding: 'utf8',
    });
    assert.equal(result.status, 2);
    assert.match(result.stderr, /--entry-point must be bash or powershell/);
    assert.throws(() => readFileSync(harness.dockerLog, 'utf8'), /ENOENT/);
  } finally {
    harness.cleanup();
  }
});

test('run-cell.ps1 help documents the supported Linux host boundary', () => {
  const result = spawnSync('pwsh', ['-NoProfile', '-File', powershellScript, '-Help'], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  if (result.error?.code === 'ENOENT') return;
  assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
  assert.match(result.stdout, /Ubuntu LTS x64/);
  assert.match(result.stdout, /Windows hosts are unsupported/);
});

test('run-cell.ps1 invokes the Bash harness with PowerShell evidence identity on Linux', {
  skip: process.platform !== 'linux',
}, () => {
  const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', '$PSVersionTable.PSVersion.Major'], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  if (pwsh.error?.code === 'ENOENT' || pwsh.status !== 0) return;

  const harness = createHarness();
  try {
    const evidence = path.join(harness.work, 'powershell.json');
    const result = spawnSync('pwsh', [
      '-NoProfile',
      '-File',
      powershellScript,
      '-Cell',
      'remote-worker',
      '-WorkDir',
      toBashPath(harness.work),
      '-Evidence',
      toBashPath(evidence),
      '-Cosign',
      toBashPath(harness.cosign),
      '-Fault',
      'before-recover=exit 0',
      '-KeepWork',
    ], {
      cwd: repoRoot,
      env: { ...process.env, PATH: `${harness.bin}${path.delimiter}${process.env.PATH}` },
      encoding: 'utf8',
    });
    assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
    const args = readFileSync(harness.runLog, 'utf8').trim().split(/\r?\n/);
    assert.equal(args[0], toBashPath(script));
    assert.ok(args.includes('--entry-point'));
    assert.ok(args.includes('powershell'));
    assert.ok(args.includes('remote-worker'));
    assert.ok(args.includes(toBashPath(evidence)));
    assert.ok(args.includes('before-recover=exit 0'));
  } finally {
    harness.cleanup();
  }
});

test('run-cell.ps1 refuses live execution on Windows', { skip: process.platform !== 'win32' }, () => {
  const result = spawnSync('pwsh', ['-NoProfile', '-File', powershellScript, '-Cell', 'c2'], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  if (result.error?.code === 'ENOENT') return;
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Windows hosts are unsupported/);
});
