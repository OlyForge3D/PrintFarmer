// Import-cell scenario runner for issue #3102. run-cell.mjs supplies the live host boundary
// (packaged import/activation, host shell, policy edits, restarts, database dump/restore); this
// module owns the channel, identity, adversarial and replay assertions so they stay unit-testable.
import { closeSync, fstatSync, openSync, readFileSync, readSync, rmSync, writeFileSync, writeSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';

import { readOfflineBundleEntries, tarHeader } from '../offline-update-bundle.mjs';
import { imageTarHeader } from '../offline-bundle-images.mjs';
import { adversarialCases, importCellsVerifiedCheckpoint } from './import-cells.mjs';

const block = 512;
const chunk = 1 << 20;

export class ImportScenarioError extends Error {
  constructor(reason, detail) {
    super(`${reason}${detail ? `: ${detail}` : ''}`);
    this.reason = reason;
  }
}

function fail(reason, detail) {
  throw new ImportScenarioError(reason, detail);
}

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function readRange(fd, offset, size) {
  const buffer = Buffer.alloc(size);
  let done = 0;
  while (done < size) {
    const read = readSync(fd, buffer, done, size - done, offset + done);
    if (read <= 0) fail('bundle_rewrite_truncated');
    done += read;
  }
  return buffer;
}

function writeAll(fd, buffer) {
  let done = 0;
  while (done < buffer.length) done += writeSync(fd, buffer, done, buffer.length - done);
}

function padding(size) {
  return Buffer.alloc((block - (size % block)) % block);
}

// Rewrites an assembled offline bundle so a case can present a hostile archive to the importer.
// `edit` receives the parsed (unsigned) index and helpers to drop or replace members, keeping the
// index consistent unless the case deliberately wants a stale index, and to insert raw tar
// entries (links, traversal names) that a well-formed bundle never contains.
export function rewriteOfflineBundle({ source, output, edit }) {
  const fd = openSync(source, 'r');
  try {
    const entries = readOfflineBundleEntries(fd, fstatSync(fd).size);
    const [indexEntry, ...rest] = entries;
    const index = JSON.parse(readRange(fd, indexEntry.offset, indexEntry.size).toString('utf8'));
    const members = rest.map((entry) => ({ name: entry.name, size: entry.size, offset: entry.offset }));
    const raw = [];
    const find = (name) => {
      const member = members.find((candidate) => candidate.name === name);
      if (!member) fail('bundle_rewrite_member_missing', name);
      return member;
    };
    const indexFile = (name) => index.files.find((file) => file.name === name);
    const context = {
      index,
      names: () => members.map((member) => member.name),
      read: (name) => {
        const member = find(name);
        return member.bytes ?? readRange(fd, member.offset, member.size);
      },
      replace: (name, bytes, { updateIndex = true } = {}) => {
        const member = find(name);
        member.bytes = Buffer.from(bytes);
        member.size = member.bytes.length;
        if (updateIndex) {
          const file = indexFile(name);
          if (!file) fail('bundle_rewrite_index_entry_missing', name);
          file.size = member.size;
          file.sha256 = sha256(member.bytes);
        }
      },
      drop: (name, { updateIndex = true } = {}) => {
        members.splice(members.indexOf(find(name)), 1);
        if (updateIndex) index.files = index.files.filter((file) => file.name !== name);
      },
      insertRaw: (header, data = Buffer.alloc(0)) => {
        raw.push(Buffer.concat([header, data, padding(data.length)]));
      },
    };
    edit(context);
    const out = openSync(output, 'wx', 0o644);
    try {
      const head = Buffer.from(`${JSON.stringify(index, undefined, 2)}\n`);
      writeAll(out, tarHeader({ name: indexEntry.name, size: head.length }));
      writeAll(out, head);
      writeAll(out, padding(head.length));
      for (const entry of raw) writeAll(out, entry);
      for (const member of members) {
        writeAll(out, tarHeader({ name: member.name, size: member.size }));
        if (member.bytes) {
          writeAll(out, member.bytes);
        } else {
          for (let done = 0; done < member.size; done += chunk) {
            writeAll(out, readRange(fd, member.offset + done, Math.min(chunk, member.size - done)));
          }
        }
        writeAll(out, padding(member.size));
      }
      writeAll(out, Buffer.alloc(block * 2));
    } finally {
      closeSync(out);
    }
    return output;
  } finally {
    closeSync(fd);
  }
}

export function normalizeDigest(value) {
  return String(value ?? '').replace(/^sha256:/, '');
}

// The imported identity must be the builder's original identity: nothing the bundle, a branch or
// an alias says may substitute another release.
export function assertImportedIdentity(label, record, built) {
  if (record?.outcome !== 'imported') fail('import_not_imported', `${label}:${record?.outcome}:${record?.reason}`);
  if (record.installable !== true || record.replay?.admitted !== true) fail('import_not_admitted', label);
  const release = record.release ?? {};
  for (const key of ['version', 'channel', 'sourceCommit', 'buildId', 'sequence', 'tag', 'sourceBranch']) {
    if (key in release || ['version', 'channel', 'sourceCommit'].includes(key)) {
      if (String(release[key]) !== String(built.release[key])) {
        fail('imported_identity_mismatch', `${label}:${key}:${release[key]}!=${built.release[key]}`);
      }
    }
  }
  if (normalizeDigest(record.verifiedDigests?.manifest) !== normalizeDigest(built.manifestDigest)) {
    fail('imported_manifest_digest_mismatch', label);
  }
  if (record.replay.sequence !== undefined && String(record.replay.sequence) !== String(built.release.sequence)) {
    fail('imported_replay_sequence_mismatch', label);
  }
}

// Every refusal must leave a refused decision record whose reason is the one the case exists to
// prove; a nonzero exit for any other cause (a crashed tool, a different guard) is not evidence.
export function assertRefusedImport(label, result, { reason } = {}) {
  if (result.exitCode === 0) fail('import_not_refused', label);
  if (!(typeof reason === 'string' && reason) && !(reason instanceof RegExp)) fail('import_refusal_reason_unbound', label);
  const record = result.record;
  if (!record) fail('import_refusal_no_record', `${label}:exit ${result.exitCode}`);
  if (record.outcome !== 'refused') fail('import_refusal_outcome', `${label}:${record.outcome}`);
  if (record.installable !== false || record.replay?.admitted === true) fail('import_refusal_admitted', label);
  if ((record.loadedImages ?? []).length !== 0) fail('import_refusal_loaded_images', label);
  const actual = String(record.reason ?? '');
  if (reason instanceof RegExp ? !reason.test(actual) : !actual.includes(reason)) {
    fail('import_refusal_reason', `${label}:expected ${reason}:got ${record.reason}`);
  }
  return actual;
}

const replayFiles = ['host-update-replay.json', 'replay-anchor.json', 'replay-anchor.journal'];

function assertSameState(label, before, after, { exempt = [] } = {}) {
  const drift = Object.keys({ ...before, ...after })
    .filter((key) => before[key] !== after[key])
    .filter((key) => !exempt.includes(key));
  if (drift.length > 0) fail('import_refusal_mutated_state', `${label}:${drift.join(',')}`);
}

const field = (object, name) => object?.[name] ?? object?.[name[0].toLowerCase() + name.slice(1)];

const anchorVersion = 1;

export function anchorEntryHash(epoch, previous, stateHash) {
  return sha256(Buffer.from(`${anchorVersion}|${epoch}|${previous}|${stateHash}`));
}

function parseJournal(label, text) {
  if (text === null || text === undefined) return [];
  if (!text.endsWith('\n')) fail('replay_anchor_journal_truncated', label);
  return text.split('\n').filter(Boolean).map((line) => {
    try {
      return JSON.parse(line);
    } catch {
      return fail('replay_anchor_journal_corrupt', label);
    }
  });
}

function parseSnapshot(label, text) {
  if (text === null || text === undefined) return null;
  try {
    return JSON.parse(text);
  } catch {
    return fail('replay_anchor_snapshot_corrupt', label);
  }
}

const snapshotOf = (entry) => ({ Version: anchorVersion, Epoch: entry.Epoch, StateHash: entry.StateHash, Hash: entry.Hash });
const sameSnapshot = (snapshot, entry) => JSON.stringify(snapshot && snapshotOf(snapshot)) === JSON.stringify(snapshotOf(entry));

// The replay anchor is an append-only hash-chained journal plus a snapshot cache of its head; the
// head's StateHash authenticates host-update-replay.json. A refusal may touch it in exactly two
// ways, and nothing else counts as an unchanged anchor:
//  - 'recorded': an authenticated replay refusal appends at most one chained entry (none when
//    the identity was already decided), the snapshot is that new head, and the head authenticates
//    the replay file now on disk. Returns the number of appended entries.
//  - 'healed': a rolled-back snapshot is rewritten to the unchanged journal head; the journal and
//    the replay file are untouched.
export function assertAnchorTransition(label, before, after, { mode }) {
  const journalBefore = parseJournal(label, before.journal);
  const journalAfter = parseJournal(label, after.journal);
  if (journalBefore.length === 0) fail('replay_anchor_not_provisioned', label);
  const headBefore = journalBefore.at(-1);
  const snapshotBefore = parseSnapshot(label, before.snapshot);
  const snapshotAfter = parseSnapshot(label, after.snapshot);
  if (mode === 'healed') {
    if (after.journal !== before.journal) fail('replay_anchor_journal_changed', label);
    if (after.replayHash !== before.replayHash) fail('replay_anchor_state_changed', label);
    if (!snapshotBefore || sameSnapshot(snapshotBefore, headBefore) || !(Number(snapshotBefore.Epoch) < Number(headBefore.Epoch))) {
      fail('replay_anchor_not_rolled_back', label);
    }
    if (!sameSnapshot(snapshotAfter, headBefore)) fail('replay_anchor_not_healed_to_head', label);
    return 0;
  }
  if (mode !== 'recorded') fail('replay_anchor_mode_unknown', `${label}:${mode}`);
  if (!String(after.journal ?? '').startsWith(before.journal)) fail('replay_anchor_journal_rewritten', label);
  const appended = journalAfter.slice(journalBefore.length);
  if (appended.length === 0) {
    if (after.snapshot !== before.snapshot) fail('replay_anchor_snapshot_changed', label);
    if (after.replayHash !== before.replayHash) fail('replay_anchor_state_changed', label);
    return 0;
  }
  if (appended.length !== 1) fail('replay_anchor_multiple_entries', `${label}:${appended.length}`);
  const [entry] = appended;
  if (entry.Version !== anchorVersion || !(Number(entry.Epoch) > Number(headBefore.Epoch)) ||
    entry.PreviousHash !== headBefore.Hash || entry.Hash !== anchorEntryHash(entry.Epoch, entry.PreviousHash, entry.StateHash)) {
    fail('replay_anchor_entry_unchained', label);
  }
  if (entry.StateHash !== after.replayHash) fail('replay_anchor_state_unauthenticated', label);
  if (!sameSnapshot(snapshotAfter, entry)) fail('replay_anchor_snapshot_not_head', label);
  return 1;
}

// An authenticated replay refusal durably records the refused identity (so it stays refused
// across restarts and restores). That record must not change what is admitted: every channel
// high-water mark and every earlier identity decision is unchanged, and only Rejected or
// Superseded identities are added. With `expect`, exactly `expect.added` identities are added,
// each the refused candidate's sequence recorded as Rejected (the only disposition a refusal
// persists for a new identity).
export function replayAdmissionDrift(before, after, expect) {
  if (!before || !after) return before === after ? [] : ['replay-store-presence'];
  const drift = [];
  const hwmBefore = field(before, 'HighWaterByNamespace') ?? {};
  const hwmAfter = field(after, 'HighWaterByNamespace') ?? {};
  for (const ns of new Set([...Object.keys(hwmBefore), ...Object.keys(hwmAfter)])) {
    if (JSON.stringify(hwmBefore[ns]) !== JSON.stringify(hwmAfter[ns])) drift.push(`high-water:${ns}`);
  }
  const idsBefore = field(before, 'Identities') ?? {};
  const idsAfter = field(after, 'Identities') ?? {};
  for (const [identity, record] of Object.entries(idsBefore)) {
    if (JSON.stringify(record) !== JSON.stringify(idsAfter[identity])) drift.push(`identity-changed:${identity}`);
  }
  const added = Object.entries(idsAfter).filter(([identity]) => !(identity in idsBefore));
  for (const [identity, record] of added) {
    const disposition = String(field(record, 'Disposition'));
    if (expect) {
      if (disposition !== 'Rejected') drift.push(`identity-added:${identity}:${disposition}`);
      if (String(field(record, 'Sequence')) !== String(expect.sequence)) drift.push(`identity-added-sequence:${identity}`);
    } else if (disposition !== 'Rejected' && disposition !== 'Superseded') {
      drift.push(`identity-added:${identity}:${disposition}`);
    }
  }
  if (expect && added.length !== expect.added) drift.push(`identities-added:${added.length}!=${expect.added}`);
  return drift;
}

const authenticatedReplayRefusal = (reason) => /replay_(rejected|superseded)/.test(String(reason));

function shortReason(reason) {
  return String(reason).replace(/\s+/g, '_').replace(/[^A-Za-z0-9_.:\-]/g, '').slice(0, 80);
}

export function runImportScenario(ctx) {
  const { cellSpec, checkpoints } = ctx;
  const runners = {
    identity: runIdentity,
    'channel-round-trips': runChannelRoundTrips,
    adversarial: runAdversarial,
    'replay-supersede': runReplaySupersede,
  };
  const runner = runners[cellSpec.importCase];
  if (!runner) fail('unknown_import_case', cellSpec.importCase);
  const helpers = createHelpers(ctx);
  try {
    runner(ctx, helpers);
  } finally {
    helpers.cleanup();
  }
  checkpoints.ok(importCellsVerifiedCheckpoint);
}

function createHelpers(ctx) {
  const { checkpoints } = ctx;
  let policyRevision = 0;
  const built = new Map();
  const bundles = [];
  const release = (version, { channel = version.includes('-insider.') ? 'insider' : 'stable', seed = 'main', ...rest } = {}) => {
    const key = `${version}:${channel}:${seed}:${rest.sigstore ? 'alt' : ''}:${rest.signAs ?? ''}`;
    if (!built.has(key)) built.set(key, ctx.newRelease({ version, channel, seed, ...rest }));
    return built.get(key);
  };
  const bundle = (rel, { trustedRootPath } = {}) => {
    const path = ctx.assembleBundle({ built: rel, trustedRootPath, label: `${rel.release.version}-${bundles.length}` });
    bundles.push(path);
    return path;
  };
  const setPolicy = (channel) => {
    policyRevision += 1;
    ctx.writePolicy({ channel, revision: policyRevision });
    checkpoints.ok(`policy-channel:${channel}@${policyRevision}`);
  };
  const imported = (label, rel, options = {}) => {
    const result = ctx.importBundle({ built: rel, bundle: options.bundle ?? bundle(rel), label, ...options });
    assertImportedIdentity(label, result.record, rel);
    checkpoints.ok(`imported:${label}:${rel.release.channel}:${rel.release.version}`);
    return result;
  };
  const refused = (label, rel, options = {}) => {
    const before = ctx.stateHashes();
    const replayBefore = ctx.replayState();
    const anchorBefore = ctx.replayAnchor();
    const result = ctx.importBundle({ built: rel, bundle: options.bundle ?? bundle(rel), label, ...options });
    const reason = assertRefusedImport(label, result, { reason: options.reason });
    if (ctx.stagingExists(label)) fail('import_refusal_left_staging', label);
    const after = ctx.stateHashes();
    if (authenticatedReplayRefusal(reason)) {
      assertSameState(label, before, after, { exempt: replayFiles });
      const appended = assertAnchorTransition(label, anchorBefore, ctx.replayAnchor(), { mode: 'recorded' });
      const drift = replayAdmissionDrift(replayBefore, ctx.replayState(), { added: appended, sequence: rel.release.sequence });
      if (drift.length > 0) fail('import_refusal_changed_admission', `${label}:${drift.join(',')}`);
    } else if (options.anchorHealedTo) {
      // A rolled-back anchor snapshot is a stale cache of the append-only journal; detecting it
      // rewrites the snapshot to the unchanged journal head, which restores, never advances, admission.
      if (!/host_update_replay_state_rollback/.test(reason)) fail('import_refusal_heal_reason', `${label}:${reason}`);
      assertSameState(label, before, after, { exempt: ['replay-anchor.json'] });
      assertAnchorTransition(label, anchorBefore, ctx.replayAnchor(), { mode: 'healed' });
      if (after['replay-anchor.json'] !== options.anchorHealedTo) fail('replay_anchor_not_healed_to_advanced', label);
    } else {
      assertSameState(label, before, after);
    }
    checkpoints.ok(`refused-before-mutation:${label}:${shortReason(reason)}`);
    return result;
  };
  return {
    release,
    bundle,
    setPolicy,
    imported,
    refused,
    cleanup: () => {
      for (const path of bundles) rmSync(path, { force: true });
    },
  };
}

// Stable and insider imports keep the builder's identity; a fresh host trusts a root only through
// an operator approval bound to its exact bytes, never through a root the bundle brings itself.
function runIdentity(ctx, h) {
  const insider = h.release('1.0.0-insider.10');
  const attacker = h.release('1.0.0-insider.11', { sigstore: 'attacker' });
  h.refused('self-enrolled-root', attacker, {
    bundle: h.bundle(attacker, { trustedRootPath: attacker.trustedRootPath }),
    reason: reasons.signature,
  });
  h.refused('self-enrolled-root-approval', attacker, {
    bundle: h.bundle(attacker, { trustedRootPath: attacker.trustedRootPath }),
    approval: attacker.approvalPath,
    reason: reasons.unboundApproval,
  });
  const insiderBundle = h.bundle(insider);
  h.refused('fresh-host-without-approval', insider, { bundle: insiderBundle, approval: join(ctx.runRoot, 'absent-approval.json'), reason: reasons.unreadableApproval });
  h.refused('fresh-host-unbound-approval', insider, { bundle: insiderBundle, approval: attacker.approvalPath, reason: reasons.unboundApproval });
  h.imported('insider-original-identity', insider, { bundle: insiderBundle });
  h.setPolicy('stable');
  h.imported('stable-original-identity', h.release('1.0.0'));
  h.setPolicy('insider');
}

// Operator policy edits switch channels in both directions. Each channel keeps its own replay
// high-water mark, a lower sequence on the same channel is an unsupported downgrade, and neither
// a moved branch nor a channel alias can change what an admitted version means.
function runChannelRoundTrips(ctx, h) {
  const trip = [
    ['stable', '1.0.1'],
    ['insider', '1.0.2-insider.1'],
    ['stable', '1.0.2'],
    ['insider', '1.0.3-insider.1'],
    ['stable', '1.0.3'],
    ['insider', '1.0.4-insider.1'],
  ];
  const labels = ['stable-to-insider-to-stable', 'insider-to-stable-to-insider'];
  trip.forEach(([channel, version], index) => {
    h.setPolicy(channel);
    if (index === 0) {
      h.refused('switch-refused-without-policy-edit', h.release('1.0.1-insider.5'), { reason: 'channel_mismatch_policy' });
    }
    h.imported(`${labels[index < 3 ? 0 : 1]}:${index % 3}`, h.release(version));
  });
  h.refused('insider-bundle-as-stable-alias', h.release('1.0.5-insider.1'), { channel: 'stable', reason: /channel insider does not match the expected stable/ });
  h.setPolicy('stable');
  h.refused('unsupported-downgrade-stable', h.release('1.0.0'), { reason: 'replay_rejected' });
  h.refused('moved-branch-same-version', h.release('1.0.3', { seed: 'moved-branch' }), { reason: 'replay_rejected' });
  h.refused('deleted-alias-reimport', h.release('1.0.2'), { reason: 'replay_superseded' });
  h.setPolicy('insider');
  h.refused('unsupported-downgrade-insider', h.release('1.0.3-insider.1'), { reason: 'replay_superseded' });
  h.refused('moved-branch-insider', h.release('1.0.4-insider.1', { seed: 'moved-branch' }), { reason: 'replay_rejected' });
}

const reasons = {
  signature: /signature verification failed for update-manifest\.json/,
  unboundApproval: /does not bind the supplied trusted root/,
  unreadableApproval: /approval record is unreadable/,
};

function readNestedImageArchive(bytes) {
  const members = [];
  for (let offset = 0; offset + block <= bytes.length;) {
    const header = bytes.subarray(offset, offset + block);
    if (header.every((byte) => byte === 0)) break;
    const nameField = header.subarray(0, 100);
    const name = nameField.subarray(0, nameField.indexOf(0) === -1 ? 100 : nameField.indexOf(0)).toString('latin1');
    const size = Number.parseInt(header.subarray(124, 136).toString('latin1').replace(/\0.*$/s, '').trim(), 8);
    if (!Number.isSafeInteger(size)) fail('nested_image_archive_malformed', name);
    const start = offset + block;
    members.push({ name, data: bytes.subarray(start, start + size) });
    offset = start + Math.ceil(size / block) * block;
  }
  if (members[0]?.name !== 'oci-layout' || members[1]?.name !== 'index.json') fail('nested_image_archive_not_oci_layout');
  return members;
}

// Adds a correctly hashed arm64 image config and manifest to a nested OCI archive, in canonical
// member order, so every blob still matches its digest and only the platform selection can refuse it.
export function addForeignPlatformBlobs(archive) {
  const members = readNestedImageArchive(Buffer.from(archive));
  const config = Buffer.from(JSON.stringify({ architecture: 'arm64', os: 'linux', rootfs: { type: 'layers', diff_ids: [] }, config: {} }));
  const manifest = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: { mediaType: 'application/vnd.oci.image.config.v1+json', digest: `sha256:${sha256(config)}`, size: config.length },
    layers: [],
  }));
  const blobs = members.slice(2);
  for (const data of [config, manifest]) {
    const name = `blobs/sha256/${sha256(data)}`;
    if (!blobs.some((blob) => blob.name === name)) blobs.push({ name, data });
  }
  blobs.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const parts = [];
  for (const member of [...members.slice(0, 2), ...blobs]) {
    parts.push(imageTarHeader({ name: member.name, size: member.data.length }), member.data, padding(member.data.length));
  }
  parts.push(Buffer.alloc(block * 2));
  return Buffer.concat(parts);
}

function applicationImage(b) {
  const names = b.names().filter((name) => /^image-[a-z0-9-]+\.oci\.tar$/.test(name));
  const name = names.includes('image-frontend.oci.tar') ? 'image-frontend.oci.tar' : names[0];
  if (!name) fail('bundle_application_image_missing');
  return name;
}

const adversarialRunners = {
  'missing-image': (ctx, h, base) => h.refused('missing-image', base.rel, {
    bundle: base.tampered('missing-image', (b) => b.drop(applicationImage(b))),
    reason: /does not carry exactly the release-selected image set/,
  }),
  'missing-trust-approval': (ctx, h, base) => h.refused('missing-trust-approval', base.rel, {
    bundle: base.path, approval: join(ctx.runRoot, 'absent-approval.json'), reason: reasons.unreadableApproval,
  }),
  'unbound-trust-approval': (ctx, h, base) => h.refused('unbound-trust-approval', base.rel, {
    bundle: base.path, approval: h.release('1.0.0-insider.12', { sigstore: 'attacker' }).approvalPath,
    reason: reasons.unboundApproval,
  }),
  'missing-config': (ctx, h, base) => h.refused('missing-config', base.rel, {
    bundle: base.path, config: join(ctx.runRoot, 'absent-host-update.json'), reason: /configuration unreadable/,
  }),
  'malicious-archive-symlink': (ctx, h, base) => h.refused('malicious-archive-symlink', base.rel, {
    bundle: base.tampered('symlink', (b) => b.insertRaw(tarHeader({ name: 'evil-link', size: 0, type: '2', linkname: '/etc/passwd' }))),
    reason: /member type is not allowed: symbolic link/,
  }),
  'malicious-archive-traversal': (ctx, h, base) => h.refused('malicious-archive-traversal', base.rel, {
    bundle: base.tampered('traversal', (b) => b.insertRaw(tarHeader({ name: '../evil', size: 4 }), Buffer.from('evil'))),
    reason: /nested or absolute/,
  }),
  'modified-bytes': (ctx, h, base) => h.refused('modified-bytes', base.rel, {
    bundle: base.tampered('modified', (b) => {
      const name = b.names().find((candidate) => candidate.startsWith('printfarmer-host-update-cli-') && candidate.endsWith('.tar.gz'))
        ?? applicationImage(b);
      const bytes = Buffer.from(b.read(name));
      bytes[bytes.length >> 1] ^= 0xff;
      b.replace(name, bytes, { updateIndex: false });
    }),
    reason: /member was modified/,
  }),
  'forged-promotion': (ctx, h) => {
    h.setPolicy('stable');
    try {
      h.refused('forged-promotion', h.release('1.1.0', { signAs: 'insider' }), { reason: reasons.signature });
    } finally {
      h.setPolicy('insider');
    }
  },
  'wrong-platform': (ctx, h, base) => h.refused('wrong-platform', base.rel, {
    bundle: base.tampered('platform', (b) => {
      const name = applicationImage(b);
      b.replace(name, addForeignPlatformBlobs(b.read(name)));
    }),
    reason: /blobs outside the selected platforms/,
  }),
  'mixed-digests': (ctx, h, base) => h.refused('mixed-digests', base.rel, {
    bundle: base.tampered('mixed-digests', (b) => {
      const name = applicationImage(b);
      b.replace(name, readMembers(ctx.priorBundlePath, [name])[name]);
    }),
    reason: /image-frontend\.oci\.tar failed verification: Image archive a/,
  }),
  'mixed-channels': (ctx, h, base) => {
    const stable = h.release('1.2.0');
    const stableBundle = h.bundle(stable);
    h.refused('mixed-channels', base.rel, {
      bundle: base.tampered('mixed-channels', (b) => {
        const donor = readMembers(stableBundle, ['update-manifest.json', 'update-manifest.sigstore.json']);
        b.replace('update-manifest.json', donor['update-manifest.json']);
        b.replace('update-manifest.sigstore.json', donor['update-manifest.sigstore.json']);
      }),
      reason: /channel stable does not match the expected insider/,
    });
  },
  'expired-trust': (ctx, h, base) => h.refused('expired-trust', base.rel, {
    bundle: base.path, approval: ctx.writeApproval('expired', { ageDays: 91 }), reason: /approval expired/,
  }),
  'revoked-trust': (ctx, h, base) => {
    const revoked = ctx.writeRevokedTrustedRoot();
    h.refused('revoked-trust', base.rel, { bundle: base.path, trustedRoot: revoked.trustedRootPath, approval: revoked.approvalPath, reason: /no certificate authority valid now/ });
  },
  'invalid-signature-poisoning': (ctx, h, base) => {
    const high = h.release('1.0.0-insider.30');
    const donor = readMembers(base.path, ['update-manifest.sigstore.json']);
    h.refused('invalid-signature-poisoning', high, {
      bundle: tamper(ctx, h, h.bundle(high), 'poisoning', (b) => b.replace('update-manifest.sigstore.json', donor['update-manifest.sigstore.json'])),
      reason: reasons.signature,
    });
    h.imported('poisoning-did-not-raise-high-water', h.release('1.0.0-insider.25'));
  },
  'equal-sequence-substitution': (ctx, h) => h.refused('equal-sequence-substitution',
    h.release('1.0.0-insider.25', { seed: 'substitute' }), { reason: 'replay_rejected' }),
  'missing-replay-store': (ctx, h) => {
    ctx.hostStateFiles.hide('host-update-replay.json');
    try {
      h.refused('missing-replay-store', h.release('1.0.0-insider.26'), { reason: 'host_update_replay_state_missing' });
    } finally {
      ctx.hostStateFiles.unhide('host-update-replay.json');
    }
  },
  'rolled-back-replay-store': (ctx, h) => {
    const saved = ctx.hostStateFiles.save('before-26');
    h.imported('replay-store-advanced', h.release('1.0.0-insider.26'));
    const advanced = ctx.hostStateFiles.save('after-26');
    const advancedHashes = ctx.stateHashes();
    ctx.hostStateFiles.restore(saved, ['host-update-replay.json', 'replay-anchor.json']);
    try {
      h.refused('rolled-back-replay-store', h.release('1.0.0-insider.27'), {
        reason: 'host_update_replay_state_rollback',
        anchorHealedTo: advancedHashes['replay-anchor.json'],
      });
    } finally {
      ctx.hostStateFiles.restore(advanced);
    }
  },
};

function readMembers(bundlePath, names) {
  const fd = openSync(bundlePath, 'r');
  try {
    const entries = readOfflineBundleEntries(fd, fstatSync(fd).size);
    return Object.fromEntries(names.map((name) => {
      const entry = entries.find((candidate) => candidate.name === name);
      if (!entry) fail('bundle_member_missing', name);
      return [name, readRange(fd, entry.offset, entry.size)];
    }));
  } finally {
    closeSync(fd);
  }
}

function tamper(ctx, h, source, label, edit) {
  const output = join(ctx.runRoot, 'import-bundles', `tampered-${label}.tar`);
  rmSync(output, { force: true });
  rewriteOfflineBundle({ source, output, edit });
  return output;
}

// Every case in the documented adversarial set is refused before any mutation.
function runAdversarial(ctx, h) {
  const rel = h.release('1.0.0-insider.20');
  const path = h.bundle(rel);
  const base = { rel, path, tampered: (label, edit) => tamper(ctx, h, path, label, edit) };
  for (const name of adversarialCases) {
    adversarialRunners[name](ctx, h, base);
  }
  // The intact channel still admits a well-formed release above the high-water mark afterwards.
  h.imported('intact-bundle-after-adversarial-set', h.release('1.0.0-insider.28'));
}

// 41 is authenticated and admitted, then 42 supersedes it. Neither is installed. Both facts hold
// after policy edits, both channel round trips, a host restart, and restores of an older app
// database, policy file and staging cache; the independent stable channel keeps working.
function runReplaySupersede(ctx, h) {
  const r41 = h.release('1.0.0-insider.41');
  const r42 = h.release('1.0.0-insider.42');
  const dump = ctx.dumpDatabase();
  const olderPolicy = ctx.hostStateFiles.save('older-policy');
  const b41 = h.bundle(r41);
  const b42 = h.bundle(r42);
  h.imported('replay-41-admitted', r41, { bundle: b41, keepStaging: true });
  h.imported('replay-42-supersedes-41', r42, { bundle: b42, keepStaging: true });
  const stateAfter42 = ctx.replayState();
  const seq42 = String(r42.release.sequence);
  const seqs = new Set([String(r41.release.sequence), seq42]);
  const supersedeView = (state) => JSON.stringify({
    highWater: Object.entries(field(state, 'HighWaterByNamespace') ?? {})
      .filter(([, mark]) => String(field(mark, 'Sequence')) === seq42),
    identities: Object.entries(field(state, 'Identities') ?? {})
      .filter(([, record]) => seqs.has(String(field(record, 'Sequence'))))
      .sort(([a], [b]) => a.localeCompare(b)),
  });
  const expectedView = supersedeView(stateAfter42);
  const holds = (phase) => {
    h.refused(`41-rejected-${phase}`, r41, { bundle: b41, reason: 'replay_superseded' });
    const before = ctx.mutationSnapshot();
    const activation = ctx.activate({ built: r41, label: 'replay-41-admitted' });
    if (activation.exitCode === 0) fail('superseded_release_activated', phase);
    ctx.assertNoMutation(`41-activation-${phase}`, before, ctx.mutationSnapshot());
    ctx.checkpoints.ok(`41-activation-refused-${phase}:exit-${activation.exitCode}`);
    if (supersedeView(ctx.replayState()) !== expectedView) {
      fail('replay_state_changed', phase);
    }
    ctx.checkpoints.ok(`42-supersedes-41-${phase}`);
  };
  holds('initial');
  h.setPolicy('stable');
  h.imported('stable-independent-channel', h.release('1.0.1'));
  h.setPolicy('insider');
  holds('after-policy-edit');
  h.setPolicy('stable');
  h.setPolicy('insider');
  holds('after-insider-stable-insider');
  h.setPolicy('stable');
  h.refused('stable-lower-after-round-trip', h.release('1.0.0'), { reason: 'replay_rejected' });
  h.setPolicy('insider');
  h.setPolicy('stable');
  h.setPolicy('insider');
  holds('after-stable-insider-stable');
  ctx.restartHost();
  ctx.checkpoints.ok('host-restarted');
  holds('after-restart');
  ctx.restoreDatabase(dump);
  ctx.checkpoints.ok('older-app-database-restored');
  holds('after-app-database-restore');
  ctx.hostStateFiles.restore(olderPolicy, ['update-automation-policy.json']);
  ctx.checkpoints.ok('older-policy-restored');
  holds('after-policy-restore');
  ctx.restoreOlderStaging('replay-41-admitted');
  ctx.checkpoints.ok('older-staging-cache-restored');
  holds('after-cache-restore');
}

export { adversarialRunners };

export function writeJsonFile(path, value) {
  writeFileSync(path, `${JSON.stringify(value, undefined, 2)}\n`);
}

export function readJsonFile(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}
