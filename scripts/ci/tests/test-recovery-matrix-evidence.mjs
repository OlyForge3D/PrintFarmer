import assert from 'node:assert/strict';
import test from 'node:test';

import {
  evidenceKind,
  evidenceSchema,
  expectedCellOutcome,
  networkDenialMechanism,
  validateRecoveryEvidence,
} from '../recovery-matrix/evidence.mjs';

const identity = (tag, sequence) => ({
  tag,
  version: tag.replace(/^v/, ''),
  channel: 'insider',
  sourceCommit: 'a'.repeat(40),
  buildId: `build-${sequence}`,
  sequence,
});

function validRecord() {
  return {
    schema: evidenceSchema,
    kind: evidenceKind,
    run: {
      id: 'run-1',
      startedAt: '2026-09-26T10:00:00Z',
      finishedAt: '2026-09-26T10:20:00Z',
      harnessCommit: 'b'.repeat(40),
      entryPoint: 'bash',
    },
    host: {
      distribution: 'ubuntu',
      distributionVersion: '24.04',
      arch: 'x64',
      kernel: '6.8.0-45-generic',
    },
    cell: {
      topology: 'monolith',
      provider: 'postgres',
      databaseLayout: 'shared',
      databaseOwner: 'host',
      storageOwner: 'host',
      workers: 'managed',
    },
    identities: {
      source: identity('v0.2.3', 3),
      target: identity('v0.2.4', 4),
      prior: identity('v0.2.2', 2),
      bundleSha256: 'c'.repeat(64),
      signingRoot: 'fixture',
    },
    tools: {
      cli: '0.2.4',
      docker: '27.3.1',
      compose: '2.29.7',
      cosign: '2.4.1',
      node: '22.9.0',
      shell: 'bash 5.2.21',
    },
    networkDenial: {
      mechanism: networkDenialMechanism,
      egressSinkActive: true,
      attempts: [],
    },
    checkpoints: [
      { name: 'backup-verified', at: '2026-09-26T10:05:00Z', result: 'ok' },
      { name: 'fault-injected', at: '2026-09-26T10:10:00Z', result: 'ok' },
    ],
    outcome: {
      expected: 'RolledBack',
      actual: 'RolledBack',
      reason: null,
      exitCode: 0,
      journalPhase: 'RolledBack',
    },
    timings: { activationSeconds: 120, recoverySeconds: 240 },
    verdict: 'pass',
  };
}

const hasError = (errors, fragment) =>
  assert.ok(
    errors.some((error) => error.includes(fragment)),
    `expected an error containing "${fragment}", got ${JSON.stringify(errors)}`,
  );

test('a complete supported-cell record validates', () => {
  assert.deepEqual(validateRecoveryEvidence(validRecord()), []);
});

test('missing and unexpected fields are rejected', () => {
  const record = validRecord();
  delete record.identities.prior.buildId;
  record.tools.extra = 'x';
  delete record.timings;
  const errors = validateRecoveryEvidence(record);
  hasError(errors, 'identities.prior.buildId: missing field');
  hasError(errors, 'tools.extra: unexpected field');
  hasError(errors, 'timings: missing field');
});

test('non-Ubuntu-LTS or non-x64 hosts are rejected', () => {
  for (const [field, value, fragment] of [
    ['arch', 'arm64', 'host.arch'],
    ['distribution', 'windows', 'host.distribution'],
    ['distributionVersion', '24.10', 'host.distributionVersion'],
  ]) {
    const record = validRecord();
    record.host[field] = value;
    hasError(validateRecoveryEvidence(record), fragment);
  }
});

test('any outbound network attempt prevents a passing verdict', () => {
  const record = validRecord();
  record.networkDenial.attempts.push({
    at: '2026-09-26T10:07:00Z',
    destination: 'ghcr.io:443',
    protocol: 'tcp',
  });
  hasError(validateRecoveryEvidence(record), 'outbound network attempts');
  record.verdict = 'fail';
  assert.deepEqual(validateRecoveryEvidence(record), []);
});

test('an inactive egress sink or other denial mechanism is rejected', () => {
  const record = validRecord();
  record.networkDenial.egressSinkActive = false;
  record.networkDenial.mechanism = 'iptables';
  const errors = validateRecoveryEvidence(record);
  hasError(errors, 'networkDenial.egressSinkActive');
  hasError(errors, 'networkDenial.mechanism');
});

test('a pass requires actual == expected and no failed checkpoint', () => {
  const record = validRecord();
  record.outcome.actual = 'NeedsOperator';
  record.checkpoints[1].result = 'failed';
  const errors = validateRecoveryEvidence(record);
  hasError(errors, 'actual outcome differs');
  hasError(errors, 'failed checkpoint');
});

test('unsupported cells must expect their fail-closed outcome and reason', () => {
  const record = validRecord();
  record.cell.workers = 'remote';
  let errors = validateRecoveryEvidence(record);
  hasError(errors, 'outcome.expected: unsupported cell must expect Refused');
  hasError(errors, 'remote_worker_unsupported');

  record.outcome = {
    expected: 'Refused',
    actual: 'Refused',
    reason: 'remote_worker_unsupported',
    exitCode: 2,
    journalPhase: 'Refused',
  };
  assert.deepEqual(validateRecoveryEvidence(record), []);

  const supported = validRecord();
  supported.outcome.expected = 'Refused';
  supported.outcome.actual = 'Refused';
  errors = validateRecoveryEvidence(supported);
  hasError(errors, 'supported cell must not expect Refused');
});

test('expectedCellOutcome maps every fail-closed cell', () => {
  const base = validRecord().cell;
  assert.deepEqual(expectedCellOutcome(base), {
    failClosed: false,
    outcome: null,
    reason: null,
  });
  for (const [field, value, outcome, reason] of [
    ['databaseLayout', 'split', 'Refused', 'split_database_not_supported'],
    ['workers', 'remote', 'Refused', 'remote_worker_unsupported'],
    ['databaseOwner', 'external', 'NeedsOperator', 'database_externally_owned'],
    ['storageOwner', 'external', 'NeedsOperator', 'storage_externally_owned'],
  ]) {
    assert.deepEqual(expectedCellOutcome({ ...base, [field]: value }), {
      failClosed: true,
      outcome,
      reason,
    });
  }
  assert.equal(
    expectedCellOutcome({ ...base, databaseLayout: 'split', workers: 'remote' }).reason,
    'split_database_not_supported',
  );
});

test('unredacted secrets anywhere in the record are rejected', () => {
  const cases = [
    ['tools.cli', ['https://admin', 'hunter2@registry.local/v2'].join(':'), 'URL userinfo'],
    ['tools.cosign', '-----BEGIN PRIVATE KEY-----', 'PEM block'],
    ['tools.node', `ghp_${'A'.repeat(36)}`, 'GitHub token'],
    ['tools.shell', `eyJ${'a'.repeat(12)}.${'b'.repeat(12)}.${'c'.repeat(12)}`, 'JWT'],
    ['host.kernel', 'Password=s3cret', 'secret assignment'],
    ['run.id', 'api_key: abc123', 'secret assignment'],
  ];
  for (const [fieldPath, value, rule] of cases) {
    const record = validRecord();
    const [section, key] = fieldPath.split('.');
    record[section][key] = value;
    hasError(validateRecoveryEvidence(record), `${fieldPath}: contains unredacted ${rule}`);
  }
  const record = validRecord();
  record.networkDenial.attempts.push({
    at: '2026-09-26T10:07:00Z',
    destination: ['postgres://pf', 'pw@db', '5432'].join(':'),
    protocol: 'tcp',
  });
  record.verdict = 'fail';
  hasError(validateRecoveryEvidence(record), 'networkDenial.attempts[0].destination');
});

test('secret-bearing field names are rejected', () => {
  const record = validRecord();
  record.tools.dbPassword = 'redacted';
  hasError(validateRecoveryEvidence(record), 'secret-bearing field name');
});

test('schema, kind, enums and timestamps are enforced', () => {
  const record = validRecord();
  record.schema = 2;
  record.kind = 'other';
  record.cell.provider = 'sqlite';
  record.run.entryPoint = 'cmd';
  record.identities.signingRoot = 'release';
  record.run.finishedAt = '2026-09-26T09:00:00Z';
  record.checkpoints[0].at = 'yesterday';
  const errors = validateRecoveryEvidence(record);
  for (const fragment of [
    'schema: expected 1',
    'kind: expected',
    'cell.provider',
    'run.entryPoint',
    'identities.signingRoot',
    'run.finishedAt: must not precede',
    'checkpoints[0].at',
  ]) {
    hasError(errors, fragment);
  }
});
