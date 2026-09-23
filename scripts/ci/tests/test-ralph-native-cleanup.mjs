import assert from 'node:assert/strict';
import test from 'node:test';
import { digest } from '../ralph-mailbox.mjs';
import {
  deletedWorkerIds, planWorkerCleanup, recordDeletionIntent, recordDeletionResult, reapSettleMs,
} from '../ralph-native-cleanup.mjs';

const now = Date.parse('2026-09-01T12:00:00Z');
const iso = (value) => new Date(value).toISOString();
const settledAt = iso(now - 60 * 60 * 1000);
const config = { role: 'consumer', workerId: 'mini', host: 'macos-mobile', worktreeRoot: '/worktrees' };
const calling = 'aaaaaaaa-0000-4000-8000-000000000000';
const id = (n) => `cccccccc-1111-4222-8333-${String(n).padStart(12, '0')}`;
const alias = (n) => `dddddddd-1111-4222-8333-${String(n).padStart(12, '0')}`;
const artifactUrl = (n) => `https://github.com/OlyForge3D/PrintFarmer/issues/${900 + n}#issuecomment-${n}`;

function world(workers) {
  const state = { assignments: {} }, journal = { sessions: {} };
  for (const worker of workers) {
    const { n } = worker, correlation = `delivery-${n}`, evidenceDigest = digest(`terminal-${n}`);
    state.assignments[`assignment-${n}`] = {
      assignmentId: `assignment-${n}`, workerId: worker.workerId ?? 'mini', state: worker.state ?? 'terminal',
      task: { purpose: worker.purpose ?? 'implementation', issue: 900 + n }, terminalCommitment: evidenceDigest,
      receipts: [{ status: 'terminal-reported', correlation, evidenceDigest, observedAt: worker.receiptAt ?? settledAt }],
    };
    journal.sessions[correlation] = { assignmentId: `assignment-${n}`, sessionId: id(n),
      worktreePath: `/worktrees/w-${n}`, lastEvidenceDigest: evidenceDigest, ...worker.mapping };
  }
  return { state, journal };
}

function candidate(n, { live = {}, worktree = {}, pr = {}, ...rest } = {}) {
  return {
    sessionId: id(n),
    live: { found: true, name: `fix: worker ${n}`, worktreePath: `/worktrees/w-${n}`, branch: `b-${n}`,
      busy: false, pendingInput: false, agentMerge: false, automation: false, ...live },
    worktree: { path: `/worktrees/w-${n}`, branch: `b-${n}`, exists: true, gitPresent: true, porcelainEmpty: true,
      unpushedCommits: 0, commitsAheadOfDevelopment: 2, remoteBranchExists: true, ...worktree },
    prsChecked: true,
    prs: [{ number: 10 + n, state: 'MERGED', mergedAt: settledAt, mergeCommitOnDevelopment: true,
      headPreservedAfterMerge: true, commitsAfterMerge: [], ...pr }],
    ...rest,
  };
}

const readArtifact = async (assignment, url) => {
  if (url !== artifactUrl(assignment.task.issue - 900)) throw new Error('fixture readback failed');
  return { kind: 'issue-comment', url, bodyDigest: digest(`body-${url}`), bodyBytes: 4 };
};

function context(w, candidates, extra = {}, overrides = {}) {
  return { config, journal: w.journal, state: w.state, now, readArtifact, roundId: 'round-1',
    evidence: { observedAt: iso(now), source: 'fixture get_session/git/gh readback', callingSessionId: calling,
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
  assert.equal(w.journal.deletions[id(1)].terminalEvidenceDigest, digest('terminal-1'));
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }),
    /already pending.*never retry delete_item/);
  const result = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) });
  assert.equal(result.confirmed, true);
  assert.deepEqual([...deletedWorkerIds(w.journal, w.state)], [id(1)]);
  assert.equal(recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) }).alreadyRecorded, true);
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
    ['wrong worktree', {}, candidate(1, { worktree: { path: '/worktrees/other' } }), /recorded isolated worker worktree/],
    ['role-named mapped session', {}, candidate(1, { live: { name: 'Ralph consumer - mini' } }), /role-named/],
    ['Reaper-named mapped session', {}, candidate(1, { live: { name: 'Reaper round' } }), /role-named/],
    ['closed PR without remote branch', {}, candidate(1, { worktree: { remoteBranchExists: false },
      pr: { state: 'CLOSED', closedAt: settledAt, closureReason: 'superseded' } }), /origin branch/],
    ['closed PR without reason', {}, candidate(1, { pr: { state: 'CLOSED', closedAt: settledAt } }), /closed-without-merge reason/],
    ['no PR with pushed commits', {}, candidate(1, { prs: [] }), /human review/],
    ['no PR with unpushed commits', {}, candidate(1, { prs: [], worktree: { unpushedCommits: 2 } }), /WARNING/],
    ['no-PR implementation', {}, candidate(1, { prs: [], worktree: { commitsAheadOfDevelopment: 0 } }), /only for research/],
    ['multiple PRs', {}, candidate(1, { prs: [candidate(1).prs[0], { ...candidate(1).prs[0], number: 99 }] }), /multiple PRs/],
    ['incomplete PR lookup', {}, candidate(1, { prsChecked: false }), /PR lookup/],
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
  let plan = await planWorkerCleanup(context(w, [unmapped, role]));
  assert.match(plan.retained.find((item) => item.sessionId === id(7)).reasons[0], /unmapped/);
  assert.match(plan.retained.find((item) => item.sessionId === id(8)).reasons[0], /role session/);
  assert.match(plan.retained.find((item) => item.sessionId === id(1)).reasons[0], /no fresh cleanup evidence/);
  plan = await planWorkerCleanup(context(w, [candidate(1)], { callingSessionId: id(1) }));
  assert.match(plan.retained[0].reasons[0], /calling session/);
  plan = await planWorkerCleanup(context(w, [candidate(1)], { mainCheckoutPath: '/worktrees/w-1' }));
  assert.ok(plan.retained[0].reasons.some((reason) => /recorded isolated worker worktree/.test(reason)));
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
    assert.throws(() => recordDeletionResult({ request: request(1, 'record-deletion-result'), ...bad,
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
  const stillThere = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), lookups: [{ id: id(1), notFound: false }], deleteOutcome: 'unknown' } });
  assert.equal(stillThere.confirmed, false);
  assert.equal(stillThere.pending, true);
  assert.deepEqual(stillThere.stillPresent, [id(1)]);
  assert.equal(stillThere.deleteAllowed, false);
  const worktreeRemains = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), worktree: { path: '/worktrees/w-1', absent: false } } });
  assert.equal(worktreeRemains.confirmed, false);
  const wrongPath = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), worktree: { path: '/worktrees/other', absent: true } } });
  assert.equal(wrongPath.confirmed, false);
  assert.equal(w.journal.deletions[id(1)].status, 'pending');
  assert.equal(w.journal.deletions[id(1)].inspections.length, 3);
  const plan = await planWorkerCleanup(context(w, [candidate(1)]));
  assert.deepEqual(plan.eligible, []);
  assert.match(plan.pending[0].reasons[0], /never retry delete_item/);
  await assert.rejects(recordDeletionIntent({ request: request(1, 'record-deletion-intent'), ...context(w, [candidate(1)]) }), /already pending/);
  assert.equal(recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) }).confirmed, true);
  assert.throws(() => recordDeletionResult({ request: request(2, 'record-deletion-result'), ...context(w, []),
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
  const primaryOnly = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1) });
  assert.equal(primaryOnly.confirmed, false);
  assert.deepEqual(new Set(primaryOnly.unchecked), new Set([alias(1), suppliedAlias]));
  const aliasLive = recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: { ...confirmedLookup(1), lookups: [{ id: id(1), notFound: true }, { id: alias(1), notFound: false },
      { id: suppliedAlias, notFound: true }] } });
  assert.deepEqual(aliasLive.stillPresent, [alias(1)]);
  assert.equal(recordDeletionResult({ request: request(1, 'record-deletion-result'), ...context(w, []),
    evidence: confirmedLookup(1, [alias(1), suppliedAlias]) }).confirmed, true);
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
