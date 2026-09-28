import assert from 'node:assert/strict';
import test from 'node:test';

import { resolveCell } from '../recovery-matrix/cells.mjs';
import {
  assertActiveServiceDigests,
  assertExpectedNeedsOperator,
  assertExpectedRefusal,
  assertNoMutation,
} from '../recovery-matrix/runtime-assertions.mjs';

const priorImages = {
  monolith: { indexDigest: 'sha256:mono' },
  api: { indexDigest: 'sha256:api' },
  frontend: { indexDigest: 'sha256:frontend' },
  'slicer-host': { indexDigest: 'sha256:slicer-host' },
  'printer-discovery': { indexDigest: 'sha256:printer-discovery' },
  'orcaslicer-worker': { indexDigest: 'sha256:orcaslicer-worker' },
};

test('post-recovery digest checks every active split service and never probes monolith', () => {
  const cell = resolveCell('split-postgres').cell;
  const probed = [];
  const checked = assertActiveServiceDigests({
    cell,
    priorImages,
    inspectDigest: (composeServiceName) => {
      probed.push(composeServiceName);
      return priorImages[composeServiceName].indexDigest;
    },
  });

  assert.deepEqual(checked.map((item) => item.serviceId), [
    'api',
    'frontend',
    'slicer-host',
    'printer-discovery',
    'orcaslicer-worker',
  ]);
  assert.ok(!probed.includes('printfarmer'));
});

test('post-recovery digest checks split no-worker without querying monolith or worker', () => {
  const cell = resolveCell('split-postgres-no-worker').cell;
  const byComposeService = {
    api: 'sha256:api',
    frontend: 'sha256:frontend',
    'slicer-host': 'sha256:slicer-host',
    'printer-discovery': 'sha256:printer-discovery',
  };
  const probed = [];
  assertActiveServiceDigests({
    cell,
    priorImages,
    inspectDigest: (composeServiceName) => {
      probed.push(composeServiceName);
      return byComposeService[composeServiceName];
    },
  });
  assert.deepEqual(probed.sort(), ['api', 'frontend', 'printer-discovery', 'slicer-host']);
});

test('refusal and needs-operator helpers reject success exits with matching text', () => {
  assert.throws(
    () => assertExpectedRefusal(
      { exitCode: 0, actual: 'Refused', reason: 'remote_worker_unsupported' },
      { outcome: 'Refused', reason: 'remote_worker_unsupported' },
    ),
    /refusal_exit_code_mismatch/,
  );
  assert.doesNotThrow(() => assertExpectedRefusal(
    { exitCode: 6, actual: 'Refused', reason: 'remote_worker_unsupported' },
    { outcome: 'Refused', reason: 'remote_worker_unsupported' },
  ));

  assert.throws(
    () => assertExpectedNeedsOperator(
      { exitCode: 0, stdout: 'plan.kind: NeedsOperator\nreason: database_externally_owned' },
      'database_externally_owned',
    ),
    /needs_operator_exit_code_mismatch/,
  );
  assert.doesNotThrow(() => assertExpectedNeedsOperator(
    { exitCode: 10, stdout: 'plan.kind: NeedsOperator\nreason: database_externally_owned' },
    'database_externally_owned',
  ));
});

test('refusal no-mutation assertion blocks mutated service digests and state', () => {
  const before = {
    serviceDigests: { monolith: 'sha256:prior' },
    migrationHeads: ['202609260001_Init'],
    volumeHashes: { 'app-data': 'aaa' },
    hostState: { installed: { version: '0.1.0' } },
  };
  assert.doesNotThrow(() => assertNoMutation('remote-worker', before, structuredClone(before)));

  const after = structuredClone(before);
  after.serviceDigests.monolith = 'sha256:target';
  assert.throws(() => assertNoMutation('remote-worker', before, after), /serviceDigests-changed/);
});
