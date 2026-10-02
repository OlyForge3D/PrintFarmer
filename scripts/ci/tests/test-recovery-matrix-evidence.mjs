import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  evidenceKind,
  evidenceSchema,
  expectedCellOutcome,
  networkDenialMechanism,
  validateEvidenceParity,
  validateMatrixRun,
  validatePublishedBundleVerification,
  validateRecoveryEvidence,
  verificationKind,
} from '../recovery-matrix/evidence.mjs';
import { cellIds, cellsById } from '../recovery-matrix/cells.mjs';

const identity = (tag, sequence) => ({
  tag,
  version: tag.replace(/^v/, ''),
  channel: 'insider',
  sourceCommit: 'a'.repeat(40),
  buildId: `build-${sequence}`,
  sequence,
});

function validRecord(cell = {
  topology: 'monolith',
  provider: 'postgres',
  databaseLayout: 'shared',
  databaseOwner: 'host',
  storageOwner: 'host',
  workers: 'managed',
}) {
  const expectation = expectedCellOutcome(cell);
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
    cell,
    identities: {
      source: identity('v0.2.3', 3),
      target: identity('v0.2.4', 4),
      prior: identity('v0.2.2', 2),
      bundleSha256: 'c'.repeat(64),
      signingRoot: 'fixture-ephemeral',
      signingRootFingerprint: 'e'.repeat(64),
      schemaDelta: 'identical',
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
    outcome: expectation.failClosed
      ? {
          expected: expectation.outcome,
          expectedReason: expectation.reason,
          actual: expectation.outcome,
          reason: expectation.reason,
          exitCode: expectation.outcome === 'Refused' ? 6 : 10,
          journalPhase: expectation.outcome,
        }
      : {
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

test('parity comparison requires every expected cell and a passing pair', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'recovery-evidence-parity-'));
  const comparator = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '../recovery-matrix/compare-evidence.mjs',
  );
  try {
    for (const entryPoint of ['bash', 'powershell']) {
      for (const cellId of cellIds) {
        const record = validRecord(cellsById[cellId].cell);
        record.run.entryPoint = entryPoint;
        writeFileSync(
          path.join(root, `evidence-parity-all-${entryPoint}-${cellId}.json`),
          JSON.stringify(record),
        );
      }
    }

    let result = spawnSync(process.execPath, [comparator, 'all', root], { encoding: 'utf8' });
    assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
    assert.match(result.stdout, /all 9 all cells/);

    const mismatchedOutcome = validRecord(cellsById.c2.cell);
    mismatchedOutcome.run.entryPoint = 'powershell';
    mismatchedOutcome.outcome.reason = 'different-reason';
    writeFileSync(
      path.join(root, 'evidence-parity-all-powershell-c2.json'),
      JSON.stringify(mismatchedOutcome),
    );
    result = spawnSync(process.execPath, [comparator, 'all', root], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /outcome: Bash and PowerShell outcomes and reasons must be identical/);

    rmSync(path.join(root, 'evidence-parity-all-powershell-split-database.json'));
    result = spawnSync(process.execPath, [comparator, 'all', root], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /expected exactly one 'split-database' evidence record, found 0/);

    writeFileSync(path.join(root, 'evidence-parity-all-powershell-unexpected.json'), '{}');
    result = spawnSync(process.execPath, [comparator, 'all', root], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /unexpected evidence record for cell 'unexpected'/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('parity comparison rejects failed evidence even when both entry points agree', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'recovery-evidence-parity-failed-'));
  const comparator = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '../recovery-matrix/compare-evidence.mjs',
  );
  try {
    for (const entryPoint of ['bash', 'powershell']) {
      const record = validRecord();
      record.run.entryPoint = entryPoint;
      record.verdict = 'fail';
      for (const cellId of cellIds) {
        writeFileSync(
          path.join(root, `evidence-parity-all-${entryPoint}-${cellId}.json`),
          JSON.stringify(record),
        );
      }
    }

    const result = spawnSync(process.execPath, [comparator, 'all', root], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /both entry points must have a passing cell verdict/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
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
    source: '172.30.1.20:49000',
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
    expectedReason: 'remote_worker_unsupported',
    actual: 'Refused',
    reason: 'remote_worker_unsupported',
    exitCode: 6,
    journalPhase: 'Refused',
  };
  assert.deepEqual(validateRecoveryEvidence(record), []);

  const supported = validRecord();
  supported.outcome.expected = 'Refused';
  supported.outcome.actual = 'Refused';
  errors = validateRecoveryEvidence(supported);
  hasError(errors, 'supported cell must not expect Refused');
});

test('fail-closed setup failures keep observed actual and reason separate from expected reason', () => {
  const record = validRecord();
  record.cell.workers = 'remote';
  record.outcome = {
    expected: 'Refused',
    expectedReason: 'remote_worker_unsupported',
    actual: 'RecoveryRequired',
    reason: 'activate-prior:HostUpdateVerificationTimeoutException',
    exitCode: 1,
    journalPhase: 'ActivatePrior',
  };
  record.verdict = 'fail';
  assert.deepEqual(validateRecoveryEvidence(record), []);
});

test('fail-closed cell with an observed setup failure records the observed actual, not the expected refusal', () => {
  const record = validRecord();
  record.cell.storageOwner = 'external';
  record.checkpoints[1] = {
    name: 'prior-activation',
    at: '2026-09-26T10:10:00Z',
    result: 'failed',
  };
  record.outcome = {
    expected: 'NeedsOperator',
    expectedReason: 'storage_externally_owned',
    actual: 'RecoveryRequired',
    reason: 'setup-failed:prior-activation',
    exitCode: 1,
    journalPhase: 'not-started',
  };
  record.verdict = 'fail';
  assert.deepEqual(validateRecoveryEvidence(record), []);
});

test('passing Refused and NeedsOperator evidence requires exact CLI exit codes', () => {
  const refused = validRecord();
  refused.cell.workers = 'remote';
  refused.outcome = {
    expected: 'Refused',
    expectedReason: 'remote_worker_unsupported',
    actual: 'Refused',
    reason: 'remote_worker_unsupported',
    exitCode: 0,
    journalPhase: 'Refused',
  };
  hasError(validateRecoveryEvidence(refused), 'Refused pass records');

  refused.outcome.exitCode = 6;
  assert.deepEqual(validateRecoveryEvidence(refused), []);

  const needsOperator = validRecord();
  needsOperator.cell.databaseOwner = 'external';
  needsOperator.outcome = {
    expected: 'NeedsOperator',
    expectedReason: 'database_externally_owned',
    actual: 'NeedsOperator',
    reason: 'database_externally_owned',
    exitCode: 0,
    journalPhase: 'RecoveryPreview',
  };
  hasError(validateRecoveryEvidence(needsOperator), 'NeedsOperator pass records');

  needsOperator.outcome.exitCode = 10;
  assert.deepEqual(validateRecoveryEvidence(needsOperator), []);
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
    ['tools.cosign', ['-----BEGIN', 'PRIVATE KEY-----'].join(' '), 'PEM block'],
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

function validVerification() {
  const cell = validRecord();
  return {
    schema: evidenceSchema,
    kind: verificationKind,
    run: cell.run,
    host: cell.host,
    identities: {
      target: identity('v0.2.3-insider.4', 4),
      bundleSha256: 'd'.repeat(64),
      signingRoot: 'published-insider',
    },
    tools: cell.tools,
    networkDenial: {
      mechanism: networkDenialMechanism,
      egressSinkActive: true,
      attempts: [],
    },
    verification: {
      signatureVerified: true,
      imported: false,
      activated: false,
      hostModified: false,
    },
    verdict: 'pass',
  };
}

test('PowerShell is a live-cell entry point but not a published-bundle entry point', () => {
  const record = validRecord();
  record.run.entryPoint = 'powershell';
  assert.deepEqual(validateRecoveryEvidence(record), []);
  const verification = validVerification();
  verification.run = { ...verification.run, entryPoint: 'powershell' };
  hasError(validatePublishedBundleVerification(verification), 'published-bundle verification must use bash');
});

test('paired Bash and PowerShell evidence requires identical cell outcomes and reasons', () => {
  const bash = validRecord();
  const powershell = structuredClone(bash);
  powershell.run = { ...powershell.run, id: 'run-powershell', entryPoint: 'powershell' };
  assert.deepEqual(validateEvidenceParity(bash, powershell), []);

  powershell.outcome = { ...powershell.outcome, reason: 'different_reason' };
  hasError(
    validateEvidenceParity(bash, powershell),
    'outcome: Bash and PowerShell outcomes and reasons must be identical',
  );

  powershell.outcome = { ...bash.outcome };
  powershell.cell = { ...powershell.cell, provider: 'sqlserver' };
  hasError(
    validateEvidenceParity(bash, powershell),
    'cell: Bash and PowerShell evidence must describe the same cell',
  );
});

test('matrix cells must use the fixture signing root', () => {
  const record = validRecord();
  record.identities.signingRoot = 'published-insider';
  hasError(
    validateRecoveryEvidence(record),
    'identities.signingRoot: matrix cells must use the fixture-ephemeral root',
  );
});

test('legacy schema-1 fixture records remain valid without ephemeral root fields', () => {
  const legacy = validRecord();
  legacy.identities = {
    source: identity('v0.2.3', 3),
    target: identity('v0.2.4', 4),
    prior: identity('v0.2.2', 2),
    bundleSha256: 'c'.repeat(64),
    signingRoot: 'fixture',
  };
  assert.deepEqual(validateRecoveryEvidence(legacy), []);
});

test('ephemeral fixture cells record the root fingerprint and prior schema delta', () => {
  const noFingerprint = validRecord();
  delete noFingerprint.identities.signingRootFingerprint;
  hasError(validateRecoveryEvidence(noFingerprint), 'identities.signingRootFingerprint: missing field');
  const badFingerprint = validRecord();
  badFingerprint.identities.signingRootFingerprint = 'not-a-digest';
  hasError(validateRecoveryEvidence(badFingerprint), 'identities.signingRootFingerprint');
  const badDelta = validRecord();
  badDelta.identities.schemaDelta = 'unknown';
  hasError(validateRecoveryEvidence(badDelta), 'identities.schemaDelta: must be one of');
});

test('a cell root is never accepted as published-bundle verification evidence', () => {
  const record = validRecord();
  record.identities.signingRoot = 'published-insider';
  hasError(
    validateRecoveryEvidence(record),
    'identities.signingRoot: matrix cells must use the fixture-ephemeral root',
  );
});

test('the published-bundle verification record is read-only and insider-signed', () => {
  assert.deepEqual(validatePublishedBundleVerification(validVerification()), []);

  for (const field of ['imported', 'activated', 'hostModified']) {
    const record = validVerification();
    record.verification[field] = true;
    hasError(
      validatePublishedBundleVerification(record),
      `verification.${field}: the published-bundle check must be read-only`,
    );
  }

  const fixtureRoot = validVerification();
  fixtureRoot.identities.signingRoot = 'fixture-ephemeral';
  hasError(validatePublishedBundleVerification(fixtureRoot), 'identities.signingRoot');

  const stable = validVerification();
  stable.identities.target = identity('v0.2.3', 3);
  stable.identities.target.channel = 'stable';
  hasError(validatePublishedBundleVerification(stable), 'identities.target.channel');

  const unverified = validVerification();
  unverified.verification.signatureVerified = false;
  hasError(validatePublishedBundleVerification(unverified), 'unverified published bundle');
  unverified.verdict = 'fail';
  assert.deepEqual(validatePublishedBundleVerification(unverified), []);

  const recoveryFields = validVerification();
  recoveryFields.outcome = validRecord().outcome;
  hasError(validatePublishedBundleVerification(recoveryFields), 'outcome: unexpected field');
});

test('a matrix run needs exactly one verification and unique cells', () => {
  const unsupported = validRecord();
  unsupported.cell.workers = 'remote';
  unsupported.outcome = {
    expected: 'Refused',
    expectedReason: 'remote_worker_unsupported',
    actual: 'Refused',
    reason: 'remote_worker_unsupported',
    exitCode: 6,
    journalPhase: 'Refused',
  };
  assert.deepEqual(
    validateMatrixRun([validRecord(), unsupported, validVerification()]),
    [],
  );

  hasError(validateMatrixRun([validRecord()]), 'exactly one published-bundle verification');
  hasError(
    validateMatrixRun([validRecord(), validVerification(), validVerification()]),
    'found 2',
  );
  hasError(validateMatrixRun([validVerification()]), 'at least one matrix cell');
  hasError(
    validateMatrixRun([validRecord(), validRecord(), validVerification()]),
    'records[1].cell: duplicate matrix cell',
  );
  const otherRun = validVerification();
  otherRun.run = { ...otherRun.run, id: 'run-2' };
  hasError(validateMatrixRun([validRecord(), otherRun]), 'share one run.id');
  hasError(validateMatrixRun([]), 'non-empty array');

  const bad = validRecord();
  bad.host.arch = 'arm64';
  hasError(validateMatrixRun([bad, validVerification()]), 'records[0].host.arch');
});

test('release identities are validated field by field for every role', () => {
  for (const role of ['source', 'target', 'prior']) {
    for (const [field, value, fragment] of [
      ['tag', 42, `identities.${role}.tag: expected release tag`],
      ['tag', 'v9.9.9', `identities.${role}.tag: must equal v<version>`],
      ['version', 'latest', `identities.${role}.version: expected release version`],
      ['channel', 'beta', `identities.${role}.channel: must be one of stable, insider`],
      ['sourceCommit', 'not-a-sha', `identities.${role}.sourceCommit: expected 40-character`],
      ['buildId', '', `identities.${role}.buildId: expected non-empty string`],
      ['sequence', 'bogus', `identities.${role}.sequence: expected non-negative integer`],
      ['sequence', -1, `identities.${role}.sequence: expected non-negative integer`],
    ]) {
      const record = validRecord();
      record.identities[role][field] = value;
      hasError(validateRecoveryEvidence(record), fragment);
    }
  }
});

test('bundleSha256 may be null only for failed runs before target bundle assembly', () => {
  const failedBeforeAssembly = validRecord();
  failedBeforeAssembly.verdict = 'fail';
  failedBeforeAssembly.identities.bundleSha256 = null;
  failedBeforeAssembly.checkpoints = [
    { name: 'trusted-root-created', at: '2026-09-26T10:01:00Z', result: 'ok' },
    { name: 'e2e-complete', at: '2026-09-26T10:02:00Z', result: 'failed' },
  ];
  failedBeforeAssembly.outcome.actual = 'RecoveryRequired';
  assert.deepEqual(validateRecoveryEvidence(failedBeforeAssembly), []);

  const passed = validRecord();
  passed.identities.bundleSha256 = null;
  hasError(validateRecoveryEvidence(passed), 'bundleSha256: null is allowed only');

  const failedAfterAssembly = validRecord();
  failedAfterAssembly.verdict = 'fail';
  failedAfterAssembly.identities.bundleSha256 = null;
  failedAfterAssembly.checkpoints.push({ name: 'offline-bundle-assembled', at: '2026-09-26T10:03:00Z', result: 'ok' });
  hasError(validateRecoveryEvidence(failedAfterAssembly), 'bundleSha256: null is allowed only');
});

test('URL userinfo is rejected with or without a password; safe URLs pass', () => {
  const record = validRecord();
  record.tools.cli = ['https://generic-secret', 'registry.local/v2'].join('@');
  hasError(validateRecoveryEvidence(record), 'tools.cli: contains unredacted URL userinfo');

  for (const safe of [
    'https://ghcr.io/v2/olyforge3d/printfarmer',
    `ghcr.io/olyforge3d/printfarmer@sha256:${'e'.repeat(64)}`,
    `https://ghcr.io/olyforge3d/printfarmer@sha256:${'e'.repeat(64)}`,
    'maintainer ops@example.com',
  ]) {
    const clean = validRecord();
    clean.tools.cli = safe;
    assert.deepEqual(validateRecoveryEvidence(clean), [], safe);
  }
});

test('supported recovery cells may expect rollback without an injected fault', () => {
  const rolledBack = validRecord();
  rolledBack.checkpoints = [
    { name: 'backup-verified', at: '2026-09-26T10:05:00Z', result: 'ok' },
  ];
  assert.deepEqual(validateRecoveryEvidence(rolledBack), []);

  const activated = validRecord();
  activated.checkpoints = rolledBack.checkpoints;
  activated.outcome = { ...activated.outcome, expected: 'Activated', actual: 'Activated' };
  assert.deepEqual(validateRecoveryEvidence(activated), []);

  const operator = validRecord();
  operator.checkpoints = rolledBack.checkpoints;
  operator.outcome = {
    expected: 'NeedsOperator',
    actual: 'NeedsOperator',
    reason: 'restore_uncertain',
    exitCode: 10,
    journalPhase: 'NeedsOperator',
  };
  hasError(validateRecoveryEvidence(operator), 'only after a successful fault-injected');

  operator.checkpoints = [
    ...rolledBack.checkpoints,
    { name: 'fault-injected', at: '2026-09-26T10:06:00Z', result: 'ok' },
  ];
  assert.deepEqual(validateRecoveryEvidence(operator), []);

  const noReason = validRecord();
  noReason.outcome = { ...operator.outcome, reason: null };
  hasError(validateRecoveryEvidence(noReason), 'NeedsOperator requires a stable reason');

  const borrowedReason = validRecord();
  borrowedReason.outcome = { ...operator.outcome, reason: 'database_externally_owned' };
  hasError(
    validateRecoveryEvidence(borrowedReason),
    'supported cell must not report a fail-closed reason',
  );

  const failedFault = validRecord();
  failedFault.outcome = { ...operator.outcome };
  failedFault.checkpoints[1].result = 'failed';
  failedFault.verdict = 'fail';
  hasError(validateRecoveryEvidence(failedFault), 'only after a successful fault-injected');
});
