import { assessHostCapacity, classifyWork } from './ralph-host-capacity.mjs';

const priorities = { revision: 0, ci: 1, review: 2, integration: 3 };
const knownHosts = new Set(['macos-mobile', 'windows-general']);
const holds = new Set(['status:on-hold', 'status:blocked', 'blocked', 'status:wontfix', 'do-not-merge']);
const shaPattern = /^[0-9a-f]{40}$/i;

function requirePull(pull) {
  if (!Number.isSafeInteger(pull?.number) || pull.number <= 0 ||
      !shaPattern.test(pull.headSha) || !Array.isArray(pull.labels) ||
      !['open', 'closed'].includes(pull.state) ||
      (pull.failedChecks !== undefined && !Array.isArray(pull.failedChecks))) {
    throw new Error('PR recovery requires a numbered PR, full head SHA and labels.');
  }
}

function recoveryKind(pull) {
  const verdict = pull.verdict?.headSha === pull.headSha
    ? pull.verdict.classification : 'SUPERSEDED';
  if (verdict === 'CHANGES_REQUESTED') return 'revision';
  if (pull.failedChecks?.length) return 'ci';
  if (!['REVIEWED', 'APPROVED'].includes(verdict)) return 'review';
  return 'integration';
}

function hasCompleteFiles(pull) {
  return pull.filesComplete === true && Array.isArray(pull.files) &&
    pull.files.length > 0 && pull.files.every((file) => typeof file === 'string' && file.length > 0);
}

function ownershipState(owner, host, now) {
  const observedAt = Date.parse(owner?.observedAt);
  if (!owner?.source || !Number.isFinite(observedAt) || observedAt > now ||
      now - observedAt > 60_000 || owner.inventoryChecked !== true ||
      owner.historyChecked !== true || owner.queueChecked !== true) return 'unknown';
  if (owner.state === 'live' && owner.sessionId && owner.host) return 'live';
  if (owner.host === host && owner.state === 'inactive' && owner.noPendingDelivery === true &&
      owner.admissionReconciled === true) return 'inactive';
  return 'unknown';
}

// Observation-only planning. Admission must re-fetch GitHub, reconcile the shared
// ledger and verify App ownership; this result never authorizes dispatch or merge.
export function planPrRecovery({ pulls, ownership, host, scope, capacity, now = Date.now() }) {
  if (!Array.isArray(pulls) || !ownership || typeof ownership !== 'object' ||
      !['macos-mobile', 'windows-general'].includes(host) || !['mobile', 'general', 'mixed'].includes(scope)) {
    throw new Error('A complete PR listing and explicit ownership observations are required.');
  }
  const seen = new Set();
  const queue = [];
  const inFlight = [];
  const blocked = [];
  const deferred = [];
  const occupied = [];
  const liveCapacity = [];
  const invalidOwnership = [];
  let unknownRemoteExecution = false;
  for (const pull of pulls) {
    requirePull(pull);
    if (seen.has(pull.number)) throw new Error(`Duplicate PR #${pull.number}.`);
    seen.add(pull.number);
    if (pull.state !== 'open' || !pull.labels.includes('squad')) continue;
    const owner = ownership[pull.number];
    if (owner && ((owner.host !== undefined && !knownHosts.has(owner.host)) ||
        (owner.executionHost !== undefined && !knownHosts.has(owner.executionHost)))) {
      const reason = `PR #${pull.number} has invalid ownership host/executionHost; reconcile its execution placement before admitting work.`;
      invalidOwnership.push(reason);
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason });
      occupied.push(pull);
      continue;
    }
    const state = ownershipState(owner, host, now);
    const category = classifyWork(pull);
    if (state === 'live') {
      if (category === 'mobile' && owner.host === 'windows-general' && !owner.executionHost) unknownRemoteExecution = true;
      else liveCapacity.push({
        ...pull, sessionId: owner.sessionId, executionHost: owner.executionHost ?? owner.host, state: 'active',
      });
    }
    if (pull.sameRepository !== true || pull.labels.some((label) => holds.has(label))) {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: 'fork, unknown repository, or explicit hold' });
      occupied.push(pull);
      continue;
    }
    if ((scope !== 'mixed' && category !== scope) || (host === 'windows-general' && category === 'mobile')) {
      deferred.push({ pr: pull.number, headSha: pull.headSha, reason: 'other host scope or unknown scope' });
      occupied.push(pull);
      continue;
    }
    if (state === 'live') {
      inFlight.push({ pr: pull.number, headSha: pull.headSha, sessionId: owner.sessionId, host: owner.host });
      occupied.push(pull);
    } else if (state === 'unknown') {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: 'ownership not proven inactive' });
      occupied.push(pull);
    } else {
      queue.push({ ...pull, kind: recoveryKind(pull) });
    }
  }
  queue.sort((left, right) => priorities[left.kind] - priorities[right.kind] || left.number - right.number);
  const ready = [];
  const inventory = capacity && {
    ...capacity, complete: capacity.complete === true && !unknownRemoteExecution,
    work: [...(capacity.work ?? []), ...liveCapacity],
  };
  for (const pull of queue) {
    if (invalidOwnership.length) {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: invalidOwnership.join(' ') });
      occupied.push(pull);
      continue;
    }
    if (!hasCompleteFiles(pull)) {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: 'changed-file coverage incomplete' });
      occupied.push(pull);
      continue;
    }
    const conflicts = occupied.filter((other) =>
      !hasCompleteFiles(other) || other.files.some((file) => pull.files.includes(file)));
    if (conflicts.length) {
      blocked.push({
        pr: pull.number, headSha: pull.headSha, reason: 'shared-file recovery already owned',
        conflicts: conflicts.map((other) => other.number),
      });
      continue;
    }
    if (host === 'macos-mobile' && classifyWork(pull) === 'general') {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: 'Mac general recovery needs a shared atomic Windows-authority admission path; none is deployed by this policy.' });
      occupied.push(pull);
      continue;
    }
    let capacityResult;
    try { capacityResult = assessHostCapacity({ host, inventory, candidate: pull, now }); }
    catch (error) {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: error.message });
      occupied.push(pull);
      continue;
    }
    if (!capacityResult.allowed) {
      blocked.push({ pr: pull.number, headSha: pull.headSha, reason: capacityResult.reason });
      occupied.push(pull);
      continue;
    }
    ready.push({
      pr: pull.number, headSha: pull.headSha, kind: pull.kind,
      findings: pull.kind === 'revision' ? pull.verdict : undefined,
      failedChecks: pull.failedChecks ?? [], files: pull.files,
    });
    inventory.work.push({
      jobId: `planned-pr-${pull.number}`, executionHost: host, state: 'reserved',
      scope: capacityResult.category, classificationComplete: true,
    });
    occupied.push(pull);
  }
  return { ready, inFlight, blocked, deferred };
}
