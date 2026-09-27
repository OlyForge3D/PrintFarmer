import { createHash } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

export function readHostStateSnapshot(root) {
  const replayPath = join(root, 'host-update-replay.json');
  const anchorPath = join(root, 'replay-anchor.json');
  const journalPath = join(root, 'replay-anchor.journal');
  const replay = existsSync(replayPath) ? JSON.parse(readFileSync(replayPath, 'utf8')) : null;
  const anchor = existsSync(anchorPath) ? JSON.parse(readFileSync(anchorPath, 'utf8')) : null;
  const journalEntries = existsSync(journalPath)
    ? readFileSync(journalPath, 'utf8').split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line))
    : [];
  const rawReplay = replay ? readFileSync(replayPath, 'utf8') : '';
  return {
    replay,
    anchor,
    journalEntries,
    rawReplay,
    replayChecksumValid: replay ? validateReplayChecksum(replay) : false,
    anchorValid: replay && anchor ? validateAnchor(anchor, rawReplay) : false,
  };
}

export function assertHostStateContinuity(before, after, { targetVersion } = {}) {
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
  if (targetVersion && !after.rawReplay.includes(targetVersion)) {
    throw new Error(`host_state_target_admission_missing:${targetVersion}`);
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

function validateAnchor(anchor, rawReplay) {
  const stateHash = createHash('sha256').update(rawReplay).digest('hex');
  if (anchor.StateHash !== stateHash) {
    return false;
  }
  const expected = createHash('sha256')
    .update(`${anchor.Version}|${anchor.Epoch}|${anchor.PreviousHash ?? ''}|${anchor.StateHash}`, 'utf8')
    .digest('hex');
  return anchor.Hash === expected;
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
