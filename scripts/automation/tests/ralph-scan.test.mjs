import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { acquireLock, scan } from '../ralph-scan.mjs';

function issue(number, overrides = {}) {
  return {
    number, state: 'open', title: `Issue ${number}`, body: `Body ${number}`,
    created_at: `2026-01-0${number}T00:00:00Z`, updated_at: '2026-01-01T00:00:00Z',
    labels: [], ...overrides,
  };
}

function pull(number, sha = 'a'.repeat(40), overrides = {}) {
  return {
    number, state: 'open', draft: false, title: `PR ${number}`, updated_at: '2026-01-01T00:00:00Z',
    labels: [], head: { sha }, base: { ref: 'development' }, ...overrides,
  };
}

function transport({ issues = [issue(1), issue(2)], pulls = [pull(3)], dependencies = {}, comments = {}, reviews = {}, checks = {}, statuses = {}, security = [] } = {}) {
  return {
    async get(endpoint) {
      if (endpoint.endsWith('/issues?state=open&per_page=100')) return [issues];
      if (endpoint.endsWith('/pulls?state=open&per_page=100')) return [pulls];
      if (endpoint.includes('/dependencies/blocked_by')) {
        const number = Number(endpoint.match(/issues\/(\d+)/)[1]);
        return [dependencies[number]?.blockedBy ?? []];
      }
      if (endpoint.includes('/dependencies/blocking')) {
        const number = Number(endpoint.match(/issues\/(\d+)/)[1]);
        return [dependencies[number]?.blocking ?? []];
      }
      if (endpoint.includes('/comments?')) {
        const number = Number(endpoint.match(/issues\/(\d+)/)[1]);
        return [comments[number] ?? []];
      }
      if (endpoint.includes('/reviews?')) {
        const number = Number(endpoint.match(/pulls\/(\d+)/)[1]);
        return [reviews[number] ?? []];
      }
      if (endpoint.includes('/check-runs?')) {
        const sha = endpoint.match(/commits\/([^/]+)/)[1];
        return [{ check_runs: checks[sha] ?? [] }];
      }
      if (endpoint.includes('/status?')) {
        const sha = endpoint.match(/commits\/([^/]+)/)[1];
        return [{ statuses: statuses[sha] ?? [] }];
      }
      if (endpoint.includes('/code-scanning/alerts?')) return [security];
      throw new Error(`Unhandled endpoint ${endpoint}`);
    },
  };
}

async function temporaryOptions(t, overrides = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'ralph-scan-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return {
    repo: 'OlyForge3D/PrintFarmer', workflowId: 'workflow-test', stateRoot: root,
    transport: transport(), ...overrides,
  };
}

test('initial and stable scans retain all unresolved issues and drafts', async (t) => {
  const options = await temporaryOptions(t, {
    transport: transport({
      pulls: [pull(3, 'a'.repeat(40), { draft: true })],
      dependencies: { 2: { blockedBy: [{ number: 1, state: 'open', repository_url: 'https://api.github.com/repos/OlyForge3D/PrintFarmer' }] } },
    }),
  });
  const first = await scan(options);
  const second = await scan(options);
  assert.equal(first.baseline, 'initial');
  assert.deepEqual(first.dependencyOrder, [1, 2]);
  assert.equal(first.prs.attention[0].draft, true);
  assert.equal(second.baseline, 'existing');
  assert.equal(second.issues.readyUnresolved.length, 2);
  assert.equal(second.counts.changedIssues, 0);
  assert.equal(second.counts.changedPrs, 0);
});

test('reviews, comment edits, CI reruns, and CodeQL state changes invalidate a PR observation', async (t) => {
  const sha = 'b'.repeat(40);
  const options = await temporaryOptions(t, { transport: transport({ pulls: [pull(3, sha)] }) });
  await scan(options);
  options.transport = transport({
    pulls: [pull(3, sha)],
    comments: { 3: [{ id: 8, updated_at: '2026-01-02T00:00:00Z', body: 'edited verdict' }] },
    reviews: { 3: [{ id: 9, submitted_at: '2026-01-02T00:00:00Z', state: 'APPROVED', body: 'review' }] },
    checks: { [sha]: [{ id: 4, name: 'CI', status: 'completed', conclusion: 'success', started_at: '2026-01-02T00:00:00Z', completed_at: '2026-01-02T00:01:00Z' }] },
    statuses: { [sha]: [{ id: 5, context: 'CodeQL', state: 'pending', updated_at: '2026-01-02T00:00:00Z' }] },
    security: [{ number: 11, state: 'open', updated_at: '2026-01-02T00:00:00Z', rule: { id: 'x', security_severity_level: 'high' } }],
  });
  const result = await scan(options);
  assert.deepEqual(result.changedItems.prs[0].reasons, ['reviews', 'comments', 'checks', 'status']);
  assert.deepEqual(result.changedItems.security, ['alerts']);
});

test('cross-repository and closed blockers remain explicit and never become ready evidence', async (t) => {
  const options = await temporaryOptions(t, {
    transport: transport({
      dependencies: { 2: { blockedBy: [{ number: 99, state: 'closed', repository_url: 'https://api.github.com/repos/example/other' }] } },
    }),
  });
  const result = await scan(options);
  assert.equal(result.graphFlags.unknown, true);
  assert.match(JSON.stringify(result.blockedEdges), /example\/other#99/);
});

test('case-insensitive repository identity preserves local dependency ordering', async (t) => {
  const options = await temporaryOptions(t, {
    repo: 'olyforge3d/printfarmer',
    transport: transport({
      dependencies: { 2: { blockedBy: [{ number: 1, state: 'open', repository_url: 'https://api.github.com/repos/OlyForge3D/PrintFarmer' }] } },
    }),
  });
  const result = await scan(options);
  assert.deepEqual(result.dependencyOrder, [1, 2]);
  assert.equal(result.graphFlags.unknown, false);
});

test('missing CI collection arrays abort before the snapshot advances', async (t) => {
  const options = await temporaryOptions(t);
  await scan(options);
  const stateFile = path.join(options.stateRoot, 'github.com', 'olyforge3d', 'printfarmer', 'workflow-test', 'snapshot.json');
  const before = await readFile(stateFile, 'utf8');
  const base = transport();
  options.transport = {
    async get(endpoint, options) {
      if (endpoint.includes('/check-runs?')) return [{}];
      return base.get(endpoint, options);
    },
  };
  await assert.rejects(() => scan(options), /check_runs returned a non-array/);
  assert.equal(await readFile(stateFile, 'utf8'), before);
});

test('a failed collection preserves the last good snapshot and does not emit an empty success', async (t) => {
  const options = await temporaryOptions(t);
  await scan(options);
  const stateFile = path.join(options.stateRoot, 'github.com', 'olyforge3d', 'printfarmer', 'workflow-test', 'snapshot.json');
  const before = await readFile(stateFile, 'utf8');
  options.transport = { async get() { throw Object.assign(new Error('rate limited'), { code: 'GITHUB_ERROR' }); } };
  await assert.rejects(() => scan(options), /rate limited/);
  assert.equal(await readFile(stateFile, 'utf8'), before);
});

test('malformed pagination, corrupt state, and concurrent locks fail closed', async (t) => {
  const options = await temporaryOptions(t);
  const lock = path.join(options.stateRoot, 'github.com', 'olyforge3d', 'printfarmer', 'workflow-test', 'scan.lock');
  await (await import('node:fs/promises')).mkdir(path.dirname(lock), { recursive: true });
  const release = await acquireLock(lock);
  await assert.rejects(() => scan(options), /already holds/);
  await release();
  options.transport = { async get() { return [{}]; } };
  await assert.rejects(() => scan(options), /non-array page/);
});

test('removed issues and PRs are terminal lookup candidates, not inferred merges', async (t) => {
  const options = await temporaryOptions(t);
  await scan(options);
  options.transport = transport({ issues: [issue(1)], pulls: [] });
  const result = await scan(options);
  assert.deepEqual(result.changedItems.issues.find((item) => item.id === 'issue:2').reasons, ['removed-terminal-lookup-candidate']);
  assert.deepEqual(result.changedItems.prs.find((item) => item.id === 'pr:3').reasons, ['removed-terminal-lookup-candidate']);
});

test('unavailable CodeQL evidence remains unknown instead of passing as clean', async (t) => {
  const base = transport();
  const options = await temporaryOptions(t, {
    transport: {
      async get(endpoint, options) {
        if (endpoint.includes('/code-scanning/alerts?')) {
          throw Object.assign(new Error('forbidden'), { code: 'GITHUB_DENIED' });
        }
        return base.get(endpoint, options);
      },
    },
  });
  const result = await scan(options);
  assert.equal(result.security.availability, 'unknown');
  assert.equal(result.security.reason, 'code-scanning-unavailable');
});

test('corrupt snapshots are retained for diagnosis and require an explicit rebaseline', async (t) => {
  const options = await temporaryOptions(t);
  const directory = path.join(options.stateRoot, 'github.com', 'olyforge3d', 'printfarmer', 'workflow-test');
  await mkdir(directory, { recursive: true });
  await writeFile(path.join(directory, 'snapshot.json'), '{ invalid');
  await assert.rejects(() => scan(options), /Prior state is corrupt/);
  const files = await (await import('node:fs/promises')).readdir(directory);
  assert.ok(files.some((file) => file.startsWith('snapshot.json.corrupt-')));
});
