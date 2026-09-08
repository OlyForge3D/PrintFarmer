import assert from 'node:assert/strict';
import { mkdir, readFile, rm, utimes, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {
  RalphCacheError,
  assessCleanupCandidate,
  cacheFileForScope,
  collectPaginated,
  compactRoundOutput,
  compareSnapshots,
  createRoundCache,
  isCacheCurrent,
  orderReadyIssues,
  readRoundCache,
  resolveRalphCacheDirectory,
  validateDependencyGraph,
  writeRoundCache,
} from '../ralph-round-cache.mjs';

const scope = { repository: 'OlyForge3D/PrintFarmer', workflow: 'ralph-hourly' };
const cache = (conclusions = { queue: ['#1'] }) => createRoundCache({
  scope,
  policyVersion: '2026-09-08',
  comparisons: {
    issues: { '1': { updatedAt: 'a', blockers: [] } },
    prs: { '2': { head: 'b', checks: 'SUCCESS', verdicts: ['c'] } },
    base: 'development-a',
    sessions: { issue1: 'active' },
    claims: { issue1: 'claimed' },
    linkedPrs: { issue1: 2 },
    codeql: { analysis: 'complete', alerts: [] },
    holds: [],
  },
  conclusions,
});

async function temporaryDirectory() {
  const directory = path.resolve('fixtures', 'ralph-cache-validation');
  await rm(directory, { recursive: true, force: true });
  await mkdir(directory, { recursive: true });
  return directory;
}

test('uses machine-scoped platform cache defaults and explicit fixture override', () => {
  assert.match(resolveRalphCacheDirectory({
    env: { LOCALAPPDATA: 'C:\\Users\\agent\\AppData\\Local' }, platform: 'win32',
  }), /PrintFarmer[\\/]ralph-cache$/);
  assert.match(resolveRalphCacheDirectory({ env: {}, platform: 'darwin', home: '/Users/agent' }),
    /Library[\\/]Caches[\\/]PrintFarmer[\\/]ralph-cache$/);
  assert.equal(resolveRalphCacheDirectory({ env: { RALPH_CACHE_DIR: 'fixtures/cache' } }),
    path.resolve('fixtures/cache'));
});

test('persists across different worktree paths because scope does not include cwd', async () => {
  const directory = await temporaryDirectory();
  try {
    await writeRoundCache(scope, cache(), { env: { RALPH_CACHE_DIR: directory } });
    const result = await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } });
    assert.equal(result.reason, undefined);
    assert.deepEqual(result.cache.conclusions, { queue: ['#1'] });
    assert.equal(cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } }),
      cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory }, cwd: 'other-worktree' }));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('rejects corrupt cache and atomically replaces it with complete JSON', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    await writeRoundCache(scope, cache(), { env: { RALPH_CACHE_DIR: directory } });
    await writeFile(file, '{broken');
    assert.equal((await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } })).reason, 'corrupt');
    await writeRoundCache(scope, cache({ queue: ['#2'] }), { env: { RALPH_CACHE_DIR: directory } });
    assert.deepEqual(JSON.parse(await readFile(file, 'utf8')).conclusions.queue, ['#2']);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('serializes concurrent rounds without partial cache content', async () => {
  const directory = await temporaryDirectory();
  try {
    await Promise.all([
      writeRoundCache(scope, cache({ queue: ['#a'] }), { env: { RALPH_CACHE_DIR: directory } }),
      writeRoundCache(scope, cache({ queue: ['#b'] }), { env: { RALPH_CACHE_DIR: directory } }),
    ]);
    const result = await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } });
    assert.ok(['#a', '#b'].includes(result.cache.conclusions.queue[0]));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('reclaims only a lock with demonstrably expired ownership metadata', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    await writeFile(`${file}.lock`, JSON.stringify({
      ownerToken: 'interrupted-owner', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    }));
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, staleLockMs: 1, isOwnerAlive: () => false,
    });
    assert.equal((await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } })).reason, undefined);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('never reclaims an expired lease while its owner is demonstrably alive', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    await writeFile(`${file}.lock`, JSON.stringify({
      ownerToken: 'long-owner', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    }));
    await assert.rejects(() => writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, retries: 1, isOwnerAlive: () => true,
    }), (error) => error.code === 'LOCK_TIMEOUT');
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('concurrent stale-lock reclaimers cannot remove a replacement generation', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    await writeFile(`${file}.lock`, JSON.stringify({
      ownerToken: 'crashed-owner', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    }));
    let observed = 0;
    let releaseObservations;
    const observations = new Promise((resolve) => { releaseObservations = resolve; });
    let activeGuards = 0;
    let maxActiveGuards = 0;
    const hooks = {
      afterStaleObservation: async () => {
        observed += 1;
        if (observed === 2) releaseObservations();
        await observations;
      },
      afterReclaimGuardAcquired: async () => {
        activeGuards += 1;
        maxActiveGuards = Math.max(maxActiveGuards, activeGuards);
        await new Promise((resolve) => setTimeout(resolve, 5));
        activeGuards -= 1;
      },
    };
    await Promise.all([
      writeRoundCache(scope, cache({ queue: ['#first'] }), {
        env: { RALPH_CACHE_DIR: directory }, isOwnerAlive: () => false, hooks,
      }),
      writeRoundCache(scope, cache({ queue: ['#second'] }), {
        env: { RALPH_CACHE_DIR: directory }, isOwnerAlive: () => false, hooks,
      }),
    ]);
    const result = await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } });
    assert.ok(['#first', '#second'].includes(result.cache.conclusions.queue[0]));
    assert.equal(maxActiveGuards, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('a stale reclaimer cannot remove a replacement lock generation', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    await writeFile(`${file}.lock`, JSON.stringify({
      ownerToken: 'old-generation', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    }));
    const replacement = {
      ownerToken: 'replacement-generation', pid: 2,
      createdAt: '2099-01-01T00:00:00Z', expiresAt: '2099-01-01T01:00:00Z',
    };
    await assert.rejects(() => writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, retries: 1, isOwnerAlive: () => false,
      hooks: { afterReclaimGuardAcquired: () => writeFile(`${file}.lock`, JSON.stringify(replacement)) },
    }), (error) => error.code === 'LOCK_TIMEOUT');
    assert.equal(JSON.parse(await readFile(`${file}.lock`, 'utf8')).ownerToken, replacement.ownerToken);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('recovers a dead stale reclaim guard and cleans up after guard creation failure', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const stale = {
      ownerToken: 'crashed-owner', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    };
    await writeFile(`${file}.lock`, JSON.stringify(stale));
    await writeFile(`${file}.lock.reclaim`, JSON.stringify(stale));
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, isOwnerAlive: () => false, retries: 4,
    });
    assert.equal((await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } })).reason, undefined);

    await writeFile(`${file}.lock`, JSON.stringify(stale));
    await assert.rejects(() => writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, isOwnerAlive: () => false,
      writeGuardMetadata: async () => { throw new Error('simulated guard write failure'); },
    }), /simulated guard write failure/);
    assert.equal(await readFile(`${file}.lock.reclaim`, 'utf8').then(() => true, () => false), false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('stale guard recovery cannot remove a replacement guard generation', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const stale = {
      ownerToken: 'dead-generation', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    };
    const replacement = {
      ownerToken: 'new-guard-generation', pid: 2,
      createdAt: '2099-01-01T00:00:00Z', expiresAt: '2099-01-01T01:00:00Z',
    };
    await writeFile(`${file}.lock`, JSON.stringify(stale));
    await writeFile(`${file}.lock.reclaim`, JSON.stringify(stale));
    await assert.rejects(() => writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, retries: 1, isOwnerAlive: () => false,
      hooks: {
        afterRecoveryClaimAcquired: async (lockFile) => {
          if (lockFile.endsWith('.reclaim')) await writeFile(lockFile, JSON.stringify(replacement));
        },
      },
    }), (error) => error.code === 'LOCK_TIMEOUT');
    assert.equal(
      JSON.parse(await readFile(`${file}.lock.reclaim`, 'utf8')).ownerToken,
      replacement.ownerToken,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('a coordinated second writer cannot overlap or remove a live replaced ancestor', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const stale = {
      ownerToken: 'dead-generation', pid: 1,
      createdAt: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:01:00Z',
    };
    const replacement = {
      ownerToken: 'live-replacement', pid: 2,
      createdAt: '2099-01-01T00:00:00Z', expiresAt: '2099-01-01T01:00:00Z',
    };
    const claim = `${file}.lock.reclaim.recover.token%3Adead-generation`;
    await writeFile(`${file}.lock`, JSON.stringify(stale));
    await writeFile(`${file}.lock.reclaim`, JSON.stringify(stale));
    await writeFile(claim, JSON.stringify(stale));
    let secondWriter;
    await assert.rejects(() => writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, retries: 1, isOwnerAlive: () => false,
      hooks: {
        afterAncestorValidated: async (ancestor) => {
          if (ancestor.endsWith('.reclaim')) {
            await writeFile(ancestor, JSON.stringify(replacement));
            secondWriter = writeRoundCache(scope, cache({ queue: ['#second'] }), {
              env: { RALPH_CACHE_DIR: directory }, retries: 1, isOwnerAlive: () => false,
            });
            await assert.rejects(secondWriter, (error) => error.code === 'LOCK_TIMEOUT');
          }
        },
      },
    }), (error) => error.code === 'LOCK_TIMEOUT');
    assert.ok(secondWriter);
    assert.equal(
      JSON.parse(await readFile(`${file}.lock.reclaim`, 'utf8')).ownerToken,
      replacement.ownerToken,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('recovers only old incomplete lock and guard records after interrupted writes', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const old = new Date('2026-01-01T00:00:00Z');
    await writeFile(`${file}.lock`, '');
    await utimes(`${file}.lock`, old, old);
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, staleLockMs: 1, isOwnerAlive: () => false,
    });
    await writeFile(`${file}.lock`, JSON.stringify({
      ownerToken: 'dead-main', pid: 1,
      createdAt: old.toISOString(), expiresAt: old.toISOString(),
    }));
    await writeFile(`${file}.lock.reclaim`, '{"partial":');
    await utimes(`${file}.lock.reclaim`, old, old);
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, staleLockMs: 1, isOwnerAlive: () => false,
    });
    assert.equal((await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } })).reason, undefined);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('recovers an orphaned generation recovery claim without deleting a replacement', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const old = new Date('2026-01-01T00:00:00Z');
    const main = { ownerToken: 'dead-main', pid: 1, createdAt: old.toISOString(), expiresAt: old.toISOString() };
    const guard = { ownerToken: 'dead-guard', pid: 1, createdAt: old.toISOString(), expiresAt: old.toISOString() };
    const claim = `${file}.lock.reclaim.recover.token%3Adead-guard`;
    await writeFile(`${file}.lock`, JSON.stringify(main));
    await writeFile(`${file}.lock.reclaim`, JSON.stringify(guard));
    await writeFile(claim, JSON.stringify(guard));
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, staleLockMs: 1, isOwnerAlive: () => false, retries: 8,
    });
    assert.equal(await readFile(claim, 'utf8').then(() => true, () => false), false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('unwinds three or more interrupted stale recovery claims without a depth limit', async () => {
  const directory = await temporaryDirectory();
  try {
    const file = cacheFileForScope(scope, { env: { RALPH_CACHE_DIR: directory } });
    const old = new Date('2026-01-01T00:00:00Z').toISOString();
    const main = { ownerToken: 'dead-main', pid: 1, createdAt: old, expiresAt: old };
    const guard = { ownerToken: 'dead-guard', pid: 1, createdAt: old, expiresAt: old };
    const claim1 = `${file}.lock.reclaim.recover.token%3Adead-guard`;
    const claim2 = `${claim1}.recover.token%3Aorphan-1`;
    const claim3 = `${claim2}.recover.token%3Aorphan-2`;
    await writeFile(`${file}.lock`, JSON.stringify(main));
    await writeFile(`${file}.lock.reclaim`, JSON.stringify(guard));
    await writeFile(claim1, JSON.stringify({ ...guard, ownerToken: 'orphan-1' }));
    await writeFile(claim2, JSON.stringify({ ...guard, ownerToken: 'orphan-2' }));
    await writeFile(claim3, JSON.stringify({ ...guard, ownerToken: 'orphan-3' }));
    await writeRoundCache(scope, cache(), {
      env: { RALPH_CACHE_DIR: directory }, staleLockMs: 1, isOwnerAlive: () => false, retries: 12,
    });
    assert.equal((await readRoundCache(scope, { env: { RALPH_CACHE_DIR: directory } })).reason, undefined);
    for (const claim of [claim1, claim2, claim3]) {
      assert.equal(await readFile(claim, 'utf8').then(() => true, () => false), false);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('all authorization-adjacent changes invalidate cached conclusions', () => {
  const baseline = cache().comparisons;
  for (const comparisons of [
    { ...baseline, prs: { '2': { ...baseline.prs['2'], verdicts: ['new'] } } },
    { ...baseline, issues: { '1': { ...baseline.issues['1'], blockers: ['#9 closed'] } } },
    { ...baseline, prs: { '2': { ...baseline.prs['2'], checks: 'FAILURE' } } },
    { ...baseline, sessions: { issue1: 'complete' } },
    { ...baseline, claims: { issue1: 'released' } },
    { ...baseline, linkedPrs: { issue1: 3 } },
    { ...baseline, base: 'development-b' },
    { ...baseline, codeql: { analysis: 'complete', alerts: ['new-alert'] } },
    { ...baseline, holds: ['#1'] },
  ]) {
    assert.equal(compareSnapshots(baseline, comparisons).unchanged, false);
  }
  assert.equal(isCacheCurrent(cache(), '2026-09-08'), true);
  assert.equal(isCacheCurrent(cache(), 'different-policy'), false);
});

test('fails closed for API errors and incomplete pagination', async () => {
  await assert.rejects(() => collectPaginated(async () => { throw new Error('rate limited'); }),
    (error) => error instanceof RalphCacheError && error.code === 'API_FAILURE');
  await assert.rejects(() => collectPaginated(async () => Array(100).fill({}), { maxPages: 2 }),
    (error) => error instanceof RalphCacheError && error.code === 'INCOMPLETE_DATA');
});

test('orders READY issues using the complete graph and rejects cycles', () => {
  const completeIssues = [
    { number: 1, labels: ['priority:p2'], createdAt: '2026-01-02' },
    { number: 2, labels: ['priority:p0'], createdAt: '2026-01-03' },
    { number: 3, labels: ['priority:p1'], createdAt: '2026-01-01' },
  ];
  const ordered = orderReadyIssues(completeIssues, [completeIssues[0]], [
    { blocker: 1, blocked: 2 }, { blocker: 1, blocked: 3 }, { blocker: 2, blocked: 3 },
  ]);
  assert.deepEqual(ordered.map((issue) => issue.number), [1]);
  assert.equal(ordered[0].number, 1);
  assert.equal(ordered[0].effectivePriority, 0);
  assert.equal(ordered[0].unblockValue, 2);
  assert.throws(() => orderReadyIssues(completeIssues, [completeIssues[0]], [
    { blocker: 1, blocked: 2 }, { blocker: 2, blocked: 1 },
  ]),
    (error) => error.code === 'DEPENDENCY_CYCLE');
  assert.throws(() => validateDependencyGraph(completeIssues, [
    { blocker: 4, blocked: 5 }, { blocker: 5, blocked: 4 },
  ]), (error) => error.code === 'DEPENDENCY_CYCLE');
});

test('reports compact unchanged and changed fixture output', () => {
  const unchanged = compactRoundOutput({});
  const changed = compactRoundOutput({ changed: ['#12'], ready: ['#12'], blocked: ['#13:#12'], deferred: ['#14'] });
  assert.equal(unchanged, 'changed:— ready:— blocked:— macOS:—');
  assert.equal(changed, 'changed:#12 ready:#12 blocked:#13:#12 macOS:#14');
  assert.ok(changed.length < 80);
});

test('cleanup candidates are report-only and fail closed for uncertainty or post-merge work', () => {
  const safe = {
    session: { active: false },
    worktree: { inspected: true, dirty: false, untracked: false },
    finalReport: { workingTreeClean: true, allCommitsPushed: true },
    settledAt: '2026-01-01T00:00:00Z',
    pr: {
      state: 'MERGED', commitsAfterMergeKnown: true, commitsAfterMerge: [],
      mergeCommitVerifiedOnDevelopment: true, headPreservedAfterMerge: true,
    },
  };
  assert.deepEqual(assessCleanupCandidate(safe, { now: Date.parse('2026-01-01T02:00:00Z') }),
    { candidate: true, reasons: [] });
  for (const unsafe of [
    { ...safe, session: { active: true } },
    { ...safe, worktree: { inspected: true, dirty: false, untracked: true } },
    { ...safe, pr: { state: 'MERGED', commitsAfterMergeKnown: true, commitsAfterMerge: ['abc'] } },
    { ...safe, pr: { state: 'MERGED', commitsAfterMergeKnown: false } },
    { ...safe, pr: { ...safe.pr, mergeCommitVerifiedOnDevelopment: false } },
    { ...safe, pr: { ...safe.pr, headPreservedAfterMerge: false } },
    {
      ...safe, pr: { state: 'CLOSED', commitsAfterMergeKnown: true, commitsAfterMerge: [] },
      finalReport: { workingTreeClean: true, allCommitsPushed: true },
    },
    { ...safe, settledAt: '2026-01-01T01:30:00Z' },
    { ...safe, pr: undefined, noPrDeliverable: { completed: true, verified: false } },
  ]) assert.equal(assessCleanupCandidate(unsafe, { now: Date.parse('2026-01-01T02:00:00Z') }).candidate, false);
  const closed = {
    ...safe,
    pr: { state: 'CLOSED', commitsAfterMergeKnown: true, commitsAfterMerge: [], linkedIssueDispositionVerified: true },
    finalReport: {
      workingTreeClean: true, allCommitsPushed: true, closedWithoutMerge: true, closureReason: 'superseded',
    },
  };
  assert.equal(assessCleanupCandidate(closed, { now: Date.parse('2026-01-01T02:00:00Z') }).candidate, true);
});

test('dispatcher routes only to self-contained policies and retains gates', async () => {
  const [skill, operations, cleanup, prePr, prMerge, terminal] = await Promise.all([
    readFile('.copilot/skills/ralph-loop/SKILL.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/operations.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/cleanup.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/implementation-pre-pr.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/pr-merge.md', 'utf8'),
    readFile('.copilot/skills/ralph-loop/session-terminal-contract.md', 'utf8'),
  ]);
  for (const reference of [
    'implementation-pre-pr.md', 'session-terminal-contract.md', 'pr-merge.md',
    'one round and exits', 'five implementation/analysis slots maximum',
    'never dispatch, review, or merge it',
    'Before every dispatch, claim, message, review decision, or merge, fetch',
    'gemini-3.1-pro-preview', 'assessCleanupCandidate', 'operations.md', 'cleanup.md',
    'No named non-workflow test entrypoint', 'test-ralph-round-cache.mjs',
  ]) assert.match(skill, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
  assert.doesNotMatch(skill, /\.squad\/templates\/ralph-reference\.md/i);
  for (const reference of [
    'GitHub native', 'dependency prose markers', 'Detect cycles', 'five live',
    'fresh eligibility', 'apply claim label and comment', 'verify that exact claim landed',
  ]) assert.match(operations, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
  for (const reference of [
    'one Ralph automation', 'delete_item', 'earlier-round children', 'post-merge',
    'confirmed-action handoff', 'explicitly confirms each exact',
  ]) assert.match(cleanup, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
  for (const reference of [
    '.github/copilot-instructions.md', 'Documentation-Only Changes: One Reviewer',
    'number of genuine canonical verdict comments', 'documentation-only change and three',
    'full-gate change',
  ]) assert.match(prePr, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
  assert.doesNotMatch(prePr, /Workflow\/configuration|agent-safety-boundary/i);
  for (const reference of [
    'verify-squad-verdict.mjs', 'CodeQL', 'match-head-commit', 'hand-authored conflict',
  ]) assert.match(prMerge, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
  assert.doesNotMatch(prMerge, /\.squad\/templates\/ralph-reference\.md/i);
  for (const reference of [
    'working tree clean', 'all commits pushed', 'origin/development', 'CLOSED WITHOUT MERGE',
    'verified linked-issue disposition', 'completed, verified deliverable',
  ]) assert.match(terminal, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
});
