import { execFile } from 'node:child_process';
import { lstat, realpath } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { digest } from './ralph-mailbox.mjs';
import { assessCleanupCandidate } from './ralph-round-cache.mjs';

// Owning-consumer deletion of its own settled Ralph workers (#2954). The plan is
// read-only; deletion intent is journaled before delete_item and a retirement is
// recorded only after get_session shows every known identifier not found, or
// archived with no path and resolving to the recorded session (#2956: native
// delete_item archives worktree sessions), plus an absent worktree directory.
// Uncertain results stay pending and are never retried.

export const reapSettleMs = 15 * 60 * 1000;
export const defaultMaxDeletions = 5;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const digestPattern = /^[0-9a-f]{64}$/;
const rolePrefix = /^\s*(ralph|reaper)/i;
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
  const bound = mapping.terminalEvidence;
  if (!bound || digest(bound) !== assignment.terminalCommitment) {
    return { reason: 'terminal session binding is not retained (mapping predates #2954); clean up manually' };
  }
  if (bound.session?.id !== mapping.sessionId || bound.session?.worktreePath !== mapping.worktreePath ||
      bound.assignmentCorrelation !== correlation) {
    return { reason: 'retained terminal evidence names a different session, worktree or correlation' };
  }
  const identity = bound.runtimeIdentity;
  if (!identity || identity.sessionId !== mapping.sessionId || !Array.isArray(identity.aliases) ||
      JSON.stringify(identity) !== JSON.stringify(terminalIdentity(mapping, mapping.sessionId))) {
    return { reason: 'mapping identifiers differ from the terminal commitment; clean up manually' };
  }
  const settledFrom = Date.parse(receipt.observedAt);
  if (!Number.isFinite(settledFrom)) return { reason: 'terminal receipt time is unknown' };
  return { assignment, receipt, terminalEvidenceDigest: mapping.lastEvidenceDigest, settledFrom };
}

function knownAliases(mapping, sessionId = mapping.sessionId) {
  return [...new Set([...(mapping.sessionAliases ?? []), mapping.creationHandle]
    .filter((id) => uuidPattern.test(id ?? '') && id !== sessionId))].sort();
}

// Committed into the terminal receipt digest at terminal-reported time.
export function terminalIdentity(mapping, sessionId) {
  return { sessionId, creationHandle: mapping.creationHandle ?? null, aliases: knownAliases(mapping, sessionId) };
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
    if (proof.reason || proof.terminalEvidenceDigest !== record.terminalEvidenceDigest ||
        mapping.terminalEvidence.runtimeIdentity.aliases.some((alias) => !record.aliases.includes(alias))) {
      fail('Retained deletion record lacks matching settled terminal evidence.');
    }
    if (record.worktreePath !== mapping.worktreePath || !Number.isFinite(Date.parse(record.intentAt)) ||
        !digestPattern.test(record.planEvidenceDigest ?? '') || typeof record.intentRequestId !== 'string') {
      fail('Retained deletion record intent is incomplete.');
    }
    if (record.status === 'deleted' && !confirmationProven(record)) {
      fail('Retained deletion record lacks recomputable not-found/archived and absent-worktree confirmation.');
    }
    bySession.set(sessionId, record);
    for (const id of [sessionId, ...record.aliases]) claim(id, sessionId);
  }
  return { bySession, identifiers };
}

// Per-identifier post-delete outcome. 'deleted': get_session not found with no
// contradicting facts. 'archived': found, archived, empty/absent path and resolving
// to the recorded session. Anything else (live, a path, another session, unknown
// or contradictory facts) is 'unconfirmed'.
export function lookupOutcome(lookup, sessionId) {
  const pathEmpty = lookup?.path === undefined || lookup.path === null || lookup.path === '';
  if (lookup?.notFound === true) {
    return lookup.archived !== true && pathEmpty && (lookup.resolvedId === undefined || lookup.resolvedId === null)
      ? 'deleted' : 'unconfirmed';
  }
  if (lookup?.notFound === false && lookup.archived === true && pathEmpty && lookup.resolvedId === sessionId) return 'archived';
  return 'unconfirmed';
}

const retirementOutcome = (outcomes) => (outcomes.every((outcome) => outcome === 'deleted') ? 'deleted' : 'archived');

function confirmationProven(record) {
  const proof = record.confirmation;
  const lookups = Array.isArray(proof?.lookups) ? proof.lookups : [];
  // Legacy #2955 confirmations carry no outcomes; newer ones must store exactly the recomputed ones.
  const outcomes = [record.sessionId, ...record.aliases].map((id) => {
    const matches = lookups.filter((lookup) => lookup?.id === id);
    if (matches.length !== 1) return 'unconfirmed';
    const outcome = lookupOutcome(matches[0], record.sessionId);
    return matches[0].outcome === (proof.outcome === undefined ? undefined : outcome) ? outcome : 'unconfirmed';
  });
  return Boolean(proof) && digest(proof) === record.resultEvidenceDigest &&
    Date.parse(record.confirmedAt) >= Date.parse(record.intentAt) &&
    Date.parse(proof.observedAt) >= Date.parse(record.intentAt) &&
    proof.worktree?.path === record.worktreePath && proof.worktree.absent === true && proof.runtimeWorktreeAbsent === true &&
    outcomes.every((outcome) => outcome !== 'unconfirmed') &&
    (proof.outcome ?? 'deleted') === retirementOutcome(outcomes);
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

const repository = 'OlyForge3D/PrintFarmer';
const shaPattern = /^[0-9a-f]{40}$/;
const branchPattern = /^[A-Za-z0-9][A-Za-z0-9._/-]*$/;
const fold = (value) => value.normalize('NFC').toLowerCase();
const git = promisify(execFile);

// Runtime-owned local readback of the recorded worktree. Canonical paths defeat
// symlink/case aliases; fsmonitor and hooks are disabled so a worker-controlled
// config cannot run code during inspection.
export const defaultCleanupProbe = {
  async canonical(target) {
    try { return await realpath(target); } catch { return undefined; }
  },
  async absent(target) {
    try { await lstat(target); return false; } catch (error) { return error.code === 'ENOENT'; }
  },
  async worktree(target) {
    const canonicalPath = await defaultCleanupProbe.canonical(target);
    if (!canonicalPath) return { exists: false };
    const gitPresent = await lstat(path.join(target, '.git')).then(() => true, () => false);
    if (!gitPresent) return { exists: true, canonicalPath, gitPresent };
    const run = async (args) => (await git('git', ['-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=/dev/null',
      '-C', target, ...args], { encoding: 'utf8', timeout: 60_000, maxBuffer: 8 * 1024 * 1024 })).stdout;
    try {
      const [headSha, branch, porcelain] = await Promise.all([run(['rev-parse', '--verify', 'HEAD']),
        run(['symbolic-ref', '--quiet', '--short', 'HEAD']), run(['status', '--porcelain=v1', '--untracked-files=all'])]);
      return { exists: true, canonicalPath, gitPresent, headSha: headSha.trim(), branch: branch.trim(), porcelain };
    } catch { return { exists: true, canonicalPath, gitPresent, gitFailed: true }; }
  },
};

const within = (root, target) => {
  const relative = path.relative(root, target);
  return Boolean(relative) && !relative.startsWith('..') && !path.isAbsolute(relative);
};

// Canonical containment: inside the configured worktree root with no symlink
// redirection, and never the main checkout, its ancestor or its descendant.
async function pathReasons(config, worktreePath, facts, evidence, probe) {
  const root = await probe.canonical(config.worktreeRoot);
  const main = await probe.canonical(evidence.mainCheckoutPath) ?? evidence.mainCheckoutPath;
  const actual = facts.canonicalPath;
  if (!root || !actual) return ['canonical worktree path is unknown'];
  const [froot, fmain, factual] = [fold(root), fold(main), fold(actual)];
  const overlapsMain = (value) => value === fmain || within(value, fmain) || within(fmain, value);
  if (!within(froot, factual) || path.relative(froot, factual) !== path.relative(fold(config.worktreeRoot), fold(worktreePath)) ||
      overlapsMain(factual) || overlapsMain(fold(worktreePath)) || fold(worktreePath) === fold(evidence.mainCheckoutPath)) {
    return ['canonical worktree path escapes the isolated worktree root or aliases the main checkout'];
  }
  return [];
}

// Runtime-owned GitHub readback bound to this repository, branch and local HEAD.
async function prFacts(api, branch, headSha) {
  const pulls = await api(`repos/${repository}/pulls?head=${encodeURIComponent(`OlyForge3D:${branch}`)}&state=all&per_page=100`);
  const refs = await api(`repos/${repository}/git/matching-refs/heads/${branch}`);
  if (!Array.isArray(pulls) || !Array.isArray(refs)) throw new Error('unexpected GitHub response');
  const prs = pulls.filter((pr) => pr?.head?.ref === branch && pr?.head?.repo?.full_name === repository);
  if (prs.length !== pulls.length) throw new Error('PR lookup returned another head');
  const remote = refs.find((ref) => ref?.ref === `refs/heads/${branch}`)?.object?.sha;
  const known = async (sha) => {
    try { return await api(`repos/${repository}/compare/development...${sha}`); } catch { return undefined; }
  };
  const contained = async (sha) => {
    try { return ['identical', 'ahead'].includes((await api(`repos/${repository}/compare/${sha}...development`))?.status); }
    catch { return false; }
  };
  return { prs, remote, local: await known(headSha), contained };
}

async function prReasons(candidate, proof, facts, context) {
  const reasons = [];
  let pr;
  let clock = proof.settledFrom;
  let github;
  try { github = await prFacts(context.api, facts.branch, facts.headSha); }
  catch { return { reasons: ['runtime PR/branch lookup by head branch failed'] }; }
  const pushed = github.remote ? github.remote === facts.headSha : Boolean(github.local);
  if (!pushed) reasons.push('WARNING: unpushed commits or unknown upstream');
  const done = (result) => ({ pushed, ...result });
  const { prs } = github;
  if (prs.some((item) => item.state === 'open')) return done({ reasons: ['open PR is never deleted'] });
  if (prs.length > 1) return done({ reasons: ['multiple PRs for the branch need human review'] });
  if (prs.length === 1) {
    const item = prs[0];
    pr = { state: item.merged_at ? 'MERGED' : 'CLOSED', closureReason: candidate.closureReason };
    const at = Date.parse(item.merged_at ?? item.closed_at);
    if (!Number.isFinite(at) || at > context.now) reasons.push('PR merge/close time is unknown');
    else clock = Math.max(clock, at);
    const preserved = item.head?.sha === facts.headSha && (!github.remote || github.remote === item.head.sha);
    pr.headPreservedAfterMerge = preserved;
    pr.commitsAfterMerge = preserved ? [] : ['local or remote head differs from the PR head'];
    if (pr.state === 'MERGED') {
      pr.mergeCommitOnDevelopment = shaPattern.test(item.merge_commit_sha ?? '') && await github.contained(item.merge_commit_sha);
    } else if (!github.remote) reasons.push('origin branch for closed PR is absent');
  } else if (!Number.isInteger(github.local?.ahead_by)) {
    if (pushed) reasons.push('commits ahead of origin/development are unknown');
  } else if (github.local.ahead_by > 0) {
    reasons.push('no PR with pushed commits needs human review');
  } else if (!['research', 'analysis'].includes(proof.assignment.task?.purpose)) {
    reasons.push('no-PR work without commits is deletable only for research/analysis');
  }
  return done({ reasons, pr, clock });
}

async function evaluate(config, candidate, mapping, correlation, proof, context) {
  const { evidence, now, readArtifact, probe } = context;
  const reasons = [];
  const live = candidate.live ?? {};
  if (live.found !== true) reasons.push('fresh get_session readback is missing');
  if (config.projectId && live.projectId !== config.projectId) reasons.push('live session is not in the configured project');
  if (typeof live.name !== 'string' || rolePrefix.test(live.name)) reasons.push('role-named or unnamed session is never deleted');
  for (const [flag, reason] of [['busy', 'session is busy or activity is unknown'],
    ['pendingInput', 'session has pending input or it is unknown'],
    ['agentMerge', 'Agent merge is active or unknown'], ['automation', 'session automation is attached or unknown']]) {
    if (live[flag] !== false) reasons.push(reason);
  }
  const worktreePath = mapping.worktreePath;
  if (!withinRoot(config.worktreeRoot, worktreePath) || live.worktreePath !== worktreePath) {
    reasons.push('worktree path does not match the recorded isolated worker worktree');
  }
  const facts = await probe.worktree(worktreePath);
  if (facts.exists !== true || facts.gitPresent !== true) reasons.push('worktree or its .git is missing');
  else reasons.push(...await pathReasons(config, worktreePath, facts, evidence, probe));
  const gitKnown = facts.gitPresent === true && !facts.gitFailed && shaPattern.test(facts.headSha ?? '') &&
    branchPattern.test(facts.branch ?? '') && !/(?:\.\.|\/\/|\.lock(?:\/|$)|\/$|\.$)/.test(facts.branch) &&
    typeof facts.porcelain === 'string';
  if (facts.gitPresent === true && !gitKnown) reasons.push('runtime git readback of the worktree failed');
  if (gitKnown && live.branch !== facts.branch) reasons.push('worker branch readback does not match');
  const clean = gitKnown && facts.porcelain === '';
  let pr, clock, pushed = false;
  if (gitKnown) {
    const result = await prReasons(candidate, proof, facts, context);
    reasons.push(...result.reasons);
    ({ pr, clock } = result);
    pushed = result.pushed === true;
  }
  let noPrDeliverable = { completed: false, verified: false };
  if (gitKnown && !pr && !reasons.length) {
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
    worktree: { inspected: gitKnown, dirty: !clean, untracked: !clean },
    finalReport: { workingTreeClean: clean, allCommitsPushed: pushed,
      closedWithoutMerge: pr?.state === 'CLOSED', closureReason: pr?.closureReason },
    settledAt: Number.isFinite(clock) ? new Date(clock).toISOString() : undefined,
    pr: pr && { state: pr.state, mergeCommitVerifiedOnDevelopment: pr.mergeCommitOnDevelopment === true,
      headPreservedAfterMerge: pr.headPreservedAfterMerge === true, linkedIssueDispositionVerified: true,
      commitsAfterMergeKnown: true, commitsAfterMerge: pr.commitsAfterMerge },
    noPrDeliverable,
  }, { now, settlingMs: reapSettleMs });
  for (const reason of assessment.reasons) if (!reasons.includes(reason)) reasons.push(reason);
  return {
    sessionId: mapping.sessionId, aliases: candidate.aliases, assignmentId: mapping.assignmentId, correlation,
    terminalEvidenceDigest: proof.terminalEvidenceDigest, worktreePath, headSha: facts.headSha,
    settledAt: Number.isFinite(clock) ? new Date(clock).toISOString() : undefined, reasons,
  };
}

// Read-only. Every mapped worker of this consumer is either eligible, retained
// with reasons, pending an unconfirmed deletion, or already deleted.
export async function planWorkerCleanup({ config, evidence, journal, state, now, readArtifact, roundId, api,
  probe = defaultCleanupProbe }) {
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
    if (record?.status === 'deleted') {
      deleted.push({ sessionId: id, assignmentId: mapping.assignmentId, confirmedAt: record.confirmedAt,
        outcome: record.confirmation.outcome ?? 'deleted' });
      continue;
    }
    if (record) {
      pending.push({ ...pendingSummary(record), reasons: ['unconfirmed deletion: inspect get_session and the worktree with record-deletion-result; never retry delete_item'] });
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
    const result = await evaluate(config, candidate, mapping, correlation, proof, { evidence, now, readArtifact, api, probe });
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

export function pendingSummary(record) {
  return { sessionId: record.sessionId, assignmentId: record.assignmentId, aliases: record.aliases,
    worktreePath: record.worktreePath, intentAt: record.intentAt };
}

export async function recordDeletionResult({ config, request, evidence, journal, state, now, probe = defaultCleanupProbe }) {
  requireCleanupRole(config);
  const sessionId = request.data?.sessionId;
  deletionLedger(journal, state);
  const record = journal.deletions?.[sessionId];
  if (!record) fail('No recorded deletion intent for this session.');
  if (record.status === 'deleted') {
    return { dispatchAuthorized: false, deleteAllowed: false, confirmed: true, alreadyRecorded: true, sessionId,
      outcome: record.confirmation.outcome ?? 'deleted' };
  }
  if (!Array.isArray(evidence?.lookups)) fail('Post-delete get_session lookups are required.');
  const observed = Date.parse(evidence.observedAt);
  if (!Number.isFinite(observed) || observed < Date.parse(record.intentAt) || observed > now + 5 * 60_000) {
    fail('Post-delete evidence needs an observedAt time after the recorded intent.');
  }
  const lookups = new Map();
  for (const lookup of evidence.lookups) {
    if (!uuidPattern.test(lookup?.id ?? '') || lookups.has(lookup.id) || typeof lookup.notFound !== 'boolean' ||
        (lookup.archived !== undefined && typeof lookup.archived !== 'boolean') ||
        (lookup.path !== undefined && lookup.path !== null && typeof lookup.path !== 'string') ||
        (lookup.resolvedId !== undefined && lookup.resolvedId !== null && !uuidPattern.test(lookup.resolvedId))) {
      fail('Each post-delete lookup needs a unique native ID, an explicit notFound result and, when found, boolean archived, string path and UUID resolvedId.');
    }
    lookups.set(lookup.id, { id: lookup.id, notFound: lookup.notFound, archived: lookup.archived ?? null,
      path: lookup.path ?? null, resolvedId: lookup.resolvedId ?? null });
  }
  const identifiers = [sessionId, ...record.aliases];
  const outcomes = new Map(identifiers.filter((id) => lookups.has(id))
    .map((id) => [id, lookupOutcome(lookups.get(id), sessionId)]));
  const retired = identifiers.every((id) => outcomes.has(id) && outcomes.get(id) !== 'unconfirmed');
  const runtimeWorktreeAbsent = await probe.absent(record.worktreePath);
  const confirmation = { observedAt: evidence.observedAt, source: evidence.source,
    lookups: identifiers.map((id) => (lookups.has(id) ? { ...lookups.get(id), outcome: outcomes.get(id) } : { id, outcome: 'unchecked' })),
    worktree: { path: evidence.worktree?.path, absent: evidence.worktree?.absent === true },
    runtimeWorktreeAbsent, deleteOutcome: evidence.deleteOutcome,
    outcome: retired ? retirementOutcome([...outcomes.values()]) : 'unconfirmed' };
  const confirmed = retired &&
    evidence.worktree?.path === record.worktreePath && evidence.worktree.absent === true && runtimeWorktreeAbsent === true;
  const inspection = { observedAt: evidence.observedAt, evidenceDigest: digest(confirmation), requestId: request.id,
    deleteOutcome: evidence.deleteOutcome, runtimeWorktreeAbsent, outcome: confirmation.outcome, confirmed };
  if (!confirmed) {
    record.inspections = [...(record.inspections ?? []), inspection].slice(-20);
    return { dispatchAuthorized: false, deleteAllowed: false, confirmed: false, pending: true, sessionId,
      stillPresent: identifiers.filter((id) => outcomes.get(id) === 'unconfirmed'),
      unchecked: identifiers.filter((id) => !lookups.has(id)),
      worktreeAbsent: evidence.worktree?.path === record.worktreePath && evidence.worktree.absent === true && runtimeWorktreeAbsent,
      message: 'Deletion is unconfirmed and remains pending. Inspect again on a later round; never retry delete_item.' };
  }
  record.status = 'deleted';
  record.confirmedAt = new Date(now).toISOString();
  record.confirmation = confirmation;
  record.resultEvidenceDigest = digest(confirmation);
  record.inspections = [...(record.inspections ?? []), inspection].slice(-20);
  return { dispatchAuthorized: false, deleteAllowed: false, confirmed: true, sessionId, outcome: confirmation.outcome };
}
