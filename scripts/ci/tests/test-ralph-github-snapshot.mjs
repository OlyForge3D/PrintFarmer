import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

import { runSnapshot } from '../ralph-github-snapshot.mjs';
import { readRoundCache } from '../ralph-round-cache.mjs';

async function fixtureDirectory(t) {
  const directory = path.resolve('fixtures', `ralph-github-snapshot-${randomUUID()}`);
  await mkdir(directory, { recursive: true });
  t.after(() => rm(directory, { recursive: true, force: true }));
  return directory;
}

function reader({ issueTitle = 'one', fail = false, responses = {} } = {}) {
  const metrics = { requests: 0, pages: 0, responseBytes: 0 };
  return {
    metrics,
    async page(endpoint) {
      if (fail) throw new Error('rate limited');
      metrics.requests += 1;
      metrics.pages += 1;
      const body = responses[endpoint] ?? (endpoint.includes('/issues?') ? [
        { number: 1, state: 'open', title: issueTitle, body: '', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', labels: [], assignees: [] },
      ] : []);
      metrics.responseBytes += Buffer.byteLength(JSON.stringify(body));
      return body;
    },
  };
}

test('uses the authorized cache helper for initial and delta observations', async (t) => {
  const directory = await fixtureDirectory(t);
  const input = {
    scope: { repository: 'OlyForge3D/PrintFarmer', workflow: '5edfe068-4f7c-4734-a078-8ee6fba95918' },
    policyVersion: '2026-09-08', cacheOptions: { env: { RALPH_CACHE_DIR: directory } },
  };
  const first = await runSnapshot({ ...input, reader: reader() });
  const second = await runSnapshot({ ...input, reader: reader() });
  const changed = await runSnapshot({ ...input, reader: reader({ issueTitle: 'changed' }) });
  assert.equal(first.baseline, 'initial');
  assert.equal(first.conclusions.observation, 'deep-scan-required');
  assert.equal(second.baseline, 'existing');
  assert.equal(second.conclusions.observation, 'deep-scan-required');
  assert.equal(second.conclusions.cacheReason, 'coverage-incomplete');
  assert.deepEqual(second.conclusions.changed, []);
  assert.deepEqual(changed.conclusions.changed, ['issues']);
  assert.ok(changed.conclusions.metrics.requests > 0);
});

test('failed reads do not replace the helper-managed baseline', async (t) => {
  const directory = await fixtureDirectory(t);
  const input = {
    scope: { repository: 'OlyForge3D/PrintFarmer', workflow: '5edfe068-4f7c-4734-a078-8ee6fba95918' },
    policyVersion: '2026-09-08', cacheOptions: { env: { RALPH_CACHE_DIR: directory } },
  };
  await runSnapshot({ ...input, reader: reader() });
  await assert.rejects(() => runSnapshot({ ...input, reader: reader({ fail: true }) }), /rate limited/);
});

test('CodeQL permission denial is explicit while other CodeQL failures fail closed', async (t) => {
  const directory = await fixtureDirectory(t);
  const input = {
    scope: { repository: 'OlyForge3D/PrintFarmer', workflow: '5edfe068-4f7c-4734-a078-8ee6fba95918' },
    policyVersion: '2026-09-08', cacheOptions: { env: { RALPH_CACHE_DIR: directory } },
  };
  const denied = reader();
  const original = denied.page;
  denied.page = async (endpoint, page) => {
    if (endpoint.includes('/code-scanning/alerts')) {
      throw Object.assign(new Error('forbidden'), { code: 'GITHUB_DENIED' });
    }
    return original(endpoint, page);
  };
  const result = await runSnapshot({ ...input, reader: denied });
  assert.equal(result.complete, true);
  assert.equal(result.conclusions.observation, 'deep-scan-required');
  assert.equal(result.conclusions.coverage.codeql, 'unknown');
  const steadyDenied = await runSnapshot({ ...input, reader: denied });
  assert.equal(steadyDenied.conclusions.observation, 'deep-scan-required');
  assert.equal(steadyDenied.conclusions.cacheReason, 'codeql-unavailable');

  const failed = reader();
  const failingOriginal = failed.page;
  failed.page = async (endpoint, page) => {
    if (endpoint.includes('/code-scanning/alerts')) throw new Error('rate limited');
    return failingOriginal(endpoint, page);
  };
  await assert.rejects(() => runSnapshot({ ...input, reader: failed }), /rate limited/);
});

test('malformed CodeQL alerts fail without advancing the cache baseline', async (t) => {
  const directory = await fixtureDirectory(t);
  const input = {
    scope: { repository: 'OlyForge3D/PrintFarmer', workflow: '5edfe068-4f7c-4734-a078-8ee6fba95918' },
    policyVersion: '2026-09-08', cacheOptions: { env: { RALPH_CACHE_DIR: directory } },
  };
  await runSnapshot({ ...input, reader: reader() });
  const malformed = reader();
  const original = malformed.page;
  malformed.page = async (endpoint, page) => endpoint.includes('/code-scanning/alerts')
    ? [{ number: 1 }]
    : original(endpoint, page);
  await assert.rejects(() => runSnapshot({ ...input, reader: malformed }), /CodeQL alerts returned incomplete data/);
  const changed = await runSnapshot({ ...input, reader: reader({ issueTitle: 'changed' }) });
  assert.deepEqual(changed.conclusions.changed, ['issues']);
});

const repository = 'OlyForge3D/PrintFarmer';
const headSha = 'a'.repeat(40);
const timestamp = '2026-09-08T00:00:00Z';
const endpoints = {
  comments: `/repos/${repository}/issues/2/comments`,
  reviews: `/repos/${repository}/pulls/2/reviews`,
  checks: `/repos/${repository}/commits/${headSha}/check-runs`,
  statuses: `/repos/${repository}/commits/${headSha}/statuses`,
};
const validEntries = {
  comments: { id: 11, body: 'review evidence', updated_at: timestamp },
  reviews: { id: 21, body: 'review body', state: 'APPROVED', submitted_at: timestamp, commit_id: headSha },
  checks: { id: 31, name: 'unit tests', status: 'completed', conclusion: 'success', completed_at: timestamp },
  statuses: { id: 41, context: 'build', state: 'success', updated_at: timestamp },
};

function prReader(overrides = {}) {
  return reader({
    responses: {
      [`/repos/${repository}/pulls?state=open`]: [{
        number: 2, state: 'open', draft: false, title: 'PR', updated_at: timestamp,
        head: { sha: headSha }, base: { ref: 'development' }, labels: [],
      }],
      ...Object.fromEntries(Object.entries(endpoints).map(([name, endpoint]) =>
        [endpoint, overrides[name] ?? [validEntries[name]]])),
    },
  });
}

async function prInput(t) {
  return {
    scope: { repository, workflow: 'snapshot-entry-validation' },
    policyVersion: '2026-09-08',
    cacheOptions: { env: { RALPH_CACHE_DIR: await fixtureDirectory(t) } },
  };
}

for (const [context, valid] of Object.entries(validEntries)) {
  test(`malformed PR ${context} entries abort before writing and preserve the full baseline`, async (t) => {
    const input = await prInput(t);
    await runSnapshot({ ...input, reader: prReader() });
    const baseline = await readRoundCache(input.scope, input.cacheOptions);
    assert.equal(baseline.reason, undefined);
    assert.equal(baseline.cache.comparisons.prs[2][context].length, 1);
    let writeAttempts = 0;
    input.cacheOptions.writeLockMetadata = async () => {
      writeAttempts += 1;
      assert.fail('Malformed evidence must not reach writeRoundCache.');
    };

    const malformed = [undefined, null, false, 42, 'invalid', [], {}]
      .map((entry) => ({ description: `entry ${JSON.stringify(entry)}`, entry }));
    for (const field of Object.keys(valid)) {
      const missing = { ...valid };
      delete missing[field];
      malformed.push({ description: `missing ${field}`, entry: missing });
      const invalidValues = field === 'id'
        ? [null, false, '11', 0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1, [], {}]
        : [false, 42, [], {}];
      if (field !== 'id' && !['commit_id', 'conclusion', 'completed_at'].includes(field)) invalidValues.push(null);
      if (field !== 'body' && field !== 'id') invalidValues.push('');
      for (const value of invalidValues) {
        malformed.push({ description: `${field}=${JSON.stringify(value)}`, entry: { ...valid, [field]: value } });
      }
    }
    if (context === 'reviews') {
      malformed.push({
        description: 'pending review with non-string submission time',
        entry: { ...valid, state: 'PENDING', submitted_at: 42 },
      });
    }
    for (const { description, entry } of malformed) {
      for (const entries of [[entry], [valid, entry]]) {
        await assert.rejects(
          () => runSnapshot({ ...input, reader: prReader({ [context]: entries }) }),
          new RegExp(`${context} returned incomplete data`),
          description,
        );
        assert.deepEqual(await readRoundCache(input.scope, input.cacheOptions), baseline, description);
      }
    }
    assert.equal(writeAttempts, 0);
  });
}

test('valid PR evidence stays comparable and each fingerprint field detects changes', async (t) => {
  const input = await prInput(t);
  await runSnapshot({ ...input, reader: prReader() });
  const unchanged = await runSnapshot({ ...input, reader: prReader() });
  assert.deepEqual(unchanged.conclusions.changed, []);
  assert.equal(unchanged.conclusions.observation, 'deep-scan-required');
  for (const [context, valid] of Object.entries(validEntries)) {
    for (const [field, value] of Object.entries(valid)) {
      const changed = { ...valid, [field]: field === 'id' ? value + 1 : `${value}-changed` };
      const result = await runSnapshot({ ...input, reader: prReader({ [context]: [changed] }) });
      assert.deepEqual(result.conclusions.changed, ['prs'], `${context}.${field}`);
      assert.equal(result.conclusions.observation, 'deep-scan-required');
      await runSnapshot({ ...input, reader: prReader() });
    }
  }
});

test('empty bodies, pending reviews and unfinished checks retain valid nullable fields', async (t) => {
  const input = await prInput(t);
  const pendingReview = { ...validEntries.reviews, body: '', state: 'PENDING', commit_id: null };
  delete pendingReview.submitted_at;
  const pending = {
    comments: [{ ...validEntries.comments, body: '' }],
    reviews: [pendingReview, { ...pendingReview, id: 22, submitted_at: null }],
    checks: [{ ...validEntries.checks, status: 'in_progress', conclusion: null, completed_at: null }],
  };
  await runSnapshot({ ...input, reader: prReader(pending) });
  const unchanged = await runSnapshot({ ...input, reader: prReader(pending) });
  assert.deepEqual(unchanged.conclusions.changed, []);
  const { cache } = await readRoundCache(input.scope, input.cacheOptions);
  assert.equal(cache.comparisons.prs[2].comments[0].body, '');
  assert.equal(cache.comparisons.prs[2].reviews[0].state, 'PENDING');
  assert.equal(cache.comparisons.prs[2].reviews[0].commitId, '');
  assert.equal(cache.comparisons.prs[2].reviews[0].updatedAt, '');
  assert.equal(cache.comparisons.prs[2].checks[0].conclusion, '');
  assert.equal(cache.comparisons.prs[2].checks[0].updatedAt, '');
  const completed = await runSnapshot({ ...input, reader: prReader() });
  assert.deepEqual(completed.conclusions.changed, ['prs']);
  assert.equal(completed.conclusions.observation, 'deep-scan-required');
});
