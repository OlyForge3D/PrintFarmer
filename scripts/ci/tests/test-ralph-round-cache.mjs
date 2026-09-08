import assert from 'node:assert/strict';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {
  RalphCacheError,
  cacheFileForScope,
  collectPaginated,
  compactRoundOutput,
  compareSnapshots,
  createRoundCache,
  isCacheCurrent,
  orderReadyIssues,
  readRoundCache,
  resolveRalphCacheDirectory,
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

test('inherits priority, deduplicates transitive unblock count, and rejects cycles', () => {
  const issues = [
    { number: 1, labels: ['priority:p2'], createdAt: '2026-01-02' },
    { number: 2, labels: ['priority:p0'], createdAt: '2026-01-03' },
    { number: 3, labels: ['priority:p1'], createdAt: '2026-01-01' },
  ];
  const ordered = orderReadyIssues(issues, [
    { blocker: 1, blocked: 2 }, { blocker: 1, blocked: 3 }, { blocker: 2, blocked: 3 },
  ]);
  assert.equal(ordered[0].number, 1);
  assert.equal(ordered[0].effectivePriority, 0);
  assert.equal(ordered[0].unblockValue, 2);
  assert.throws(() => orderReadyIssues(issues, [{ blocker: 1, blocked: 2 }, { blocker: 2, blocked: 1 }]),
    (error) => error.code === 'DEPENDENCY_CYCLE');
});

test('reports compact unchanged and changed fixture output', () => {
  const unchanged = compactRoundOutput({});
  const changed = compactRoundOutput({ changed: ['#12'], ready: ['#12'], blocked: ['#13:#12'], deferred: ['#14'] });
  assert.equal(unchanged, 'changed:— ready:— blocked:— macOS:—');
  assert.equal(changed, 'changed:#12 ready:#12 blocked:#13:#12 macOS:#14');
  assert.ok(changed.length < 80);
});

test('dispatcher routes to required child policies and retains gates', async () => {
  const skill = await readFile('.copilot/skills/ralph-loop/SKILL.md', 'utf8');
  for (const reference of [
    'implementation-pre-pr.md', '.squad/templates/ralph-reference.md', '.github/ralph-reference.md',
    'verify-squad-verdict.mjs', 'one round and exits', 'five implementation/analysis slots maximum',
    'never dispatch, review, or merge it', 'CodeQL completion', 'archive/delete others',
    'Before every dispatch, claim, message, review decision, or merge, fetch',
    'gemini-3.1-pro-preview',
  ]) assert.match(skill, new RegExp(reference.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i'));
});
