import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { digest } from '../ralph-mailbox.mjs';
import {
  defaultCleanupProbe, deletedWorkerIds, terminalIdentity, planWorkerCleanup, recordDeletionIntent, recordDeletionResult, reapSettleMs,
} from '../ralph-native-cleanup.mjs';

const now = Date.parse('2026-09-01T12:00:00Z');
const iso = (value) => new Date(value).toISOString();
const settledAt = iso(now - 60 * 60 * 1000);
const projectId = 'bbbbbbbb-1111-4222-8333-444444444444';
const config = { role: 'consumer', workerId: 'mini', host: 'macos-mobile', worktreeRoot: '/worktrees', projectId };
const calling = 'aaaaaaaa-0000-4000-8000-000000000000';
const id = (n) => `cccccccc-1111-4222-8333-${String(n).padStart(12, '0')}`;
const alias = (n) => `dddddddd-1111-4222-8333-${String(n).padStart(12, '0')}`;
const sha = (label) => digest(label).slice(0, 40);
const artifactUrl = (n) => `https://github.com/OlyForge3D/PrintFarmer/issues/${900 + n}#issuecomment-${n}`;
const repo = 'OlyForge3D/PrintFarmer';
// Fixture-only facts read by the fake git/GitHub probes. Symbol keys never reach
// the runtime's evidence digest, proving the runtime derives these facts itself.
const FACTS = Symbol('facts');

function world(workers) {
  const state = { assignments: {} }, journal = { sessions: {} };
  for (const worker of workers) {
    const { n } = worker, correlation = `delivery-${n}`;
    const mapping = { assignmentId: `assignment-${n}`, sessionId: id(n), worktreePath: `/worktrees/w-${n}`, ...worker.mapping };
    const terminalEvidence = { assignmentCorrelation: correlation,
      session: { id: id(n), projectId, worktreePath: `/worktrees/w-${n}`, terminalVerified: true },
      runtimeIdentity: terminalIdentity(mapping, id(n)) };
    const evidenceDigest = digest(terminalEvidence);
    state.assignments[`assignment-${n}`] = {
      assignmentId: `assignment-${n}`, workerId: worker.workerId ?? 'mini', state: worker.state ?? 'terminal',
      task: { purpose: worker.purpose ?? 'implementation', issue: 900 + n }, terminalCommitment: evidenceDigest,
      receipts: [{ status: 'terminal-reported', correlation, evidenceDigest, observedAt: worker.receiptAt ?? settledAt }],
    };
    journal.sessions[correlation] = { ...mapping, lastEvidenceDigest: evidenceDigest, terminalEvidence };
  }
  return { state, journal, present: new Set() };
}

function candidate(n, { live = {}, worktree = {}, pr = {}, prs, prsChecked = true, ...rest } = {}) {
  const facts = { n, prsChecked, worktree: { branch: `b-${n}`, exists: true, gitPresent: true, porcelainEmpty: true,
    unpushedCommits: 0, commitsAheadOfDevelopment: 2, remoteBranchExists: true, ...worktree },
  prs: prs ?? [{ number: 10 + n, state: 'MERGED', mergedAt: settledAt, mergeCommitOnDevelopment: true,
    headPreservedAfterMerge: true, commitsAfterMerge: [], ...pr }] };
  return {
    sessionId: id(n),
    live: { found: true, name: `lambert implementation ${n}`, projectId, worktreePath: `/worktrees/w-${n}`, branch: `b-${n}`,
      busy: false, pendingInput: false, agentMerge: false, automation: false, ...live },
    ...(pr.closureReason ? { closureReason: pr.closureReason } : {}), ...rest, [FACTS]: facts,
  };
}

const readArtifact = async (assignment, url) => {
  if (url !== artifactUrl(assignment.task.issue - 900)) throw new Error('fixture readback failed');
  return { kind: 'issue-comment', url, bodyDigest: digest(`body-${url}`), bodyBytes: 4 };
};

// Fake local git/filesystem and GitHub, derived only from fixture facts.
function fakes(w, candidates, links = {}) {
  const byPath = new Map(), byBranch = new Map();
  for (const supplied of candidates) {
    const facts = supplied[FACTS];
    const mapping = Object.values(w.journal.sessions).find((entry) => entry.sessionId === supplied.sessionId);
    if (!facts || !mapping) continue;
    byPath.set(mapping.worktreePath, facts);
    byBranch.set(facts.worktree.branch, facts);
  }
  const head = (facts) => sha(`head-${facts.n}`);
  const probe = {
    canonical: async (target) => links[target] ?? target,
    absent: async (target) => !w.present.has(target),
    worktree: async (target) => {
      const facts = byPath.get(target);
      if (!facts || facts.worktree.exists === false) return { exists: false };
      if (facts.worktree.gitPresent === false) return { exists: true, canonicalPath: links[target] ?? target, gitPresent: false };
      return { exists: true, canonicalPath: links[target] ?? target, gitPresent: true, headSha: head(facts),
        branch: facts.worktree.branch, porcelain: facts.worktree.porcelainEmpty ? '' : ' M src/file.ts\n?? new.txt\n' };
    },
  };
  const api = async (endpoint) => {
    let match = endpoint.match(/^repos\/OlyForge3D\/PrintFarmer\/pulls\?head=([^&]+)&state=all&per_page=100$/);
    if (match) {
      const facts = byBranch.get(decodeURIComponent(match[1]).replace(/^OlyForge3D:/, ''));
      if (!facts || !facts.prsChecked) throw new Error('fixture PR lookup failed');
      return facts.prs.map((item) => ({
        number: item.number, state: item.state === 'OPEN' ? 'open' : 'closed',
        merged_at: item.state === 'MERGED' ? item.mergedAt : null,
        closed_at: item.state === 'OPEN' ? null : (item.closedAt ?? item.mergedAt),
        merge_commit_sha: item.state === 'MERGED' ? sha(`merge-${facts.n}`) : null,
        head: { ref: item.headRef ?? facts.worktree.branch, sha: item.headPreservedAfterMerge === false || item.commitsAfterMerge?.length
          ? sha(`other-${facts.n}`) : head(facts), repo: { full_name: item.headRepository ?? repo } },
        mergeCommitOnDevelopment: item.mergeCommitOnDevelopment,
      }));
    }
    match = endpoint.match(/^repos\/OlyForge3D\/PrintFarmer\/git\/matching-refs\/heads\/(.+)$/);
    if (match) {
      const facts = byBranch.get(match[1]);
      if (!facts?.worktree.remoteBranchExists) return [];
      return [{ ref: `refs/heads/${match[1]}`, object: { sha: facts.worktree.unpushedCommits ? sha(`remote-${facts.n}`) : head(facts) } }];
    }
    match = endpoint.match(/^repos\/OlyForge3D\/PrintFarmer\/compare\/development\.\.\.([0-9a-f]{40})$/);
    if (match) {
      const facts = [...byBranch.values()].find((entry) => head(entry) === match[1]);
      if (!facts || facts.worktree.unpushedCommits) throw new Error('unknown commit');
      const ahead = facts.worktree.commitsAheadOfDevelopment;
      return { status: ahead > 0 ? 'ahead' : 'behind', ahead_by: ahead };
    }
    match = endpoint.match(/^repos\/OlyForge3D\/PrintFarmer\/compare\/([0-9a-f]{40})\.\.\.development$/);
    if (match) {
      const facts = [...byBranch.values()].find((entry) => sha(`merge-${entry.n}`) === match[1]);
      return { status: facts?.prs.some((item) => item.mergeCommitOnDevelopment) ? 'ahead' : 'diverged' };
    }
    throw new Error(`unexpected fixture endpoint ${endpoint}`);
  };
  return { probe, api };
}

function context(w, candidates, extra = {}, overrides = {}) {
  return { config, journal: w.journal, state: w.state, now, readArtifact, roundId: 'round-1',
    ...fakes(w, candidates, overrides.links),
    evidence: { observedAt: iso(now), source: 'fixture get_session readback', callingSessionId: calling,
      mainCheckoutPath: '/Volumes/data/src/pfarm1', candidates, ...extra }, ...overrides };
}

const request = (n, type, extra = {}) => ({ id: `${type}-${n}-${Math.random()}`, roundId: 'round-1', type,
  data: { sessionId: id(n) }, ...extra });
const confirmedLookup = (n, aliases = []) => ({ observedAt: iso(now), source: 'fixture get_session',
  lookups: [id(n), ...aliases].map((value) => ({ id: value, notFound: true })),
  worktree: { path: `/worktrees/w-${n}`, absent: true }, deleteOutcome: 'succeeded' });

test('a settled clean merged worker is planned and deleted exactly once', async () => {
  const w = world([{ n: 1 }]);
  const plan = await planWorkerCleanup(context(w, [candidate(1)]));
  assert.deepEqual(plan.eligible.map((item) => item.sessionId), [id(1)]);
  assert.equal(plan.deleteAllowed, false);
  assert.deepEqual(plan.retained, []);
  const authorized = [];
  const intent = await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  assert.equal(intent.deleteAllowed, true);
  assert.equal(intent.nativeTool, 'delete_item');
  assert.deepEqual(intent.nativeArguments, { id: id(1) });
  authorized.push(intent);
  assert.equal(w.journal.deletions[id(1)].status, 'pending');
  assert.equal(w.journal.deletions[id(1)].terminalEvidenceDigest, w.journal.sessions['delivery-1'].lastEvidenceDigest);
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }),
    /already pending.*never retry delete_item/);
  const result = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) });
  assert.equal(result.confirmed, true);
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) })).alreadyRecorded, true);
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }),
    /already confirmed; never delete it again/);
  const after = await planWorkerCleanup(context(w, []));
  assert.deepEqual(after.deleted.map((item) => item.sessionId), [id(1)]);
  assert.deepEqual(after.eligible, []);
  await assert.rejects(planWorkerCleanup(context(w, [candidate(1)])), /reappeared/);
  assert.equal(authorized.length, 1);
});

test('fail-closed cases are retained with explicit reasons', async () => {
  const cases = [
    ['dirty worktree', {}, candidate(1, { worktree: { porcelainEmpty: false } }), /tracked changes|untracked/],
    ['unpushed commits', {}, candidate(1, { worktree: { unpushedCommits: 1 } }), /unpushed/],
    ['busy session', {}, candidate(1, { live: { busy: true } }), /busy/],
    ['unknown activity', {}, candidate(1, { live: { busy: undefined } }), /busy or activity is unknown/],
    ['pending input', {}, candidate(1, { live: { pendingInput: true } }), /pending input/],
    ['Agent merge', {}, candidate(1, { live: { agentMerge: true } }), /Agent merge/],
    ['attached automation', {}, candidate(1, { live: { automation: true } }), /automation/],
    ['unsettled assignment', { state: 'terminal-reported' }, candidate(1), /not coordinator-settled/],
    ['running assignment', { state: 'running' }, candidate(1), /not coordinator-settled/],
    ['open PR', {}, candidate(1, { pr: { state: 'OPEN' } }), /open PR is never deleted/],
    ['merge within settle window', {}, candidate(1, { pr: { mergedAt: iso(now - reapSettleMs + 60_000) } }), /settling period has not elapsed/],
    ['receipt within settle window', { receiptAt: iso(now - 60_000) }, candidate(1), /settling period has not elapsed/],
    ['merge not on development', {}, candidate(1, { pr: { mergeCommitOnDevelopment: false } }), /origin\/development/],
    ['commits after merge', {}, candidate(1, { pr: { commitsAfterMerge: ['abc'] } }), /after PR merge/],
    ['missing .git', {}, candidate(1, { worktree: { gitPresent: false } }), /\.git is missing/],
    ['wrong worktree', {}, candidate(1, { live: { worktreePath: '/worktrees/other' } }), /recorded isolated worker worktree/],
    ['wrong project', {}, candidate(1, { live: { projectId: calling } }), /configured project/],
    ['wrong branch', {}, candidate(1, { live: { branch: 'other' } }), /branch readback/],
    ['missing worktree', {}, candidate(1, { worktree: { exists: false } }), /worktree or its \.git is missing/],
    ['glued Ralph name', {}, candidate(1, { live: { name: 'RalphConsumer' } }), /role-named/],
    ['glued Reaper name', {}, candidate(1, { live: { name: 'ReaperAutomation' } }), /role-named/],
    ['role-named mapped session', {}, candidate(1, { live: { name: 'Ralph consumer - mini' } }), /role-named/],
    ['Reaper-named mapped session', {}, candidate(1, { live: { name: 'Reaper round' } }), /role-named/],
    ['closed PR without remote branch', {}, candidate(1, { worktree: { remoteBranchExists: false },
      pr: { state: 'CLOSED', closedAt: settledAt, closureReason: 'superseded' } }), /origin branch/],
    ['closed PR without reason', {}, candidate(1, { pr: { state: 'CLOSED', closedAt: settledAt } }), /closed-without-merge reason/],
    ['no PR with pushed commits', {}, candidate(1, { prs: [] }), /human review/],
    ['no PR with unpushed commits', {}, candidate(1, { prs: [], worktree: { unpushedCommits: 2 } }), /WARNING/],
    ['no-PR implementation', {}, candidate(1, { prs: [], worktree: { commitsAheadOfDevelopment: 0 } }), /only for research/],
    ['multiple PRs', {}, candidate(1, { prs: [candidate(1)[FACTS].prs[0], { ...candidate(1)[FACTS].prs[0], number: 99 }] }), /multiple PRs/],
    ['incomplete PR lookup', {}, candidate(1, { prsChecked: false }), /lookup .*failed/],
    ['PR from another head repository', {}, candidate(1, { pr: { headRepository: 'fork/PrintFarmer' } }), /lookup .*failed/],
    ['PR for another head ref', {}, candidate(1, { pr: { headRef: 'other' } }), /lookup .*failed/],
    ['missing live readback', {}, candidate(1, { live: { found: false } }), /get_session readback is missing/],
  ];
  for (const [name, worker, supplied, reason] of cases) {
    const w = world([{ n: 1, ...worker }]);
    const plan = await planWorkerCleanup(context(w, [supplied]));
    assert.deepEqual(plan.eligible, [], name);
    const retained = plan.retained.find((item) => item.sessionId === id(1));
    assert.ok(retained?.reasons.some((item) => reason.test(item)), `${name}: ${JSON.stringify(retained)}`);
    await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [supplied]) }),
      /not eligible/, name);
    assert.equal(w.journal.deletions?.[id(1)], undefined, name);
  }
});

test('unmapped, role, calling and main-checkout sessions are never eligible', async () => {
  const w = world([{ n: 1 }]);
  const unmapped = candidate(7);
  const role = candidate(8, { live: { name: 'Ralph coordinator - mini - PrintFarmer' } });
  const glued = [candidate(9, { live: { name: 'RalphConsumer' } }), candidate(10, { live: { name: 'ReaperAutomation' } })];
  let plan = await planWorkerCleanup(context(w, [unmapped, role, ...glued]));
  assert.match(plan.retained.find((item) => item.sessionId === id(7)).reasons[0], /unmapped/);
  for (const n of [8, 9, 10]) assert.match(plan.retained.find((item) => item.sessionId === id(n)).reasons[0], /role session/);
  assert.match(plan.retained.find((item) => item.sessionId === id(1)).reasons[0], /no fresh cleanup evidence/);
  plan = await planWorkerCleanup(context(w, [candidate(1)], { callingSessionId: id(1) }));
  assert.match(plan.retained[0].reasons[0], /calling session/);
  for (const mainCheckoutPath of ['/worktrees/w-1', '/worktrees', '/worktrees/w-1/nested']) {
    plan = await planWorkerCleanup(context(w, [candidate(1)], { mainCheckoutPath }));
    assert.ok(plan.retained[0].reasons.some((reason) => /aliases the main checkout/.test(reason)), mainCheckoutPath);
  }
  const outside = world([{ n: 2, mapping: { worktreePath: '/Volumes/data/src/pfarm1' } }]);
  plan = await planWorkerCleanup(context(outside, [candidate(2, {
    live: { worktreePath: '/Volumes/data/src/pfarm1' }, worktree: { path: '/Volumes/data/src/pfarm1' } })]));
  assert.deepEqual(plan.eligible, []);
  const other = world([{ n: 3, workerId: 'windows' }]);
  await assert.rejects(planWorkerCleanup(context(other, [candidate(3)])), /unresolved assignment ownership/);
});

test('coordinator and non-macOS roles can never plan or record deletion', async () => {
  const w = world([{ n: 1 }]);
  for (const override of [{ role: 'coordinator' }, { host: 'windows-general' }]) {
    const bad = context(w, [candidate(1)], {}, { config: { ...config, ...override } });
    await assert.rejects(planWorkerCleanup(bad), /coordinator never deletes/);
    await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...bad }), /coordinator never deletes/);
    await assert.rejects(recordDeletionResult({ request: request(1, 'record-deletion-result'), ...bad,
      evidence: confirmedLookup(1) }), /coordinator never deletes/);
  }
  assert.equal(w.journal.deletions, undefined);
});

test('no-PR research is deletable only with a verified artifact and terminal settlement', async () => {
  const research = (extra = {}) => candidate(4, { prs: [], worktree: { commitsAheadOfDevelopment: 0 },
    artifactUrl: artifactUrl(4), ...extra });
  let w = world([{ n: 4, purpose: 'research' }]);
  assert.deepEqual((await planWorkerCleanup(context(w, [research()]))).eligible.map((item) => item.sessionId), [id(4)]);
  let plan = await planWorkerCleanup(context(w, [research({ artifactUrl: artifactUrl(5) })]));
  assert.ok(plan.retained[0].reasons.some((reason) => /artifact readback failed/.test(reason)));
  w = world([{ n: 4, purpose: 'research', mapping: { terminalArtifact: { url: artifactUrl(4), bodyDigest: digest('other') } } }]);
  plan = await planWorkerCleanup(context(w, [research()]));
  assert.ok(plan.retained[0].reasons.some((reason) => /no longer matches the retained terminal artifact/.test(reason)));
  w = world([{ n: 4, purpose: 'research', mapping: { terminalArtifact: { url: artifactUrl(4), bodyDigest: digest(`body-${artifactUrl(4)}`) } } }]);
  assert.equal((await planWorkerCleanup(context(w, [research()]))).eligible.length, 1);
  w = world([{ n: 4, purpose: 'research', state: 'terminal-reported' }]);
  assert.match((await planWorkerCleanup(context(w, [research()]))).retained[0].reasons[0], /not coordinator-settled/);
});

test('closed-unmerged PR with a retained remote branch is eligible after settling', async () => {
  const w = world([{ n: 1 }]);
  const closed = candidate(1, { pr: { state: 'CLOSED', closedAt: settledAt, closureReason: 'superseded by #2' } });
  assert.equal((await planWorkerCleanup(context(w, [closed]))).eligible.length, 1);
});

test('deletions are bounded per round and ordered oldest settled first', async () => {
  const workers = [1, 2, 3, 4, 5, 6, 7].map((n) => ({ n, receiptAt: iso(now - (20 + 10 * (8 - n)) * 60_000) }));
  const w = world(workers);
  const candidates = workers.map(({ n }) => candidate(n, { pr: { mergedAt: iso(now - (20 + 10 * (8 - n)) * 60_000) } }));
  const plan = await planWorkerCleanup(context(w, candidates));
  assert.deepEqual(plan.eligible.map((item) => item.sessionId), [1, 2, 3, 4, 5].map(id));
  assert.deepEqual(plan.retained.filter((item) => /bound/.test(item.reasons[0])).map((item) => item.sessionId), [6, 7].map(id));
  assert.equal((await planWorkerCleanup(context(w, candidates, { maxDeletions: 2 }))).eligible.length, 2);
  await assert.rejects(planWorkerCleanup(context(w, candidates, { maxDeletions: 6 })), /maxDeletions/);
  for (const n of [1, 2]) await recordDeletionIntent({ request: request(n, 'record-deletion-intent'), ...context(w, candidates) });
  const next = await planWorkerCleanup(context(w, candidates));
  assert.deepEqual(next.eligible.map((item) => item.sessionId), [3, 4, 5].map(id));
  assert.deepEqual(next.pending.map((item) => item.sessionId), [1, 2].map(id));
  await assert.rejects(recordDeletionIntent({ request: request(6, 'record-deletion-intent'), ...context(w, candidates) }), /not eligible/);
});

test('uncertain results stay pending, are inspected later and never re-authorize deletion', async () => {
  const w = world([{ n: 1 }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  const stillThere = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), lookups: [{ id: id(1), notFound: false }], deleteOutcome: 'unknown' } });
  assert.equal(stillThere.confirmed, false);
  assert.equal(stillThere.pending, true);
  assert.deepEqual(stillThere.stillPresent, [id(1)]);
  assert.equal(stillThere.deleteAllowed, false);
  const worktreeRemains = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), worktree: { path: '/worktrees/w-1', absent: false } } });
  assert.equal(worktreeRemains.confirmed, false);
  const wrongPath = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), worktree: { path: '/worktrees/other', absent: true } } });
  assert.equal(wrongPath.confirmed, false);
  assert.equal(w.journal.deletions[id(1)].status, 'pending');
  assert.equal(w.journal.deletions[id(1)].inspections.length, 3);
  const plan = await planWorkerCleanup(context(w, [candidate(1)]));
  assert.deepEqual(plan.eligible, []);
  assert.match(plan.pending[0].reasons[0], /never retry delete_item/);
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }), /already pending/);
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) })).confirmed, true);
  await assert.rejects(recordDeletionResult({ request: request(2, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(2) }), /No recorded deletion intent/);
});

test('project_session_id aliases must all be not-found, never match another mapping or substitute for the ID', async () => {
  const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }, { n: 2 }]);
  await assert.rejects(planWorkerCleanup(context(w, [candidate(1, { aliases: [id(2)] })])), /different Ralph mapping/);
  const byAlias = { ...candidate(1), sessionId: alias(1) };
  const plan = await planWorkerCleanup(context(w, [byAlias]));
  assert.match(plan.retained.find((item) => item.sessionId === alias(1)).reasons[0], /cannot substitute/);
  assert.equal(plan.eligible.length, 0);
  const suppliedAlias = alias(11);
  const intent = await recordDeletionIntent({ request: request(1, 'record-deletion-intent'),
    ...context(w, [candidate(1, { aliases: [suppliedAlias] })]) });
  assert.deepEqual(new Set(intent.aliases), new Set([alias(1), suppliedAlias]));
  await assert.rejects(planWorkerCleanup(context(w, [candidate(1, { aliases: [alias(12)] })])), /cannot gain new aliases/);
  const primaryOnly = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) });
  assert.equal(primaryOnly.confirmed, false);
  assert.deepEqual(new Set(primaryOnly.unchecked), new Set([alias(1), suppliedAlias]));
  const aliasLive = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), lookups: [{ id: id(1), notFound: true }, { id: alias(1), notFound: false },
      { id: suppliedAlias, notFound: true }] } });
  assert.deepEqual(aliasLive.stillPresent, [alias(1)]);
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1, [alias(1), suppliedAlias]) })).confirmed, true);
  await assert.rejects(planWorkerCleanup(context(w, [{ ...candidate(2), sessionId: suppliedAlias }])), /reappeared/);
  await assert.rejects(planWorkerCleanup(context(w, [candidate(2, { aliases: [alias(1)] })])), /another recorded deletion|different Ralph mapping/);
});

test('tampered deletion records fail closed', async () => {
  const w = world([{ n: 1 }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  for (const change of [{ terminalEvidenceDigest: digest('forged') }, { correlation: 'delivery-9' },
    { status: 'abandoned' }, { aliases: [id(1)] }]) {
    const saved = { ...w.journal.deletions[id(1)] };
    Object.assign(w.journal.deletions[id(1)], change);
    await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/);
    w.journal.deletions[id(1)] = saved;
  }
});

test('confirmed deletions must carry recomputable proof bound to every identifier and the runtime worktree check', async () => {
  const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  const record = w.journal.deletions[id(1)];
  const pendingCopy = structuredClone(record);
  record.status = 'deleted';
  record.confirmedAt = iso(now);
  await assert.rejects(planWorkerCleanup(context(w, [])), /recomputable/);
  w.journal.deletions[id(1)] = structuredClone(pendingCopy);
  w.present.add('/worktrees/w-1');
  const lingering = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1, [alias(1)]) });
  assert.equal(lingering.confirmed, false, 'caller absent:true cannot override the runtime directory check');
  w.present.clear();
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1, [alias(1)]) })).confirmed, true);
  const confirmed = structuredClone(w.journal.deletions[id(1)]);
  const tamper = [
    (entry) => { entry.resultEvidenceDigest = digest('forged'); },
    (entry) => { entry.confirmation.lookups = entry.confirmation.lookups.filter((lookup) => lookup.id !== alias(1)); entry.resultEvidenceDigest = digest(entry.confirmation); },
    (entry) => { entry.confirmation.runtimeWorktreeAbsent = false; entry.resultEvidenceDigest = digest(entry.confirmation); },
    (entry) => { entry.confirmation.observedAt = iso(now - 60_000); entry.resultEvidenceDigest = digest(entry.confirmation); },
    (entry) => { entry.worktreePath = '/worktrees/other'; },
    (entry) => { delete entry.planEvidenceDigest; },
  ];
  for (const change of tamper) {
    w.journal.deletions[id(1)] = structuredClone(confirmed);
    change(w.journal.deletions[id(1)]);
    await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/);
  }
  w.journal.deletions[id(1)] = confirmed;
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
});

test('the delete target is anchored to the settled terminal commitment, not mutable journal fields', async () => {
  let w = world([{ n: 1 }]);
  delete w.journal.sessions['delivery-1'].terminalEvidence;
  let plan = await planWorkerCleanup(context(w, [candidate(1)]));
  assert.match(plan.retained[0].reasons[0], /predates #2954.*manually/);
  w = world([{ n: 1 }]);
  w.journal.sessions['delivery-1'].worktreePath = '/worktrees/w-9';
  plan = await planWorkerCleanup(context(w, [candidate(1, { live: { worktreePath: '/worktrees/w-9' } })]));
  assert.match(plan.retained[0].reasons[0], /different session, worktree or correlation/);
  w = world([{ n: 1 }]);
  w.journal.sessions['delivery-1'].terminalEvidence.session.worktreePath = '/worktrees/w-9';
  plan = await planWorkerCleanup(context(w, [candidate(1)]));
  assert.match(plan.retained[0].reasons[0], /pre|binding/);
  for (const links of [{ '/worktrees/w-1': '/elsewhere/w-1' }, { '/worktrees/w-1': '/worktrees/w-2' },
    { '/worktrees/w-1': '/Volumes/data/src/pfarm1' }]) {
    w = world([{ n: 1 }]);
    plan = await planWorkerCleanup(context(w, [candidate(1)], {}, { links }));
    assert.ok(plan.retained[0].reasons.some((reason) => /escapes the isolated worktree root|aliases the main/.test(reason)), JSON.stringify(links));
  }
  w = world([{ n: 1 }]);
  plan = await planWorkerCleanup(context(w, [candidate(1)], { mainCheckoutPath: '/WORKTREES/W-1' }));
  assert.ok(plan.retained[0].reasons.some((reason) => /aliases the main checkout/.test(reason)), 'case-only alias of main');
  // A recorded path that is itself a main checkout (its .git is a directory) is never deleted.
  w = world([{ n: 1 }]);
  const mainLike = context(w, [candidate(1)]);
  const readback = mainLike.probe.worktree;
  mainLike.probe = { ...mainLike.probe, worktree: async (target) => ({ ...await readback(target), gitDirectory: true }) };
  plan = await planWorkerCleanup(mainLike);
  assert.ok(plan.retained[0].reasons.some((reason) => /aliases the main checkout/.test(reason)), 'main checkout .git directory');
  assert.equal(w.journal.deletions, undefined);
});

test('a native alias of the recorded canonical worktree matches; an alias of another worktree does not', async () => {
  // /alias/w-1 is the native app's symlinked spelling of the canonical /worktrees/w-1.
  const links = { '/alias/w-1': '/worktrees/w-1', '/alias/w-2': '/worktrees/w-2', '/alias': '/worktrees' };
  let w = world([{ n: 1 }]);
  let plan = await planWorkerCleanup(context(w, [{ ...candidate(1), live: { ...candidate(1).live, worktreePath: '/alias/w-1' } }], {}, { links }));
  assert.deepEqual(plan.eligible.map((item) => item.sessionId), [id(1)], JSON.stringify(plan.retained));
  assert.equal(plan.eligible[0].worktreePath, '/worktrees/w-1');
  w = world([{ n: 1 }]);
  plan = await planWorkerCleanup(context(w, [{ ...candidate(1), live: { ...candidate(1).live, worktreePath: '/alias/w-2' } }], {}, { links }));
  assert.ok(plan.retained[0].reasons.includes('worktree path does not match the recorded isolated worker worktree'));
});

test('git and PR facts come from runtime readback; caller claims are ignored', async () => {
  const w = world([{ n: 1 }]);
  const omittedOpenPr = { ...candidate(1, { pr: { state: 'OPEN' } }), prs: [], prsChecked: true };
  let plan = await planWorkerCleanup(context(w, [omittedOpenPr]));
  assert.ok(plan.retained[0].reasons.includes('open PR is never deleted'));
  const claimedClean = { ...candidate(1, { worktree: { porcelainEmpty: false } }),
    worktree: { path: '/worktrees/w-1', porcelainEmpty: true, gitPresent: true, unpushedCommits: 0 } };
  plan = await planWorkerCleanup(context(w, [claimedClean]));
  assert.deepEqual(plan.eligible, []);
  assert.ok(plan.retained[0].reasons.some((reason) => /tracked changes|untracked/.test(reason)));
  const claimedPushed = { ...candidate(1, { worktree: { unpushedCommits: 3 } }), worktree: { unpushedCommits: 0 } };
  plan = await planWorkerCleanup(context(w, [claimedPushed]));
  assert.ok(plan.retained[0].reasons.some((reason) => /unpushed/.test(reason)));
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [omittedOpenPr]) }), /not eligible/);
  assert.equal(w.journal.deletions, undefined);
});

test('default probe reads real git state without hooks and resolves symlinks canonically', async (t) => {
  const root = await realpath(await mkdtemp(path.join(tmpdir(), 'ralph-cleanup-probe-')));
  t.after(() => rm(root, { recursive: true, force: true }));
  const main = path.join(root, 'main');
  const worktree = path.join(root, 'w-1');
  await mkdir(main);
  const git = (...args) => execFileSync('git', ['-C', main, ...args], { encoding: 'utf8' });
  git('init', '-q', '-b', 'development');
  git('-c', 'user.email=f@example.com', '-c', 'user.name=f', 'commit', '-q', '--allow-empty', '-m', 'init');
  git('worktree', 'add', '-q', '-b', 'feature/cleanup', worktree);
  const mainFacts = await defaultCleanupProbe.worktree(main);
  assert.equal(mainFacts.gitDirectory, true, 'a main checkout (.git directory) is never a linked worker worktree');
  assert.equal(mainFacts.headSha, undefined, 'git is never run in a main checkout');
  const clean = await defaultCleanupProbe.worktree(worktree);
  assert.equal(clean.exists, true);
  assert.equal(clean.gitPresent, true);
  assert.equal(clean.branch, 'feature/cleanup');
  assert.match(clean.headSha, /^[0-9a-f]{40}$/);
  assert.equal(clean.porcelain, '');
  await writeFile(path.join(worktree, 'untracked.txt'), 'x');
  assert.match((await defaultCleanupProbe.worktree(worktree)).porcelain, /\?\? untracked\.txt/);
  const link = path.join(root, 'w-link');
  await symlink(worktree, link);
  assert.equal(await defaultCleanupProbe.canonical(link), worktree);
  assert.equal(await defaultCleanupProbe.absent(link), false);
  assert.equal(await defaultCleanupProbe.absent(path.join(root, 'missing')), true);
  assert.deepEqual(await defaultCleanupProbe.worktree(path.join(root, 'missing')), { exists: false });
  await mkdir(path.join(root, 'plain'));
  assert.equal((await defaultCleanupProbe.worktree(path.join(root, 'plain'))).gitPresent, false);
});

test('the committed identifier set cannot be narrowed or replaced before intent', async () => {
  for (const source of ['sessionAliases', 'creationHandle']) {
    const aliasValue = source === 'sessionAliases' ? [alias(1)] : alias(1);
    for (const change of [(mapping) => { delete mapping[source]; },
      (mapping) => { mapping[source] = source === 'sessionAliases' ? [alias(2)] : alias(2); }]) {
      const w = world([{ n: 1, mapping: { [source]: aliasValue } }]);
      assert.equal((await planWorkerCleanup(context(w, [candidate(1)]))).eligible.length, 1, source);
      change(w.journal.sessions['delivery-1']);
      const plan = await planWorkerCleanup(context(w, [candidate(1)]));
      assert.match(plan.retained[0].reasons[0], /identifiers differ from the terminal commitment/, source);
      await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }), /not eligible/);
    }
    const w = world([{ n: 1, mapping: { [source]: aliasValue } }]);
    await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
    assert.deepEqual(w.journal.deletions[id(1)].aliases, [alias(1)]);
    w.journal.deletions[id(1)].aliases = [];
    await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/);
  }
});

// Native delete_item archives worktree sessions (#2956): get_session keeps resolving the
// session ID and its project_session_id alias with archived:true and path:"".
const archivedLookup = (n, aliases = [], overrides = {}) => ({ ...confirmedLookup(n, aliases), deleteOutcome: 'Session archive requested.',
  lookups: [id(n), ...aliases].map((value) => ({ id: value, notFound: false, archived: true, path: '', resolvedId: id(n),
    ...overrides[value] })) });

test('an archived retirement with an empty path and absent worktree is confirmed and recorded as archived', async () => {
  const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  w.present.add('/worktrees/w-1');
  const lingering = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: archivedLookup(1, [alias(1)]) });
  assert.equal(lingering.confirmed, false, 'archive does not bypass the runtime worktree check');
  w.present.clear();
  const result = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: archivedLookup(1, [alias(1)], { [alias(1)]: { path: undefined } }) });
  assert.deepEqual([result.confirmed, result.outcome], [true, 'archived']);
  const record = w.journal.deletions[id(1)];
  assert.equal(record.status, 'deleted');
  assert.equal(record.confirmation.outcome, 'archived');
  assert.deepEqual(record.confirmation.lookups.map((lookup) => [lookup.id, lookup.outcome, lookup.resolvedId]),
    [[id(1), 'archived', id(1)], [alias(1), 'archived', id(1)]]);
  assert.equal(record.resultEvidenceDigest, digest(record.confirmation));
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
  const plan = await planWorkerCleanup(context(w, []));
  assert.deepEqual(plan.deleted.map((item) => [item.sessionId, item.outcome]), [[id(1), 'archived']]);
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: archivedLookup(1, [alias(1)]) })).outcome, 'archived');
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }),
    /already confirmed; never delete it again/);
});

test('archived lookups stay pending unless every identifier is retired to the recorded session with no path', async () => {
  const pendingCases = [
    ['archived with a non-empty path', { [id(1)]: { path: '/worktrees/w-1' } }],
    ['unarchived with an empty path', { [id(1)]: { archived: false } }],
    ['archived flag missing', { [id(1)]: { archived: undefined } }],
    ['alias resolves to a different session', { [alias(1)]: { resolvedId: id(2) } }],
    ['resolved session unknown', { [alias(1)]: { resolvedId: undefined } }],
    ['mixed archived and live alias', { [alias(1)]: { archived: false, path: '/worktrees/w-1' } }],
    ['not found yet contradicted by archive facts', { [alias(1)]: { notFound: true, archived: true, resolvedId: undefined } }],
    ['not found yet contradicted by a path', { [alias(1)]: { notFound: true, archived: undefined, path: '/worktrees/w-1', resolvedId: undefined } }],
  ];
  for (const [name, overrides] of pendingCases) {
    const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }, { n: 2 }]);
    await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
    const result = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
      evidence: archivedLookup(1, [alias(1)], overrides) });
    assert.equal(result.confirmed, false, name);
    assert.equal(result.pending, true, name);
    assert.deepEqual(result.stillPresent, Object.keys(overrides), name);
    const record = w.journal.deletions[id(1)];
    assert.equal(record.status, 'pending', name);
    assert.equal(record.inspections.at(-1).outcome, 'unconfirmed', name);
  }
  const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  for (const bad of [{ archived: 'true' }, { path: 7 }, { resolvedId: 'not-a-uuid' }]) {
    await assert.rejects(recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
      evidence: archivedLookup(1, [alias(1)], { [id(1)]: bad }) }), /explicit notFound result/);
  }
  const unchecked = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: archivedLookup(1) });
  assert.deepEqual([unchecked.confirmed, unchecked.unchecked], [false, [alias(1)]]);
});

test('mixed not-found and archived identifiers confirm as an archived retirement', async () => {
  for (const overrides of [{ [alias(1)]: { notFound: true, archived: undefined, path: undefined, resolvedId: undefined } },
    { [id(1)]: { notFound: true, archived: undefined, path: undefined, resolvedId: undefined } }]) {
    const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }]);
    await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
    const result = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
      evidence: archivedLookup(1, [alias(1)], overrides) });
    assert.deepEqual([result.confirmed, result.outcome], [true, 'archived']);
    assert.deepEqual(w.journal.deletions[id(1)].confirmation.lookups.map((lookup) => lookup.outcome).sort(), ['archived', 'deleted']);
  }
  const w = world([{ n: 1 }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  const hard = await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []), evidence: confirmedLookup(1) });
  assert.deepEqual([hard.confirmed, hard.outcome], [true, 'deleted']);
  const saved = structuredClone(w.journal.deletions[id(1)]);
  for (const forged of [null, 'archived', 'unconfirmed']) {
    const entry = structuredClone(saved);
    entry.confirmation.outcome = forged;
    entry.resultEvidenceDigest = digest(entry.confirmation);
    w.journal.deletions[id(1)] = entry;
    await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/, String(forged));
  }
  w.journal.deletions[id(1)] = saved;
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
});

test('a legacy not-found confirmation without stored outcomes still verifies as a deletion', async () => {
  const w = world([{ n: 1 }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []), evidence: confirmedLookup(1) });
  const record = w.journal.deletions[id(1)];
  record.confirmation = { observedAt: record.confirmation.observedAt, source: record.confirmation.source,
    lookups: [{ id: id(1), notFound: true }], worktree: record.confirmation.worktree, runtimeWorktreeAbsent: true,
    deleteOutcome: 'succeeded' };
  record.resultEvidenceDigest = digest(record.confirmation);
  assert.deepEqual((await planWorkerCleanup(context(w, []))).deleted.map((item) => item.outcome), ['deleted']);
  record.confirmation.lookups[0].outcome = 'deleted';
  record.resultEvidenceDigest = digest(record.confirmation);
  await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/);
});

test('a retired archived identity that reappears live or tampered archive proof fails closed', async () => {
  const w = world([{ n: 1, mapping: { sessionAliases: [alias(1)] } }, { n: 2 }]);
  await recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) });
  assert.equal((await recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: archivedLookup(1, [alias(1)]) })).confirmed, true);
  await assert.rejects(planWorkerCleanup(context(w, [candidate(1)])), /reappeared/);
  await assert.rejects(planWorkerCleanup(context(w, [{ ...candidate(2), sessionId: alias(1) }])), /reappeared/);
  const confirmed = structuredClone(w.journal.deletions[id(1)]);
  const redigest = (entry) => { entry.resultEvidenceDigest = digest(entry.confirmation); };
  const tamper = [
    (entry) => { entry.confirmation.outcome = 'deleted'; redigest(entry); },
    (entry) => { entry.confirmation.lookups[1].resolvedId = id(2); redigest(entry); },
    (entry) => { entry.confirmation.lookups[0].path = '/worktrees/w-1'; redigest(entry); },
    (entry) => { entry.confirmation.lookups[0].archived = false; redigest(entry); },
    (entry) => { entry.confirmation.lookups.push({ ...entry.confirmation.lookups[0], archived: false }); redigest(entry); },
    (entry) => { entry.confirmation.lookups[1].outcome = 'deleted'; redigest(entry); },
    (entry) => { entry.confirmation.lookups[0].outcome = 'unchecked'; redigest(entry); },
    (entry) => { delete entry.confirmation.lookups[0].outcome; redigest(entry); },
    (entry) => { delete entry.confirmation.outcome; redigest(entry); },
  ];
  for (const change of tamper) {
    w.journal.deletions[id(1)] = structuredClone(confirmed);
    change(w.journal.deletions[id(1)]);
    await assert.rejects(planWorkerCleanup(context(w, [])), /deletion record/);
  }
  w.journal.deletions[id(1)] = confirmed;
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
});
