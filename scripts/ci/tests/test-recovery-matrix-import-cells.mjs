import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { closeSync, mkdtempSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

import { readOfflineBundleEntries, tarHeader } from '../offline-update-bundle.mjs';
import { resolveCellList } from '../recovery-matrix/cells.mjs';
import { validateRecoveryEvidence } from '../recovery-matrix/evidence.mjs';
import {
  adversarialCases,
  importCellIds,
  importCells,
  importCellsVerifiedCheckpoint,
} from '../recovery-matrix/import-cells.mjs';
import { readImageArchiveEntries } from '../offline-bundle-images.mjs';
import {
  addForeignPlatformBlobs,
  adversarialRunners,
  anchorEntryHash,
  assertAnchorTransition,
  assertImportedIdentity,
  assertRefusedImport,
  ImportScenarioError,
  replayAdmissionDrift,
  rewriteOfflineBundle,
} from '../recovery-matrix/import-scenarios.mjs';

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const pad = (size) => Buffer.alloc((512 - (size % 512)) % 512);

function buildTinyBundle(path, members) {
  const index = {
    files: members.map(([name, bytes]) => ({ name, size: bytes.length, sha256: sha256(bytes) })),
  };
  const head = Buffer.from(`${JSON.stringify(index)}\n`);
  const parts = [tarHeader({ name: 'offline-bundle.json', size: head.length }), head, pad(head.length)];
  for (const [name, bytes] of members) parts.push(tarHeader({ name, size: bytes.length }), bytes, pad(bytes.length));
  parts.push(Buffer.alloc(1024));
  writeFileSync(path, Buffer.concat(parts));
}

function readBundle(path) {
  const fd = openSync(path, 'r');
  try {
    const bytes = readFileSync(fd);
    const entries = readOfflineBundleEntries(fd, bytes.length);
    return entries.map((entry) => ({
      name: entry.name,
      bytes: bytes.subarray(entry.offset, entry.offset + entry.size),
    }));
  } finally {
    closeSync(fd);
  }
}

test('import cells are a runnable group on the supported c2 shape', () => {
  assert.deepEqual(importCellIds, [
    'import-identity',
    'import-channel-round-trips',
    'import-adversarial',
    'import-replay-supersede',
  ]);
  assert.deepEqual(resolveCellList('imports').map((cell) => cell.id ?? cell), importCellIds);
  for (const cell of importCells) {
    assert.equal(cell.scenario, 'import');
    assert.deepEqual(cell.expected, { failClosed: false, outcome: 'Imported', reason: null });
    assert.equal(cell.cell.provider, 'postgres');
  }
});

test('every documented adversarial case has a runner', () => {
  assert.deepEqual(Object.keys(adversarialRunners).sort(), [...adversarialCases].sort());
});

function importedRecord(checkpoints) {
  return {
    checkpoints,
    outcome: { expected: 'Imported', actual: 'Imported', reason: null, exitCode: 0, journalPhase: null },
  };
}

test('evidence may expect Imported only after the verified checkpoint', () => {
  const importErrors = (record) =>
    validateRecoveryEvidence(record).filter((error) => error.includes('Imported'));
  const missing = importErrors(importedRecord([{ name: 'backup-verified', at: '2026-09-26T10:05:00Z', result: 'ok' }]));
  assert.ok(missing.some((error) => error.includes(importCellsVerifiedCheckpoint)), JSON.stringify(missing));
  const present = importErrors(
    importedRecord([{ name: importCellsVerifiedCheckpoint, at: '2026-09-26T10:05:00Z', result: 'ok' }]),
  );
  assert.deepEqual(present, []);
  const withReason = importedRecord([{ name: importCellsVerifiedCheckpoint, at: '2026-09-26T10:05:00Z', result: 'ok' }]);
  withReason.outcome.reason = 'x';
  assert.ok(importErrors(withReason).some((error) => error.includes('must not carry a reason')));
});

test('rewriteOfflineBundle drops, replaces and inserts members with a consistent index', () => {
  const dir = mkdtempSync(join(tmpdir(), 'import-cells-'));
  try {
    const source = join(dir, 'source.tar');
    buildTinyBundle(source, [
      ['a.txt', Buffer.from('alpha')],
      ['b.txt', Buffer.from('bravo')],
    ]);
    const output = join(dir, 'output.tar');
    rewriteOfflineBundle({
      source,
      output,
      edit: (bundle) => {
        assert.deepEqual(bundle.names(), ['a.txt', 'b.txt']);
        assert.equal(bundle.read('a.txt').toString(), 'alpha');
        bundle.drop('a.txt');
        bundle.replace('b.txt', Buffer.from('bravo-2'));
        bundle.insertRaw(tarHeader({ name: 'c.txt', size: 3 }), Buffer.from('cee'));
      },
    });
    const entries = readBundle(output);
    assert.deepEqual(entries.map((entry) => entry.name), ['offline-bundle.json', 'c.txt', 'b.txt']);
    const index = JSON.parse(entries[0].bytes.toString());
    assert.deepEqual(index.files, [{ name: 'b.txt', size: 7, sha256: sha256(Buffer.from('bravo-2')) }]);
    assert.equal(entries[2].bytes.toString(), 'bravo-2');

    const stale = join(dir, 'stale.tar');
    rewriteOfflineBundle({
      source,
      output: stale,
      edit: (bundle) => bundle.replace('a.txt', Buffer.from('tampered'), { updateIndex: false }),
    });
    const staleIndex = JSON.parse(readBundle(stale)[0].bytes.toString());
    assert.equal(staleIndex.files[0].sha256, sha256(Buffer.from('alpha')));
    assert.throws(
      () => rewriteOfflineBundle({ source, output: join(dir, 'x.tar'), edit: (bundle) => bundle.read('missing') }),
      (error) => error instanceof ImportScenarioError && error.reason === 'bundle_rewrite_member_missing',
    );
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

const built = {
  release: { version: '1.0.0-insider.10', channel: 'insider', sourceCommit: 'a'.repeat(40), sequence: 10 },
  manifestDigest: `sha256:${'d'.repeat(64)}`,
};

function imported(overrides = {}) {
  return {
    outcome: 'imported',
    installable: true,
    replay: { admitted: true, sequence: 10 },
    release: { ...built.release },
    verifiedDigests: { manifest: 'd'.repeat(64) },
    ...overrides,
  };
}

const reasonOf = (fn) => {
  try {
    fn();
  } catch (error) {
    assert.ok(error instanceof ImportScenarioError);
    return error.reason;
  }
  return null;
};

test('assertImportedIdentity requires the builder original identity', () => {
  assert.equal(reasonOf(() => assertImportedIdentity('ok', imported(), built)), null);
  assert.equal(
    reasonOf(() => assertImportedIdentity('x', imported({ outcome: 'refused' }), built)),
    'import_not_imported',
  );
  assert.equal(
    reasonOf(() =>
      assertImportedIdentity('x', imported({ release: { ...built.release, sourceCommit: 'b'.repeat(40) } }), built),
    ),
    'imported_identity_mismatch',
  );
  assert.equal(
    reasonOf(() => assertImportedIdentity('x', imported({ verifiedDigests: { manifest: 'e'.repeat(64) } }), built)),
    'imported_manifest_digest_mismatch',
  );
  assert.equal(
    reasonOf(() => assertImportedIdentity('x', imported({ replay: { admitted: false } }), built)),
    'import_not_admitted',
  );
});

test('assertRefusedImport requires a refused record bound to the expected reason', () => {
  const refused = { outcome: 'refused', installable: false, reason: 'replay_superseded', replay: { admitted: false } };
  assert.equal(assertRefusedImport('ok', { exitCode: 1, record: refused }, { reason: 'replay_superseded' }), 'replay_superseded');
  assert.equal(assertRefusedImport('re', { exitCode: 1, record: refused }, { reason: /^replay_super/ }), 'replay_superseded');
  assert.equal(reasonOf(() => assertRefusedImport('x', { exitCode: 0, record: refused }, { reason: 'x' })), 'import_not_refused');
  assert.equal(
    reasonOf(() => assertRefusedImport('x', { exitCode: 1, record: { ...refused, loadedImages: ['img'] } }, { reason: 'replay' })),
    'import_refusal_loaded_images',
  );
  assert.equal(
    reasonOf(() => assertRefusedImport('x', { exitCode: 1, record: refused }, { reason: 'channel_mismatch' })),
    'import_refusal_reason',
  );
  assert.equal(
    reasonOf(() => assertRefusedImport('x', { exitCode: 1, record: refused }, { reason: /channel/ })),
    'import_refusal_reason',
  );
  assert.equal(reasonOf(() => assertRefusedImport('x', { exitCode: 1, record: refused })), 'import_refusal_reason_unbound');
  assert.equal(
    reasonOf(() =>
      assertRefusedImport('cli', { exitCode: 2, record: null, stdout: '', stderr: 'config missing' }, { reason: 'config' }),
    ),
    'import_refusal_no_record',
  );
});

test('failed import cells may record a refusal without the verified checkpoint', () => {
  const record = importedRecord([{ name: 'backup-verified', at: '2026-09-26T10:05:00Z', result: 'ok' }]);
  record.outcome.actual = 'Refused';
  record.outcome.reason = 'import_refusal_mutated_state';
  record.outcome.exitCode = 1;
  assert.deepEqual(validateRecoveryEvidence(record).filter((error) => error.includes('Imported')), []);
});

test('replayAdmissionDrift allows only durable authenticated refusals', () => {
  const before = {
    Version: 1,
    HighWaterByNamespace: { insider: { Sequence: 42, Identity: 'i42' } },
    Identities: { i42: { Sequence: 42, Disposition: 'Imported', CorrelationId: 'c' } },
  };
  const withRefusal = structuredClone(before);
  withRefusal.Identities.i41 = { Sequence: 41, Disposition: 'Superseded', CorrelationId: 'd' };
  assert.deepEqual(replayAdmissionDrift(before, withRefusal), []);
  const camel = { version: 1, highWaterByNamespace: before.HighWaterByNamespace, identities: withRefusal.Identities };
  assert.deepEqual(replayAdmissionDrift(before, camel), []);

  const admitted = structuredClone(before);
  admitted.Identities.i43 = { Sequence: 43, Disposition: 'Imported', CorrelationId: 'e' };
  assert.deepEqual(replayAdmissionDrift(before, admitted), ['identity-added:i43:Imported']);
  const raised = structuredClone(before);
  raised.HighWaterByNamespace.insider = { Sequence: 43, Identity: 'i43' };
  assert.deepEqual(replayAdmissionDrift(before, raised), ['high-water:insider']);
  const rewritten = structuredClone(before);
  rewritten.Identities.i42.Disposition = 'Superseded';
  assert.deepEqual(replayAdmissionDrift(before, rewritten), ['identity-changed:i42']);
  assert.deepEqual(replayAdmissionDrift(before, null), ['replay-store-presence']);
  assert.deepEqual(replayAdmissionDrift(null, null), []);
});

test('replayAdmissionDrift with an expectation requires exactly the refused identity as Rejected', () => {
  const before = {
    HighWaterByNamespace: { insider: { Sequence: 42, Identity: 'i42' } },
    Identities: { i42: { Sequence: 42, Disposition: 'Imported', CorrelationId: 'c' } },
  };
  const rejected = structuredClone(before);
  rejected.Identities.i40 = { Sequence: 40, Disposition: 'Rejected', CorrelationId: 'd' };
  assert.deepEqual(replayAdmissionDrift(before, rejected, { added: 1, sequence: 40 }), []);
  assert.deepEqual(replayAdmissionDrift(before, before, { added: 0, sequence: 40 }), []);
  assert.deepEqual(replayAdmissionDrift(before, rejected, { added: 0, sequence: 40 }), ['identities-added:1!=0']);
  assert.deepEqual(replayAdmissionDrift(before, before, { added: 1, sequence: 40 }), ['identities-added:0!=1']);
  assert.deepEqual(replayAdmissionDrift(before, rejected, { added: 1, sequence: 39 }), ['identity-added-sequence:i40']);
  const superseded = structuredClone(before);
  superseded.Identities.i40 = { Sequence: 40, Disposition: 'Superseded', CorrelationId: 'd' };
  assert.deepEqual(replayAdmissionDrift(before, superseded, { added: 1, sequence: 40 }), ['identity-added:i40:Superseded']);
  const two = structuredClone(rejected);
  two.Identities.i39 = { Sequence: 40, Disposition: 'Rejected', CorrelationId: 'e' };
  assert.deepEqual(replayAdmissionDrift(before, two, { added: 1, sequence: 40 }), ['identities-added:2!=1']);
});

test('replayAdmissionDrift rejects untracked replay-store fields and epoch drift', () => {
  const before = {
    Version: 1,
    Epoch: 7,
    Checksum: 'a',
    HighWaterByNamespace: {},
    Identities: {},
  };
  const rejected = structuredClone(before);
  rejected.Epoch = 8;
  rejected.Checksum = 'b';
  rejected.Identities.i40 = { Sequence: 40, Disposition: 'Rejected', CorrelationId: 'd' };
  assert.deepEqual(replayAdmissionDrift(before, rejected, { added: 1, sequence: 40 }), []);
  assert.deepEqual(replayAdmissionDrift(before, { ...rejected, Version: 2 }, { added: 1, sequence: 40 }), ['field:version']);
  assert.deepEqual(replayAdmissionDrift(before, { ...rejected, Extra: true }, { added: 1, sequence: 40 }), ['field:extra']);
  assert.deepEqual(replayAdmissionDrift(before, { ...rejected, Epoch: 9 }, { added: 1, sequence: 40 }), ['epoch:7->9']);
  assert.deepEqual(replayAdmissionDrift(before, { ...before, Epoch: 8 }, { added: 0, sequence: 40 }), ['epoch:7->8']);
  assert.deepEqual(replayAdmissionDrift(before, { ...before, Checksum: 'z' }, { added: 0, sequence: 40 }), ['checksum']);
});

function anchorEntry(epoch, previous, stateHash) {
  return { Version: 1, Epoch: epoch, PreviousHash: previous, StateHash: stateHash, Hash: anchorEntryHash(epoch, previous, stateHash) };
}
const line = (entry) => `${JSON.stringify(entry)}\n`;
const snap = (entry) => JSON.stringify({ Version: 1, Epoch: entry.Epoch, StateHash: entry.StateHash, Hash: entry.Hash });

test('assertAnchorTransition accepts only one chained append authenticating the replay file', () => {
  const e0 = anchorEntry(0, '', 'a'.repeat(64));
  const e1 = anchorEntry(1, e0.Hash, 'b'.repeat(64));
  const e2 = anchorEntry(2, e1.Hash, 'c'.repeat(64));
  const before = { journal: line(e0) + line(e1), snapshot: snap(e1), replayHash: e1.StateHash };
  const recorded = { journal: before.journal + line(e2), snapshot: snap(e2), replayHash: e2.StateHash };
  const check = (after) => reasonOf(() => assertAnchorTransition('x', before, after, { mode: 'recorded' }));
  assert.equal(assertAnchorTransition('x', before, recorded, { mode: 'recorded' }), 1);
  assert.equal(assertAnchorTransition('x', before, { ...before }, { mode: 'recorded' }), 0);
  assert.equal(check({ ...before, replayHash: 'd'.repeat(64) }), 'replay_anchor_state_changed');
  assert.equal(check({ ...before, snapshot: snap(e0) }), 'replay_anchor_snapshot_changed');
  assert.equal(check({ ...recorded, journal: line(e0) + line(e2) }), 'replay_anchor_journal_rewritten');
  const e3 = anchorEntry(3, e2.Hash, 'e'.repeat(64));
  assert.equal(check({ ...recorded, journal: recorded.journal + line(e3), snapshot: snap(e3), replayHash: e3.StateHash }), 'replay_anchor_multiple_entries');
  const forged = { ...e2, Hash: 'f'.repeat(64) };
  assert.equal(check({ ...recorded, journal: before.journal + line(forged) }), 'replay_anchor_entry_unchained');
  const unlinked = anchorEntry(2, e0.Hash, 'c'.repeat(64));
  assert.equal(check({ ...recorded, journal: before.journal + line(unlinked) }), 'replay_anchor_entry_unchained');
  assert.equal(check({ ...recorded, replayHash: 'd'.repeat(64) }), 'replay_anchor_state_unauthenticated');
  assert.equal(check({ ...recorded, snapshot: snap(e1) }), 'replay_anchor_snapshot_not_head');
  assert.equal(check({ ...recorded, journal: recorded.journal.slice(0, -1) }), 'replay_anchor_journal_truncated');
});

test('assertAnchorTransition heals only a rolled-back snapshot to the unchanged journal head', () => {
  const e0 = anchorEntry(0, '', 'a'.repeat(64));
  const e1 = anchorEntry(1, e0.Hash, 'b'.repeat(64));
  const journal = line(e0) + line(e1);
  const before = { journal, snapshot: snap(e0), replayHash: e0.StateHash };
  const healed = { journal, snapshot: snap(e1), replayHash: e0.StateHash };
  const check = (b, a) => reasonOf(() => assertAnchorTransition('x', b, a, { mode: 'healed' }));
  assert.equal(check(before, healed), null);
  assert.equal(check(before, { ...healed, snapshot: snap(e0) }), 'replay_anchor_not_healed_to_head');
  assert.equal(check({ ...before, snapshot: snap(e1) }, healed), 'replay_anchor_not_rolled_back');
  assert.equal(check(before, { ...healed, journal: line(e0) }), 'replay_anchor_journal_changed');
  assert.equal(check(before, { ...healed, replayHash: e1.StateHash }), 'replay_anchor_state_changed');
});

test('addForeignPlatformBlobs keeps a canonical, correctly hashed OCI archive', () => {
  const dir = mkdtempSync(join(tmpdir(), 'import-platform-'));
  try {
    const layout = Buffer.from('{"imageLayoutVersion":"1.0.0"}');
    const index = Buffer.from('{"schemaVersion":2}');
    const blob = Buffer.from('root');
    const header = (name, size) => {
      const h = tarHeader({ name, size });
      return h;
    };
    const original = Buffer.concat([
      ...[['oci-layout', layout], ['index.json', index], [`blobs/sha256/${sha256(blob)}`, blob]]
        .flatMap(([name, bytes]) => [header(name, bytes.length), bytes, pad(bytes.length)]),
      Buffer.alloc(1024),
    ]);
    const rewritten = addForeignPlatformBlobs(original);
    const path = join(dir, 'image.oci.tar');
    writeFileSync(path, rewritten);
    const fd = openSync(path, 'r');
    try {
      const entries = readImageArchiveEntries(fd, 0, rewritten.length);
      assert.equal(entries.length, 5);
      assert.deepEqual(entries.slice(0, 2).map((entry) => entry.name), ['oci-layout', 'index.json']);
      const blobs = entries.slice(2);
      assert.deepEqual(blobs.map((entry) => entry.name), [...blobs.map((entry) => entry.name)].sort());
      for (const entry of blobs) {
        assert.equal(entry.name, `blobs/sha256/${sha256(rewritten.subarray(entry.offset, entry.offset + entry.size))}`);
      }
      assert.ok(blobs.some((entry) => rewritten.subarray(entry.offset, entry.offset + entry.size).includes('arm64')));
    } finally {
      closeSync(fd);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});