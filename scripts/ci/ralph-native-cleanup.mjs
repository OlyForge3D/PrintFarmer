import path from 'node:path';
import { digest } from './ralph-mailbox.mjs';
import { assessCleanupCandidate } from './ralph-round-cache.mjs';

// Owning-consumer deletion of its own settled Ralph workers (#2954). The plan is
// read-only; deletion intent is journaled before delete_item and a deletion is
// recorded only after get_session not-found for every known identifier and an
// absent worktree directory. Uncertain results stay pending and are never retried.

export const reapSettleMs = 15 * 60 * 1000;
export const defaultMaxDeletions = 5;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const digestPattern = /^[0-9a-f]{64}$/;
const rolePrefix = /^\s*(ralph|reaper)\b/i;
const fail = (message) => { throw new Error(`Native Ralph blocked: ${message}`); };

function withinRoot(root, target) {
  if (!path.isAbsolute(root ?? '') || !path.isAbsolute(target ?? '') || path.normalize(target) !== target) return false;
  const relative = path.relative(root, target);
  return Boolean(relative) && !relative.startsWith('..') && !path.isAbsolute(relative);
}

export function requireCleanupRole(config) {
  if (config.role !== 'consumer' || typeof config.host !== 'string' || !config.host.startsWith('macos-')) {
    fail('Only the owning macOS consumer may plan or record deletion of its own settled workers; the coordinator never deletes.');
  }
}

// Verified retained terminal proof for one mapping, or a retention reason.
export function terminalProof(state, journal, correlation, mapping) {
  const assignment = state.assignments[mapping?.assignmentId];
  const receipt = assignment?.receipts?.at(-1);
  if (!assignment) return { reason: 'mapped assignment is missing from the mailbox' };
  if (assignment.state !== 'terminal') return { reason: `assignment is not coordinator-settled (state ${assignment.state})` };
  if (assignment.disposition === 'withdrawn-before-delivery') return { reason: 'withdrawn assignment has no delivered worker' };
  if (journal.sessions[correlation] !== mapping || !uuidPattern.test(mapping.sessionId ?? '')) {
    return { reason: 'no correlated native session mapping' };
  }
  if (receipt?.status !== 'terminal-reported' || receipt.correlation !== correlation ||
      !digestPattern.test(mapping.lastEvidenceDigest ?? '') ||
      mapping.lastEvidenceDigest !== receipt.evidenceDigest ||
      mapping.lastEvidenceDigest !== assignment.terminalCommitment) {
    return { reason: 'retained correlated terminal evidence does not match the settled receipt' };
  }
  const settledFrom = Date.parse(receipt.observedAt);
  if (!Number.isFinite(settledFrom)) return { reason: 'terminal receipt time is unknown' };
  return { assignment, receipt, terminalEvidenceDigest: mapping.lastEvidenceDigest, settledFrom };
}

function knownAliases(mapping) {
  return [...new Set([...(mapping.sessionAliases ?? []), mapping.creationHandle]
    .filter((id) => uuidPattern.test(id ?? '') && id !== mapping.sessionId))];
}

// Validates every retained deletion record against its immutable mapping and
// returns every identifier that must never reappear live.
export function deletionLedger(journal, state) {
  const deletions = journal.deletions ?? {};
  const bySession = new Map();
  const identifiers = new Map();
  const claim = (id, owner) => {
    if (identifiers.has(id) && identifiers.get(id) !== owner) fail('Deletion identifiers collide across Ralph mappings.');
    identifiers.set(id, owner);
  };
  for (const [sessionId, record] of Object.entries(deletions)) {
    const mapping = journal.sessions[record?.correlation];
    if (record?.sessionId !== sessionId || !uuidPattern.test(sessionId) || !mapping ||
        mapping.sessionId !== sessionId || mapping.assignmentId !== record.assignmentId ||
        !['pending', 'deleted'].includes(record.status) || !Array.isArray(record.aliases) ||
        record.aliases.some((alias) => !uuidPattern.test(alias) || alias === sessionId)) {
      fail('Retained deletion record no longer matches its immutable Ralph mapping.');
    }
    const proof = terminalProof(state, journal, record.correlation, mapping);
    if (proof.reason || proof.terminalEvidenceDigest !== record.terminalEvidenceDigest) {
      fail('Retained deletion record lacks matching settled terminal evidence.');
    }
    bySession.set(sessionId, record);
    for (const id of [sessionId, ...record.aliases]) claim(id, sessionId);
  }
  return { bySession, identifiers };
}

export function deletedWorkerIds(journal, state) {
  const { bySession } = deletionLedger(journal, state);
  return new Set([...bySession.values()].filter((record) => record.status === 'deleted').map((record) => record.sessionId));
}

function validateEvidenceShape(config, evidence) {
  if (!uuidPattern.test(evidence?.callingSessionId ?? '') || !path.isAbsolute(evidence.mainCheckoutPath ?? '') ||
      !Array.isArray(evidence.candidates)) {
    fail('Cleanup evidence needs callingSessionId, mainCheckoutPath and a candidates array.');
  }
  const max = evidence.maxDeletions ?? defaultMaxDeletions;
  if (!Number.isInteger(max) || max < 1 || max > defaultMaxDeletions) fail(`maxDeletions must be 1-${defaultMaxDeletions}.`);
  if (!path.isAbsolute(config.worktreeRoot ?? '')) fail('Configured worktree root required for cleanup.');
  return max;
}

function prReasons(candidate, proof, now) {
  const { worktree, prs } = candidate;
  const reasons = [];
  let pr;
  let clock = proof.settledFrom;
  if (candidate.prsChecked !== true || !Array.isArray(prs)) return { reasons: ['PR lookup by head branch is incomplete'] };
  if (prs.some((item) => item?.state === 'OPEN')) return { reasons: ['open PR is never deleted'] };
  if (prs.length > 1) return { reasons: ['multiple PRs for the branch need human review'] };
  if (prs.length === 1) {
    pr = prs[0];
    if (!['MERGED', 'CLOSED'].includes(pr.state)) return { reasons: [`PR state ${pr.state} is not terminal`] };
    const at = Date.parse(pr.state === 'MERGED' ? pr.mergedAt : pr.closedAt);
    if (!Number.isFinite(at) || at > now) reasons.push('PR merge/close time is unknown');
    else clock = Math.max(clock, at);
    if (pr.state === 'CLOSED' && worktree?.remoteBranchExists !== true) reasons.push('origin branch for closed PR is absent');
  } else if (!Number.isInteger(worktree?.commitsAheadOfDevelopment) || worktree.commitsAheadOfDevelopment < 0) {
    reasons.push('commits ahead of origin/development are unknown');
  } else if (worktree.commitsAheadOfDevelopment > 0) {
    reasons.push(worktree.unpushedCommits === 0
      ? 'no PR with pushed commits needs human review'
      : 'WARNING: no PR with unpushed commits');
  } else if (!['research', 'analysis'].includes(proof.assignment.task?.purpose)) {
    reasons.push('no-PR work without commits is deletable only for research/analysis');
  }
  return { reasons, pr, clock };
}

async function evaluate(config, candidate, mapping, correlation, proof, context) {
  const { evidence, now, readArtifact } = context;
  const reasons = [];
  const live = candidate.live ?? {};
  const worktree = candidate.worktree ?? {};
  if (live.found !== true) reasons.push('fresh get_session readback is missing');
  if (typeof live.name !== 'string' || rolePrefix.test(live.name)) reasons.push('role-named or unnamed session is never deleted');
  for (const [flag, reason] of [['busy', 'session is busy or activity is unknown'],
    ['pendingInput', 'session has pending input or it is unknown'],
    ['agentMerge', 'Agent merge is active or unknown'], ['automation', 'session automation is attached or unknown']]) {
    if (live[flag] !== false) reasons.push(reason);
  }
  const worktreePath = mapping.worktreePath;
  if (!withinRoot(config.worktreeRoot, worktreePath) || worktreePath === evidence.mainCheckoutPath ||
      live.worktreePath !== worktreePath || worktree.path !== worktreePath) {
    reasons.push('worktree path does not match the recorded isolated worker worktree');
  }
  if (typeof live.branch !== 'string' || !live.branch || worktree.branch !== live.branch) reasons.push('worker branch readback does not match');
  if (worktree.exists !== true || worktree.gitPresent !== true) reasons.push('worktree or its .git is missing');
  if (worktree.unpushedCommits !== 0) reasons.push('WARNING: unpushed commits or unknown upstream');
  const { reasons: prIssues, pr, clock } = prReasons(candidate, proof, now);
  reasons.push(...prIssues);
  let noPrDeliverable = { completed: false, verified: false };
  if (!pr && !prIssues.length) {
    try {
      const artifact = await readArtifact(proof.assignment, candidate.artifactUrl);
      const retained = mapping.terminalArtifact;
      const matches = !retained || (retained.url === artifact.url && retained.bodyDigest === artifact.bodyDigest);
      if (!matches) reasons.push('research artifact no longer matches the retained terminal artifact');
      noPrDeliverable = { completed: matches, verified: matches };
    } catch { reasons.push('research artifact readback failed'); }
  }
  const assessment = assessCleanupCandidate({
    session: { active: !['busy', 'pendingInput', 'agentMerge', 'automation'].every((flag) => live[flag] === false) },
    worktree: { inspected: worktree.exists === true && worktree.gitPresent === true,
      dirty: worktree.porcelainEmpty !== true, untracked: worktree.porcelainEmpty !== true },
    finalReport: { workingTreeClean: worktree.porcelainEmpty === true, allCommitsPushed: worktree.unpushedCommits === 0,
      closedWithoutMerge: pr?.state === 'CLOSED', closureReason: pr?.closureReason },
    settledAt: Number.isFinite(clock) ? new Date(clock).toISOString() : undefined,
    pr: pr && { state: pr.state, mergeCommitVerifiedOnDevelopment: pr.mergeCommitOnDevelopment === true,
      headPreservedAfterMerge: pr.headPreservedAfterMerge === true, linkedIssueDispositionVerified: true,
      commitsAfterMergeKnown: Array.isArray(pr.commitsAfterMerge), commitsAfterMerge: pr.commitsAfterMerge },
    noPrDeliverable,
  }, { now, settlingMs: reapSettleMs });
  for (const reason of assessment.reasons) if (!reasons.includes(reason)) reasons.push(reason);
  return {
    sessionId: mapping.sessionId, aliases: candidate.aliases, assignmentId: mapping.assignmentId, correlation,
    terminalEvidenceDigest: proof.terminalEvidenceDigest, worktreePath,
    settledAt: Number.isFinite(clock) ? new Date(clock).toISOString() : undefined, reasons,
  };
}

// Read-only. Every mapped worker of this consumer is either eligible, retained
// with reasons, pending an unconfirmed deletion, or already deleted.
export async function planWorkerCleanup({ config, evidence, journal, state, now, readArtifact, roundId }) {
  requireCleanupRole(config);
  const max = validateEvidenceShape(config, evidence);
  const { bySession, identifiers } = deletionLedger(journal, state);
  const mappingBySession = new Map();
  const mappedIdentifiers = new Map();
  for (const [correlation, mapping] of Object.entries(journal.sessions)) {
    if (state.assignments[mapping.assignmentId]?.workerId !== config.workerId) fail('Recorded Ralph mapping has unresolved assignment ownership.');
    if (!uuidPattern.test(mapping.sessionId ?? '')) continue;
    mappingBySession.set(mapping.sessionId, { correlation, mapping });
    for (const id of [mapping.sessionId, ...knownAliases(mapping)]) {
      if (mappedIdentifiers.has(id) && mappedIdentifiers.get(id) !== mapping.sessionId) fail('Native identifiers collide across Ralph mappings.');
      mappedIdentifiers.set(id, mapping.sessionId);
    }
  }
  const supplied = new Map();
  for (const candidate of evidence.candidates) {
    const id = candidate?.sessionId;
    if (!uuidPattern.test(id ?? '') || supplied.has(id)) fail('Cleanup candidates need unique native session IDs.');
    const aliases = candidate.aliases ?? [];
    if (!Array.isArray(aliases) || aliases.some((alias) => !uuidPattern.test(alias ?? '') || alias === id)) {
      fail('Cleanup aliases must be distinct native UUIDs.');
    }
    for (const alias of aliases) {
      if (mappedIdentifiers.has(alias) && mappedIdentifiers.get(alias) !== id) fail('Cleanup alias matches a different Ralph mapping or substitutes for a recorded ID.');
      if (identifiers.has(alias) && identifiers.get(alias) !== id) fail('Cleanup alias matches another recorded deletion.');
    }
    const record = bySession.get(id);
    if (record?.status === 'deleted' || (identifiers.has(id) && identifiers.get(id) !== id)) {
      fail(`Deleted Ralph worker ${id} reappeared live; reconcile ownership before any cleanup or readiness.`);
    }
    if (record && aliases.some((alias) => !record.aliases.includes(alias))) {
      fail('A pending deletion cannot gain new aliases; inspect the recorded identifiers.');
    }
    supplied.set(id, { ...candidate, aliases: [...new Set([...aliases, ...(mappingBySession.get(id) ? knownAliases(mappingBySession.get(id).mapping) : [])])] });
  }
  const eligible = [], retained = [], pending = [], deleted = [];
  for (const [id, candidate] of supplied) {
    if (!mappingBySession.has(id)) {
      retained.push({ sessionId: id, reasons: [mappedIdentifiers.has(id)
        ? 'an alias cannot substitute for the recorded session ID'
        : rolePrefix.test(candidate.live?.name ?? '')
          ? 'role session is never deleted' : 'unmapped session is not owned Ralph worker work'] });
    }
  }
  const roundIntents = [...bySession.values()].filter((record) => record.roundId === roundId).length;
  for (const [id, { correlation, mapping }] of mappingBySession) {
    const record = bySession.get(id);
    if (record?.status === 'deleted') { deleted.push({ sessionId: id, assignmentId: mapping.assignmentId, confirmedAt: record.confirmedAt }); continue; }
    if (record) {
      pending.push({ sessionId: id, assignmentId: mapping.assignmentId, aliases: record.aliases, worktreePath: record.worktreePath, reasons: ['unconfirmed deletion: inspect get_session and the worktree with record-deletion-result; never retry delete_item'] });
      continue;
    }
    const proof = terminalProof(state, journal, correlation, mapping);
    if (proof.reason) { retained.push({ sessionId: id, assignmentId: mapping.assignmentId, reasons: [proof.reason] }); continue; }
    if (id === evidence.callingSessionId || (supplied.get(id)?.aliases ?? []).includes(evidence.callingSessionId)) {
      retained.push({ sessionId: id, assignmentId: mapping.assignmentId, reasons: ['calling session is never deleted'] });
      continue;
    }
    const candidate = supplied.get(id);
    if (!candidate) { retained.push({ sessionId: id, assignmentId: mapping.assignmentId, reasons: ['no fresh cleanup evidence supplied'] }); continue; }
    const result = await evaluate(config, candidate, mapping, correlation, proof, { evidence, now, readArtifact });
    (result.reasons.length ? retained : eligible).push(result);
  }
  eligible.sort((left, right) => Date.parse(left.settledAt) - Date.parse(right.settledAt) || left.sessionId.localeCompare(right.sessionId));
  const allowance = Math.max(0, max - roundIntents);
  for (const item of eligible.splice(allowance)) retained.push({ ...item, reasons: ['per-round deletion bound reached'] });
  return { dispatchAuthorized: false, nativeCreateAllowed: false, deleteAllowed: false,
    maxDeletions: max, eligible: eligible.map(({ reasons, ...item }) => item), retained, pending, deleted };
}

export async function recordDeletionIntent({ request, ...context }) {
  requireCleanupRole(context.config);
  const sessionId = request.data?.sessionId;
  const { journal, state } = context;
  const existing = journal.deletions?.[sessionId];
  if (existing) {
    deletionLedger(journal, state);
    fail(existing.status === 'deleted'
      ? 'Session deletion is already confirmed; never delete it again.'
      : 'A deletion intent is already pending for this session; inspect with record-deletion-result, never retry delete_item.');
  }
  const plan = await planWorkerCleanup({ ...context, roundId: request.roundId });
  const item = plan.eligible.find((entry) => entry.sessionId === sessionId);
  if (!item) fail('Session is not eligible for deletion in a fresh cleanup plan.');
  journal.deletions ??= {};
  journal.deletions[sessionId] = {
    status: 'pending', sessionId, aliases: item.aliases, correlation: item.correlation,
    assignmentId: item.assignmentId, terminalEvidenceDigest: item.terminalEvidenceDigest,
    worktreePath: item.worktreePath, roundId: request.roundId, intentRequestId: request.id,
    intentAt: new Date(context.now).toISOString(), planEvidenceDigest: digest(context.evidence), inspections: [],
  };
  return { dispatchAuthorized: false, nativeCreateAllowed: false, deleteAllowed: true,
    nativeTool: 'delete_item', nativeArguments: { id: sessionId }, sessionId, aliases: item.aliases,
    worktreePath: item.worktreePath,
    message: 'Call delete_item exactly once, then record-deletion-result. A lost or failed response NEVER authorizes another delete_item.' };
}

export function recordDeletionResult({ config, request, evidence, journal, state, now }) {
  requireCleanupRole(config);
  const sessionId = request.data?.sessionId;
  deletionLedger(journal, state);
  const record = journal.deletions?.[sessionId];
  if (!record) fail('No recorded deletion intent for this session.');
  if (record.status === 'deleted') {
    return { dispatchAuthorized: false, deleteAllowed: false, confirmed: true, alreadyRecorded: true, sessionId };
  }
  if (!Array.isArray(evidence.lookups)) fail('Post-delete get_session lookups are required.');
  const lookups = new Map();
  for (const lookup of evidence.lookups) {
    if (!uuidPattern.test(lookup?.id ?? '') || lookups.has(lookup.id) || typeof lookup.notFound !== 'boolean') {
      fail('Each post-delete lookup needs a unique native ID and an explicit notFound result.');
    }
    lookups.set(lookup.id, lookup.notFound);
  }
  const identifiers = [sessionId, ...record.aliases];
  const confirmed = identifiers.every((id) => lookups.get(id) === true) &&
    evidence.worktree?.path === record.worktreePath && evidence.worktree.absent === true;
  const inspection = { observedAt: evidence.observedAt, evidenceDigest: digest(evidence), requestId: request.id,
    deleteOutcome: evidence.deleteOutcome, confirmed };
  if (!confirmed) {
    record.inspections = [...(record.inspections ?? []), inspection].slice(-20);
    return { dispatchAuthorized: false, deleteAllowed: false, confirmed: false, pending: true, sessionId,
      stillPresent: identifiers.filter((id) => lookups.get(id) === false),
      unchecked: identifiers.filter((id) => !lookups.has(id)),
      worktreeAbsent: evidence.worktree?.path === record.worktreePath && evidence.worktree.absent === true,
      message: 'Deletion is unconfirmed and remains pending. Inspect again on a later round; never retry delete_item.' };
  }
  record.status = 'deleted';
  record.confirmedAt = new Date(now).toISOString();
  record.resultEvidenceDigest = inspection.evidenceDigest;
  record.inspections = [...(record.inspections ?? []), inspection].slice(-20);
  return { dispatchAuthorized: false, deleteAllowed: false, confirmed: true, sessionId };
}
