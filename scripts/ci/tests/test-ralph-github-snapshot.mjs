import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { runSnapshot } from '../ralph-github-snapshot.mjs';

function reader({ issueTitle = 'one', fail = false } = {}) {
  const metrics = { requests: 0, pages: 0, responseBytes: 0 };
  return {
    metrics,
    async page(endpoint) {
      if (fail) throw new Error('rate limited');
      metrics.requests += 1;
      metrics.pages += 1;
      const body = endpoint.includes('/issues?') ? [
        { number: 1, state: 'open', title: issueTitle, body: '', created_at: '2026-01-01T00:00:00Z', updated_at: '2026-01-01T00:00:00Z', labels: [], assignees: [] },
      ] : [];
      metrics.responseBytes += Buffer.byteLength(JSON.stringify(body));
      return body;
    },
  };
}

test('uses the authorized cache helper for initial and delta observations', async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ralph-github-snapshot-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
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
  assert.deepEqual(second.conclusions.changed, []);
  assert.deepEqual(changed.conclusions.changed, ['issues']);
  assert.ok(changed.conclusions.metrics.requests > 0);
});

test('failed reads do not replace the helper-managed baseline', async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ralph-github-snapshot-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const input = {
    scope: { repository: 'OlyForge3D/PrintFarmer', workflow: '5edfe068-4f7c-4734-a078-8ee6fba95918' },
    policyVersion: '2026-09-08', cacheOptions: { env: { RALPH_CACHE_DIR: directory } },
  };
  await runSnapshot({ ...input, reader: reader() });
  await assert.rejects(() => runSnapshot({ ...input, reader: reader({ fail: true }) }), /rate limited/);
});

test('CodeQL permission denial is explicit while other CodeQL failures fail closed', async (t) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'ralph-github-snapshot-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
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
