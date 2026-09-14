import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import { readOnlyClient, rehearsalReadUrl } from '../release-rehearsal.mjs';

const repositoryApi = 'https://api.github.com/repos/OlyForge3D/PrintFarmer/';

test('internal diagnostics are hidden, environment-free and least privilege', () => {
  const source = readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8');
  const workflow = load(source);
  assert.ok(workflow.on.workflow_call);
  assert.equal(workflow.on.workflow_dispatch, undefined);
  assert.deepEqual(Object.keys(workflow.jobs), ['diagnostics']);
  assert.equal(workflow.jobs.diagnostics.environment, undefined);
  assert.deepEqual(workflow.permissions, { contents: 'read' });
  assert.deepEqual(workflow.jobs.diagnostics.permissions, {
    contents: 'read',
    actions: 'read',
    checks: 'read',
    statuses: 'read',
    'pull-requests': 'read',
  });
  assert.doesNotMatch(source,
    /secrets\.|vars\.|environment:|id-token:|packages:|contents: write|create-github-app-token/);
});

test('internal diagnostics cannot call publisher or mutation operations', () => {
  const source = readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8');
  assert.doesNotMatch(source,
    /release-control\.mjs (?:authorize|preflight|advance)|release-set\.mjs tag|docker-publish\.yml/);
  assert.doesNotMatch(source,
    /RELEASE_PUBLISHER|RELEASE_REGISTRY|cosign|docker login|gh release|git\/refs|git\/tags/);
  assert.match(source, /release-transaction\.mjs validate/);
  assert.match(source, /actions\/download-artifact@[0-9a-f]{40}/);
  assert.match(source, /release-transaction\.mjs diagnose/);
  assert.match(source, /rehearsal-receipt\.json/);
});

test('trusted release dispatch supplies complete evidence to the hidden diagnostic path', () => {
  const source = readFileSync('.github/workflows/consolidated-release.yml', 'utf8');
  const workflow = load(source);
  const diagnostics = workflow.jobs['internal-diagnostics'];
  assert.equal(diagnostics.uses, './.github/workflows/release-protection-rehearsal.yml');
  assert.deepEqual(diagnostics.needs, ['admit', 'qualification', 'collect-qualification']);
  assert.equal(diagnostics.with.transaction, '${{ needs.admit.outputs.transaction }}');
  assert.equal(diagnostics.with.qualification_artifact,
    'release-qualification-${{ github.run_id }}');
  assert.equal(diagnostics.environment, undefined);
  assert.equal(diagnostics.secrets, undefined);
  assert.deepEqual(diagnostics.permissions, {
    contents: 'read',
    actions: 'read',
    checks: 'read',
    statuses: 'read',
    'pull-requests': 'read',
  });
  assert.ok(workflow.jobs.publish.needs.includes('internal-diagnostics'));
});

test('diagnostic API client accepts only bounded repository reads', async () => {
  const calls = [];
  const api = readOnlyClient('read-token', async (url, options) => {
    calls.push({ url, options });
    return new Response(JSON.stringify({ ok: true }), { status: 200 });
  });
  assert.deepEqual(await api('git/ref/heads/main'), { ok: true });
  assert.equal(calls[0].url, `${repositoryApi}git/ref/heads/main`);
  assert.equal(calls[0].options.method, 'GET');
  assert.equal(calls[0].options.headers.Authorization, 'Bearer read-token');
  for (const endpoint of [
    'git/refs',
    'git/tags',
    'releases',
    'actions/workflows/consolidated-release.yml/dispatches',
    'environments/release-stable',
    '../users',
  ]) {
    await assert.rejects(api(endpoint),
      /Unapproved rehearsal read URL|route or method is not allowlisted/);
  }
});

test('diagnostic URL allowlist excludes publication and environment endpoints', () => {
  for (const endpoint of [
    'git/ref/heads/main',
    'git/ref/heads/development',
    'git/ref/heads/release-ledger',
    'releases?per_page=100&page=1',
    'actions/workflows/consolidated-release.yml/runs?status=queued&per_page=100',
  ]) {
    assert.equal(rehearsalReadUrl(endpoint), `${repositoryApi}${endpoint}`);
  }
  for (const endpoint of [
    'environments/release-stable',
    'environments/release-insider',
    'packages/container/printfarmer-api/versions/1',
  ]) {
    assert.throws(() => rehearsalReadUrl(endpoint), /Unapproved rehearsal read URL/);
  }
});
