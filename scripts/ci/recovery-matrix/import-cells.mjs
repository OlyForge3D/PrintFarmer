// Live channel, identity and adversarial import cells for issue #3102. Every cell runs on the
// supported c2 shape and imports extra fixture bundles through the packaged
// `offline-bundle-import` operation after the prior release is activated. No cell activates a
// release it imported: the terminal outcome is `Imported`, proven by the `import-cells-verified`
// checkpoint after every assertion held.

const c2Shape = Object.freeze({
  topology: 'monolith',
  provider: 'postgres',
  databaseLayout: 'shared',
  databaseOwner: 'host',
  storageOwner: 'host',
  workers: 'managed',
});

export { importCellsVerifiedCheckpoint } from './evidence.mjs';

export const importCaseKinds = Object.freeze([
  'identity',
  'channel-round-trips',
  'adversarial',
  'replay-supersede',
]);

const defineImportCell = ({ id, kind }) => Object.freeze({
  id,
  scenario: 'import',
  cell: c2Shape,
  importCase: kind,
  expected: Object.freeze({ failClosed: false, outcome: 'Imported', reason: null }),
});

export const importCells = Object.freeze([
  defineImportCell({ id: 'import-identity', kind: 'identity' }),
  defineImportCell({ id: 'import-channel-round-trips', kind: 'channel-round-trips' }),
  defineImportCell({ id: 'import-adversarial', kind: 'adversarial' }),
  defineImportCell({ id: 'import-replay-supersede', kind: 'replay-supersede' }),
]);

export const importCellIds = Object.freeze(importCells.map((entry) => entry.id));

// The docs/OFFLINE_UPDATE_RECOVERY.md adversarial set. Each case must be refused before any
// mutation: the replay store, policy and loaded images are unchanged and no staging remains.
export const adversarialCases = Object.freeze([
  'missing-image',
  'missing-trust-approval',
  'unbound-trust-approval',
  'missing-config',
  'malicious-archive-symlink',
  'malicious-archive-traversal',
  'modified-bytes',
  'forged-promotion',
  'wrong-platform',
  'mixed-digests',
  'mixed-channels',
  'expired-trust',
  'revoked-trust',
  'invalid-signature-poisoning',
  'equal-sequence-substitution',
  'missing-replay-store',
  'rolled-back-replay-store',
]);
