import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, chmodSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

import { cellIds } from '../recovery-matrix/cells.mjs';

const repoRoot = path.resolve(import.meta.dirname, '../../..');
const script = path.join(repoRoot, 'scripts/ci/recovery-matrix/run-cell.sh');

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
  const root = mkdtempSync(path.join(tmpdir(), 'recovery-entrypoint-'));
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
  printf '%s\n' "${cellIds.join(' ')}"
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
  return { root, bin, work, runLog, dockerLog, cosign: path.join(bin, 'cosign') };
}

test('run-cell.sh expands all cells with executable cosign and distinct evidence files', { skip: !hasBash() }, () => {
  const harness = createHarness();
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
});

test('run-cell.sh rejects unknown cells before Docker work', { skip: !hasBash() }, () => {
  const harness = createHarness();
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
});
