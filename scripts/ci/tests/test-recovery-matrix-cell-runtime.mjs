import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import {
  baseEvidence,
  createCheckpoints,
  defaultCell,
  lastJournalPhase,
  networkDenial,
  parseCosignGitVersion,
  writeValidatedEvidence,
} from '../recovery-matrix/cell-runtime.mjs';
import { evidenceKind, evidenceSchema, networkDenialMechanism } from '../recovery-matrix/evidence.mjs';

const scratchRoot = path.resolve('.recovery-matrix-test-work');

const identity = (version, sequence) => ({
  tag: `v${version}`,
  version,
  channel: 'insider',
  sourceCommit: 'a'.repeat(40),
  buildId: String(sequence),
  sequence,
});

function validEvidence() {
  const checkpoints = createCheckpoints(() => new Date('2026-09-26T18:00:00.000Z'));
  checkpoints.ok('fault-injected');
  return baseEvidence({
    run: {
      id: 'c2-test',
      startedAt: '2026-09-26T18:00:00.000Z',
      finishedAt: '2026-09-26T18:10:00.000Z',
      harnessCommit: 'b'.repeat(40),
      entryPoint: 'bash',
    },
    host: {
      distribution: 'ubuntu',
      distributionVersion: '24.04',
      arch: 'x64',
      kernel: 'test',
    },
    identities: {
      source: identity('1.0.0-insider.0', 100000000000),
      target: identity('1.0.0-insider.2', 100000000002),
      prior: identity('1.0.0-insider.1', 100000000001),
      bundleSha256: 'c'.repeat(64),
      signingRootFingerprint: 'd'.repeat(64),
      schemaDelta: 'identical',
    },
    tools: {
      cli: 'fixture',
      docker: '29.0.0',
      compose: '2.40.0',
      cosign: '3.0.6',
      node: '24.0.0',
      shell: 'bash 5.2',
    },
    checkpoints: checkpoints.checkpoints,
    outcome: {
      expected: 'RolledBack',
      actual: 'RolledBack',
      reason: null,
      exitCode: 0,
      journalPhase: 'completed',
    },
    timings: { activationSeconds: 1, recoverySeconds: 2 },
    verdict: 'pass',
  });
}

test('networkDenial emits the required AC3 mechanism', () => {
  assert.deepEqual(networkDenial(), {
    mechanism: networkDenialMechanism,
    egressSinkActive: true,
    attempts: [],
  });
});

test('baseEvidence emits the recovery evidence contract fields', () => {
  const evidence = validEvidence();
  assert.equal(evidence.schema, evidenceSchema);
  assert.equal(evidence.kind, evidenceKind);
  assert.deepEqual(evidence.cell, defaultCell);
  assert.equal(evidence.identities.signingRoot, 'fixture-ephemeral');
});

test('parseCosignGitVersion records only the concise cosign gitVersion', () => {
  assert.equal(parseCosignGitVersion('{"gitVersion":"v3.0.6","gitCommit":"abc"}'), 'v3.0.6');
  assert.equal(parseCosignGitVersion('GitVersion: v3.0.6\nGitCommit: abc'), 'v3.0.6');
});

test('writeValidatedEvidence rejects invalid records and writes valid ones', () => {
  const scratch = path.join(scratchRoot, `evidence-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const output = path.join(scratch, 'evidence.json');
    writeValidatedEvidence(output, validEvidence());
    assert.equal(JSON.parse(readFileSync(output, 'utf8')).verdict, 'pass');

    const bad = validEvidence();
    bad.networkDenial.attempts.push({
      at: '2026-09-26T18:01:00.000Z',
      destination: 'example.invalid:443',
      protocol: 'tcp',
    });
    assert.throws(() => writeValidatedEvidence(path.join(scratch, 'bad.json'), bad), /outbound network attempts/);

    const placeholderBundle = validEvidence();
    placeholderBundle.identities.bundleSha256 = '0'.repeat(64);
    assert.throws(
      () => writeValidatedEvidence(path.join(scratch, 'placeholder.json'), placeholderBundle),
      /bundleSha256: must be the real target bundle SHA-256/,
    );

    const mismatchedSource = validEvidence();
    mismatchedSource.identities.source = { ...mismatchedSource.identities.prior, sourceCommit: 'e'.repeat(40) };
    assert.throws(
      () => writeValidatedEvidence(path.join(scratch, 'source-mismatch.json'), mismatchedSource),
      /source: must match identities\.prior/,
    );
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test('lastJournalPhase reads the actual hashed journal payload phase', () => {
  const scratch = path.join(scratchRoot, `journal-${process.pid}-${Date.now()}`);
  mkdirSync(scratch, { recursive: true });
  try {
    const journal = path.join(scratch, 'journal.ndjson');
    writeFileSync(journal, `${JSON.stringify({
      PreviousHash: '',
      Payload: JSON.stringify({ Phase: 'recovery-confirm:after', State: 'Completed' }),
      Hash: 'ignored',
      Activity: { Phase: 'untrusted' },
    })}\n`);
    assert.equal(lastJournalPhase(journal), 'recovery-confirm:after');
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
