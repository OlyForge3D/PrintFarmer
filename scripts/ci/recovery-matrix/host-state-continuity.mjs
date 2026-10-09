import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// Faults may garble any of these files on purpose, so an unparsable file reads as `null` (or an
// `{ unparsable }` journal entry) with its raw text kept for mutation comparison. Continuity then
// fails closed through the checksum/anchor validity flags instead of crashing the reader.
function tryParseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

export function readHostStateSnapshot(root) {
  const replayPath = join(root, 'host-update-replay.json');
  const anchorPath = join(root, 'replay-anchor.json');
  const journalPath = join(root, 'replay-anchor.journal');
  const rawReplay = existsSync(replayPath) ? readFileSync(replayPath, 'utf8') : '';
  const rawAnchor = existsSync(anchorPath) ? readFileSync(anchorPath, 'utf8') : '';
  const replay = rawReplay ? tryParseJson(rawReplay) : null;
  const anchor = rawAnchor ? tryParseJson(rawAnchor) : null;
  const journalEntries = existsSync(journalPath)
    ? readFileSync(journalPath, 'utf8').split(/\r?\n/).filter(Boolean).map((line) => tryParseJson(line) ?? { unparsable: line })
    : [];
  return {
    replay,
    anchor,
    journalEntries,
    rawReplay,
    rawAnchor,
    replayChecksumValid: replay ? validateReplayChecksum(replay) : false,
    anchorValid: replay && anchor ? validateAnchor(anchor, rawReplay, journalEntries) : false,
  };
}

export function readHostStateSnapshotFromBoundary(root, { exec }) {
  const script = [
    "import { readHostStateSnapshot } from './scripts/ci/recovery-matrix/host-state-continuity.mjs';",
    `process.stdout.write(JSON.stringify(readHostStateSnapshot(${JSON.stringify(root)})));`,
  ].join('\n');
  return JSON.parse(exec(['node', '--input-type=module', '-e', script]));
}

export function assertHostStateContinuity(before, after, { targetIdentity } = {}) {
  if (!after?.replayChecksumValid) {
    throw new Error('host_state_replay_checksum_invalid');
  }
  if (!after?.anchorValid) {
    throw new Error('host_state_anchor_invalid');
  }
  if ((after.replay?.Epoch ?? 0) < (before.replay?.Epoch ?? 0)) {
    throw new Error('host_state_epoch_regressed');
  }
  assertMapNonDecreasing('host_state_high_water', highWater(before.replay), highWater(after.replay));
  assertMapContains('host_state_identities', identities(before.replay), identities(after.replay));
  if (targetIdentity && !Object.hasOwn(identities(after.replay), targetIdentity)) {
    throw new Error(`host_state_target_admission_missing:${targetIdentity}`);
  }
}

function validateReplayChecksum(replay) {
  if (typeof replay.Checksum !== 'string') {
    return false;
  }
  const candidates = [
    {
      Version: replay.Version,
      Epoch: replay.Epoch,
      HighWater: Object.entries(replay.HighWaterByNamespace ?? {})
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([Namespace, value]) => ({ Namespace, Sequence: value.Sequence, Identity: value.Identity })),
      Identities: Object.entries(replay.Identities ?? {})
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([Identity, value]) => ({ Identity, Sequence: value.Sequence, Disposition: String(value.Disposition), CorrelationId: value.CorrelationId })),
    },
    {
      Version: replay.Version,
      Epoch: replay.Epoch,
      HighWater: replay.HighWater ?? [],
      Identities: replay.Identities ?? [],
    },
    {
      Version: replay.Version,
      Epoch: replay.Epoch,
      HighWaterByNamespace: replay.HighWaterByNamespace ?? {},
      Identities: replay.Identities ?? {},
    },
  ];
  return candidates.some((candidate) => sha256Json(candidate) === replay.Checksum);
}

function validateAnchor(anchor, rawReplay, journalEntries) {
  const stateHash = createHash('sha256').update(rawReplay).digest('hex');
  if (anchor.StateHash !== stateHash) {
    return false;
  }
  return journalEntries.some(entry =>
    entry.Version === anchor.Version
    && entry.Epoch === anchor.Epoch
    && entry.StateHash === anchor.StateHash
    && entry.Hash === anchor.Hash);
}

function highWater(replay) {
  if (!replay) return {};
  if (Array.isArray(replay.HighWater)) {
    return Object.fromEntries(replay.HighWater.map((entry, index) => [String(entry.Namespace ?? index), Number(entry.Sequence ?? entry.HighWater ?? 0)]));
  }
  return Object.fromEntries(Object.entries(replay.HighWaterByNamespace ?? {}).map(([key, value]) => [key, Number(value)]));
}

function identities(replay) {
  if (!replay) return {};
  if (Array.isArray(replay.Identities)) {
    return Object.fromEntries(replay.Identities.map((entry, index) => [String(entry.Id ?? entry.Identity ?? index), entry]));
  }
  return replay.Identities ?? {};
}

function assertMapNonDecreasing(label, before, after) {
  for (const [key, value] of Object.entries(before)) {
    if (!Object.hasOwn(after, key)) {
      throw new Error(`${label}_missing:${key}`);
    }
    if (Number(after[key]) < Number(value)) {
      throw new Error(`${label}_regressed:${key}`);
    }
  }
}

function assertMapContains(label, before, after) {
  for (const key of Object.keys(before)) {
    if (!Object.hasOwn(after, key)) {
      throw new Error(`${label}_missing:${key}`);
    }
  }
}

function sha256Json(value) {
  return `sha256:${createHash('sha256').update(JSON.stringify(value), 'utf8').digest('hex')}`;
}
