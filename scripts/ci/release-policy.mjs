import { createHash } from 'node:crypto';

export const repository = 'OlyForge3D/PrintFarmer';
export const workflow = '.github/workflows/consolidated-release.yml';
export const ledgerBranch = 'release-ledger';
const numeric = '(0|[1-9][0-9]*)';
const basePattern = `${numeric}\\.${numeric}\\.${numeric}`;
const tagPattern = new RegExp(`^v(${basePattern})(?:-(insider|beta|rc)\\.([1-9][0-9]*))?$`);
const shaPattern = /^[a-f0-9]{40}$/;
export const components = {
  api: ['linux/amd64', 'linux/arm64'],
  frontend: ['linux/amd64', 'linux/arm64'],
  'slicer-host': ['linux/amd64', 'linux/arm64'],
  'printer-discovery': ['linux/amd64', 'linux/arm64'],
  'orcaslicer-worker': ['linux/amd64'],
  monolith: ['linux/amd64', 'linux/arm64'],
};

export function requireThat(condition, message) {
  if (!condition) throw new Error(message);
}

export function parseTag(tag) {
  const match = typeof tag === 'string' && tag.match(tagPattern);
  requireThat(match && match[0] === tag, 'Invalid canonical tag (strict SemVer, positive N, no leading zeros)');
  const [, baseVersion, major, minor, patch, stage, sequence] = match;
  return { baseVersion, major, minor, patch, stage, sequence,
    canonicalVersion: tag.slice(1), channel: stage ? 'insider' : 'stable' };
}

export function parseVersionFile(text) {
  // Permit the text-file final newline, not arbitrary whitespace normalization.
  const tag = text.replace(/\r?\n$/, '');
  requireThat(!parseTag(tag).stage, 'VERSION must contain only vX.Y.Z');
  return tag.slice(1);
}

export function compareVersions(left, right) {
  const a = parseTag(`v${left}`);
  const b = parseTag(`v${right}`);
  for (const field of ['major', 'minor', 'patch']) {
    if (BigInt(a[field]) !== BigInt(b[field])) return BigInt(a[field]) > BigInt(b[field]) ? 1 : -1;
  }
  // SemVer orders beta < insider < rc; stage transitions must follow this order.
  if (a.stage !== b.stage) {
    if (!a.stage) return 1;
    if (!b.stage) return -1;
    return a.stage > b.stage ? 1 : -1;
  }
  return !a.stage || a.sequence === b.sequence ? 0 : BigInt(a.sequence) > BigInt(b.sequence) ? 1 : -1;
}

export function hash(value) {
  return createHash('sha256').update(JSON.stringify(value)).digest('hex');
}

export function admit(context, selectedHead, versionText, lastStable) {
  requireThat(context.repository === repository, 'Untrusted repository');
  requireThat(['workflow_dispatch', 'schedule'].includes(context.event), 'Event cannot authorize publication');
  requireThat(context.workflowIdentity === `${repository}/${workflow}@refs/heads/${context.workflowBranch}`,
    'Untrusted workflow/caller identity');
  requireThat(context.workflowSha === context.eventSha, 'Workflow SHA mismatch');
  const channel = context.event === 'schedule' ? 'insider' : context.channel;
  const sourceBranch = channel === 'stable' ? 'main' : 'development';
  requireThat(['stable', 'insider'].includes(channel), 'Invalid channel');
  requireThat(context.ref === `refs/heads/${sourceBranch}`, 'Branch/channel mismatch');
  requireThat(context.workflowBranch === sourceBranch, 'Workflow/source branch mismatch');
  requireThat(shaPattern.test(selectedHead) && context.eventSha === selectedHead,
    'Source must equal canonical branch HEAD at admission; reselect and requalify');
  for (const field of ['buildId', 'buildAttempt']) {
    requireThat(/^[1-9][0-9]*$/.test(context[field]), `Invalid ${field}`);
  }
  const baseVersion = parseVersionFile(versionText);
  const stage = channel === 'stable' ? undefined : (context.stage || 'insider');
  requireThat(!stage || ['insider', 'beta', 'rc'].includes(stage), 'Unsupported stage');
  requireThat(channel !== 'stable' || !context.stage, 'Stable cannot select a stage');
  requireThat(context.event !== 'schedule' || stage === 'insider', 'Schedule only supports ordinary insider');
  if (channel === 'insider' && lastStable) {
    requireThat(compareVersions(baseVersion, lastStable) > 0, 'Development base must exceed published stable');
  }
  if (context.requestedTag) {
    const requested = parseTag(context.requestedTag);
    requireThat(requested.baseVersion === baseVersion && requested.channel === channel && requested.stage === stage,
      'Requested tag disagrees with VERSION/channel/stage');
  }
  return {
    repository, channel, baseVersion, sourceBranch, stage,
    sourceCommit: selectedHead, authorizedBranchHead: selectedHead,
    buildId: context.buildId, buildAttempt: context.buildAttempt,
    workflowIdentity: context.workflowIdentity, workflowCommit: context.workflowSha,
  };
}

export function allocationKey(admission) {
  return hash([admission.repository, admission.workflowIdentity, admission.buildId,
    admission.buildAttempt, admission.sourceCommit, admission.baseVersion]);
}

export function validateLedger(state, anchor) {
  requireThat(state?.schema === 1 && shaPattern.test(anchor) && state.anchor === anchor,
    'Ledger missing or continuity anchor mismatch: owner recovery required');
  requireThat(/^(0|[1-9][0-9]*)$/.test(state.counter), 'Invalid ledger counter');
  requireThat(state.reservations && state.pointers && state.identities, 'Incomplete ledger');
  const sequences = new Set();
  for (const [key, reservation] of Object.entries(state.reservations)) {
    requireThat(reservation.record?.allocationKey === key &&
      state.identities[reservation.record.canonicalVersion] === key &&
      reservation.sequence === reservation.record.sequence, 'Ledger identity continuity violation');
    if (!reservation.sequence) continue;
    requireThat(/^[1-9][0-9]*$/.test(reservation.sequence) &&
      BigInt(reservation.sequence) <= BigInt(state.counter) &&
      !sequences.has(reservation.sequence), 'Ledger sequence continuity violation');
    sequences.add(reservation.sequence);
  }
}

export function reserve(state, admission, created) {
  const key = allocationKey(admission);
  const existing = state.reservations[key];
  if (existing) {
    requireThat(hash(existing.admission) === hash(admission), 'Same allocation key changed its admission');
    return existing;
  }
  const sequence = admission.channel === 'insider' ? (BigInt(state.counter) + 1n).toString() : undefined;
  const canonicalVersion = admission.baseVersion + (sequence ? `-${admission.stage}.${sequence}` : '');
  requireThat(!state.identities[canonicalVersion], 'Immutable identity already reserved');
  const previousStage = state.stages?.[admission.baseVersion];
  if (sequence && previousStage) {
    requireThat(compareVersions(canonicalVersion, previousStage) > 0,
      'Stage regression: beta < insider < rc; bump base before returning to an earlier stage');
  }
  const record = {
    schema: 1, ...admission, releaseId: `${admission.channel}:${canonicalVersion}`,
    canonicalVersion, sourceTag: `v${canonicalVersion}`, sequence, allocationKey: key, created,
  };
  if (admission.channel === 'stable') {
    const qualification = state.qualifications?.[admission.sourceCommit];
    requireThat(qualification?.sourceCommit === admission.sourceCommit &&
      qualification.reviewed === true && qualification.tests === 'passed' &&
      qualification.compatibility === 'passed' && qualification.migrations === 'passed' &&
      qualification.recovery === 'passed', 'Stable requires owner-recorded exact-SHA qualification');
    if (qualification.promotionOrigin) {
      const origin = qualification.promotionOrigin;
      const candidate = state.reservations[origin.allocationKey];
      requireThat(candidate?.setHash === origin.setHash && candidate.record.channel === 'insider' &&
        candidate.record.sourceCommit === origin.sourceCommit &&
        candidate.record.releaseId === origin.releaseId &&
        qualification.sourceTreeReviewed === true,
      'Promotion requires a qualified immutable insider set and reviewed main source-tree changes');
    } else {
      requireThat(typeof qualification.hotfixReason === 'string' && qualification.hotfixReason.trim().length >= 10,
        'Direct stable release requires an explicit non-promotion qualification reason');
    }
    record.qualification = qualification;
  }
  const reservation = { admission, record, sequence };
  state.reservations[key] = reservation;
  state.identities[canonicalVersion] = key;
  if (sequence) {
    state.counter = sequence;
    state.stages ??= {};
    state.stages[admission.baseVersion] = canonicalVersion;
  }
  return reservation;
}

export function verifyTag(record, expectedObject, actualTag) {
  requireThat(actualTag?.object === expectedObject && actualTag.commit === record.sourceCommit,
    'Source tag missing, moved, recreated, or not the exact authorized peeled SHA');
}

export function verifyConsumer(record, stored, context) {
  requireThat(hash(record) === hash(stored), 'Canonical record was changed');
  requireThat(context.repository === repository &&
    context.workflowIdentity === record.workflowIdentity &&
    context.workflowSha === record.workflowCommit &&
    context.eventSha === record.sourceCommit &&
    context.ref === `refs/heads/${record.sourceBranch}` &&
    record.authorizedBranchHead === record.sourceCommit &&
    record.sourceBranch === (record.channel === 'stable' ? 'main' : 'development') &&
    ['stable', 'insider'].includes(record.channel) &&
    context.buildId === record.buildId && context.buildAttempt === record.buildAttempt &&
    ['workflow_dispatch', 'schedule'].includes(context.event),
  'Unauthorized consumer/caller/run/attempt');
}

export function identityLabels(record) {
  return {
    'org.opencontainers.image.version': record.canonicalVersion,
    'org.opencontainers.image.revision': record.sourceCommit,
    'org.opencontainers.image.source': `https://github.com/${record.repository}`,
    'org.opencontainers.image.created': record.created,
    'org.printfarmer.release-channel': record.channel,
    'org.printfarmer.release-id': record.releaseId,
    'org.printfarmer.build-id': record.buildId,
    'org.printfarmer.build-attempt': record.buildAttempt,
    'org.printfarmer.workflow': record.workflowIdentity,
    'org.printfarmer.identity-sha256': hash(record),
  };
}

export function validateCompleteSet(record, set) {
  requireThat(set.schema === 1 && set.managedEligible === false,
    'Preparatory release sets are not managed eligibility manifests');
  requireThat(hash(set.identity) === hash(record), 'Set identity mismatch');
  requireThat(Object.keys(set.images).sort().join() === Object.keys(components).sort().join(),
    'Incomplete or mixed component set');
  for (const [component, platforms] of Object.entries(components)) {
    const image = set.images[component];
    requireThat(/^sha256:[a-f0-9]{64}$/.test(image.digest), 'Invalid image digest');
    requireThat(Object.keys(image.platforms).sort().join() === [...platforms].sort().join(),
      `Incomplete platforms: ${component}`);
    for (const platform of Object.values(image.platforms)) {
      requireThat(/^sha256:[a-f0-9]{64}$/.test(platform.digest), 'Invalid platform digest');
      for (const [key, value] of Object.entries(identityLabels(record))) {
        requireThat(platform.labels[key] === value, `Mixed identity: ${component}/${key}`);
      }
    }
  }
}

export function advance(state, record, set, currentHead, expectedPointer) {
  validateCompleteSet(record, set);
  requireThat(record.sourceCommit === currentHead, 'Stale source cannot advance channel, regardless of N');
  const reservation = state.reservations[record.allocationKey];
  requireThat(reservation && hash(reservation.record) === hash(record), 'Unknown authorization');
  const pointer = state.pointers[record.channel];
  requireThat((pointer?.setHash || '') === expectedPointer, 'Channel compare-and-set conflict');
  const setHash = hash(set);
  if (reservation.setHash) requireThat(reservation.setHash === setHash, 'Same identity, different bytes');
  if (pointer?.releaseId === record.releaseId) {
    requireThat(pointer.setHash === setHash, 'Same identity, different bytes');
    return pointer;
  }
  if (pointer) requireThat(compareVersions(record.canonicalVersion, pointer.canonicalVersion) > 0,
    'Channel version regression');
  reservation.setHash = setHash;
  reservation.set = set;
  state.pointers[record.channel] = {
    releaseId: record.releaseId, canonicalVersion: record.canonicalVersion,
    sourceCommit: record.sourceCommit, setHash, allocationKey: record.allocationKey,
  };
  return state.pointers[record.channel];
}

// A Git ref fast-forward is the transaction. Two sibling commits cannot both win.
export async function transact(store, mutate, retries = 20) {
  for (let attempt = 0; attempt < retries; attempt++) {
    const { revision, state } = await store.read();
    const result = await mutate(state);
    if (await store.compareAndSet(revision, state)) return result;
  }

  throw new Error('Ledger CAS contention; retry the same allocation key');
}

export function validateCandidate(candidate, now, maximumDays) {
  requireThat(Number.isInteger(maximumDays) && maximumDays > 0, 'Owner must choose candidate expiry limit');
  const target = parseTag(`v${candidate.target}`);
  requireThat(!target.stage && candidate.branch === `release/v${target.baseVersion}`, 'Invalid stabilization branch');
  requireThat(shaPattern.test(candidate.sourceCommit) && candidate.owner &&
    candidate.qualification && candidate.created && candidate.expires, 'Incomplete candidate ownership/evidence');
  const created = Date.parse(candidate.created);
  const expires = Date.parse(candidate.expires);
  requireThat(Number.isFinite(created) && Number.isFinite(expires) && expires > created &&
    expires - created <= maximumDays * 86400000 && Date.parse(now) < expires, 'Expired candidate or invalid lifetime');
  if (candidate.action === 'delete') {
    requireThat(candidate.mergeBack?.development === true && candidate.mergeBack?.activeCandidates === true &&
      candidate.mergeBack?.versionDidNotRegress === true, 'Candidate deletion requires merge-back parity');
    requireThat(candidate.publication || candidate.abandonmentReason, 'Retain candidate until publication or documented abandonment');
  }
  requireThat(!candidate.publish, 'Stabilization branches never publish');
  return candidate;
}
