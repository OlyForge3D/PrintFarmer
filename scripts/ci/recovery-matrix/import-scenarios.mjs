// Import-cell scenario runner for issue #3102. run-cell.mjs supplies the live host boundary
// (packaged import/activation, host shell, policy edits, restarts, database dump/restore); this
// module owns the channel, identity, adversarial and replay assertions so they stay unit-testable.
import { closeSync, fstatSync, openSync, readFileSync, readSync, rmSync, writeFileSync, writeSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';

import { readOfflineBundleEntries, tarHeader } from '../offline-update-bundle.mjs';
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

export function assertRefusedImport(label, result, { reason } = {}) {
  if (result.exitCode === 0) fail('import_not_refused', label);
  const record = result.record;
  if (record) {
    if (record.outcome !== 'refused') fail('import_refusal_outcome', `${label}:${record.outcome}`);
    if (record.installable !== false || record.replay?.admitted === true) fail('import_refusal_admitted', label);
    if ((record.loadedImages ?? []).length !== 0) fail('import_refusal_loaded_images', label);
    if (reason && !String(record.reason ?? '').includes(reason)) {
      fail('import_refusal_reason', `${label}:expected ${reason}:got ${record.reason}`);
    }
  } else if (reason && !`${result.stdout}\n${result.stderr}`.includes(reason)) {
    fail('import_refusal_reason', `${label}:expected ${reason}:no decision record`);
  }
  return record?.reason ?? `exit:${result.exitCode}`;
}

function assertSameState(label, before, after, { allowReplayRecord = false } = {}) {
  const replayFiles = new Set(['host-update-replay.json', 'replay-anchor.json', 'replay-anchor.journal']);
  const drift = Object.keys({ ...before, ...after })
    .filter((key) => before[key] !== after[key])
    .filter((key) => !(allowReplayRecord && replayFiles.has(key)));
  if (drift.length > 0) fail('import_refusal_mutated_state', `${label}:${drift.join(',')}`);
}

const field = (object, name) => object?.[name] ?? object?.[name[0].toLowerCase() + name.slice(1)];

// An authenticated replay refusal durably records the refused identity (so it stays refused
// across restarts and restores). That record must not change what is admitted: every channel
// high-water mark and every earlier identity decision is unchanged, and only Rejected or
// Superseded identities are added.
export function replayAdmissionDrift(before, after) {
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
  for (const [identity, record] of Object.entries(idsAfter)) {
    if (identity in idsBefore) continue;
    const disposition = String(field(record, 'Disposition'));
    if (disposition !== 'Rejected' && disposition !== 'Superseded') drift.push(`identity-added:${identity}:${disposition}`);
  }
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
    const result = ctx.importBundle({ built: rel, bundle: options.bundle ?? bundle(rel), label, ...options });
    const reason = assertRefusedImport(label, result, { reason: options.reason });
    if (ctx.stagingExists(label)) fail('import_refusal_left_staging', label);
    const allowReplayRecord = authenticatedReplayRefusal(reason);
    const after = ctx.stateHashes();
    // The anchor snapshot is a cache of the append-only anchor journal. Detecting a rolled-back
    // snapshot heals it forward to the journal head; that restores, never advances, admission.
    if (options.anchorRepairTo && after['replay-anchor.json'] === options.anchorRepairTo) {
      before['replay-anchor.json'] = after['replay-anchor.json'];
    }
    assertSameState(label, before, after, { allowReplayRecord });
    if (allowReplayRecord) {
      const drift = replayAdmissionDrift(replayBefore, ctx.replayState());
      if (drift.length > 0) fail('import_refusal_changed_admission', `${label}:${drift.join(',')}`);
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
  h.refused('self-enrolled-root', attacker, { bundle: h.bundle(attacker, { trustedRootPath: attacker.trustedRootPath }) });
  h.refused('self-enrolled-root-approval', attacker, {
    bundle: h.bundle(attacker, { trustedRootPath: attacker.trustedRootPath }),
    approval: attacker.approvalPath,
  });
  const insiderBundle = h.bundle(insider);
  h.refused('fresh-host-without-approval', insider, { bundle: insiderBundle, approval: join(ctx.runRoot, 'absent-approval.json') });
  h.refused('fresh-host-unbound-approval', insider, { bundle: insiderBundle, approval: attacker.approvalPath });
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
  h.refused('insider-bundle-as-stable-alias', h.release('1.0.5-insider.1'), { channel: 'stable' });
  h.setPolicy('stable');
  h.refused('unsupported-downgrade-stable', h.release('1.0.0'));
  h.refused('moved-branch-same-version', h.release('1.0.3', { seed: 'moved-branch' }));
  h.refused('deleted-alias-reimport', h.release('1.0.2'), { reason: 'replay_superseded' });
  h.setPolicy('insider');
  h.refused('unsupported-downgrade-insider', h.release('1.0.3-insider.1'), { reason: 'replay_superseded' });
  h.refused('moved-branch-insider', h.release('1.0.4-insider.1', { seed: 'moved-branch' }));
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
  }),
  'missing-trust-approval': (ctx, h, base) => h.refused('missing-trust-approval', base.rel, {
    bundle: base.path, approval: join(ctx.runRoot, 'absent-approval.json'),
  }),
  'unbound-trust-approval': (ctx, h, base) => h.refused('unbound-trust-approval', base.rel, {
    bundle: base.path, approval: h.release('1.0.0-insider.12', { sigstore: 'attacker' }).approvalPath,
  }),
  'missing-config': (ctx, h, base) => h.refused('missing-config', base.rel, {
    bundle: base.path, config: join(ctx.runRoot, 'absent-host-update.json'),
  }),
  'malicious-archive-symlink': (ctx, h, base) => h.refused('malicious-archive-symlink', base.rel, {
    bundle: base.tampered('symlink', (b) => b.insertRaw(tarHeader({ name: 'evil-link', size: 0, type: '2', linkname: '/etc/passwd' }))),
  }),
  'malicious-archive-traversal': (ctx, h, base) => h.refused('malicious-archive-traversal', base.rel, {
    bundle: base.tampered('traversal', (b) => b.insertRaw(tarHeader({ name: '../evil', size: 4 }), Buffer.from('evil'))),
  }),
  'modified-bytes': (ctx, h, base) => h.refused('modified-bytes', base.rel, {
    bundle: base.tampered('modified', (b) => {
      const name = b.names().find((candidate) => candidate.startsWith('printfarmer-host-update-cli-') && candidate.endsWith('.tar.gz'))
        ?? applicationImage(b);
      const bytes = Buffer.from(b.read(name));
      bytes[bytes.length >> 1] ^= 0xff;
      b.replace(name, bytes, { updateIndex: false });
    }),
  }),
  'forged-promotion': (ctx, h) => {
    h.setPolicy('stable');
    try {
      h.refused('forged-promotion', h.release('1.1.0', { signAs: 'insider' }));
    } finally {
      h.setPolicy('insider');
    }
  },
  'wrong-platform': (ctx, h, base) => h.refused('wrong-platform', base.rel, {
    bundle: base.tampered('platform', (b) => {
      const name = applicationImage(b);
      const bytes = Buffer.from(b.read(name));
      let swapped = 0;
      for (let at = bytes.indexOf('amd64'); at !== -1; at = bytes.indexOf('amd64', at + 5)) {
        bytes.write('arm64', at, 'ascii');
        swapped += 1;
      }
      if (swapped === 0) fail('wrong_platform_marker_missing', name);
      b.replace(name, bytes);
    }),
  }),
  'mixed-digests': (ctx, h, base) => h.refused('mixed-digests', base.rel, {
    bundle: base.tampered('mixed-digests', (b) => {
      const name = applicationImage(b);
      b.replace(name, readMembers(ctx.priorBundlePath, [name])[name]);
    }),
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
    });
  },
  'expired-trust': (ctx, h, base) => h.refused('expired-trust', base.rel, {
    bundle: base.path, approval: ctx.writeApproval('expired', { ageDays: 91 }),
  }),
  'revoked-trust': (ctx, h, base) => {
    const revoked = ctx.writeRevokedTrustedRoot();
    h.refused('revoked-trust', base.rel, { bundle: base.path, trustedRoot: revoked.trustedRootPath, approval: revoked.approvalPath });
  },
  'invalid-signature-poisoning': (ctx, h, base) => {
    const high = h.release('1.0.0-insider.30');
    const donor = readMembers(base.path, ['update-manifest.sigstore.json']);
    h.refused('invalid-signature-poisoning', high, {
      bundle: tamper(ctx, h, h.bundle(high), 'poisoning', (b) => b.replace('update-manifest.sigstore.json', donor['update-manifest.sigstore.json'])),
    });
    h.imported('poisoning-did-not-raise-high-water', h.release('1.0.0-insider.25'));
  },
  'equal-sequence-substitution': (ctx, h) => h.refused('equal-sequence-substitution',
    h.release('1.0.0-insider.25', { seed: 'substitute' }), { reason: 'replay_rejected' }),
  'missing-replay-store': (ctx, h) => {
    ctx.hostStateFiles.hide('host-update-replay.json');
    try {
      h.refused('missing-replay-store', h.release('1.0.0-insider.26'));
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
      h.refused('rolled-back-replay-store', h.release('1.0.0-insider.27'), { anchorRepairTo: advancedHashes['replay-anchor.json'] });
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
  h.refused('stable-lower-after-round-trip', h.release('1.0.0'));
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
