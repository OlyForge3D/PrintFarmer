import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { createHash, generateKeyPairSync, verify } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { load } from 'js-yaml';
import { canonicalAuthorizationFixture } from './fixtures/canonical-qualification.mjs';
import { canonicalValidationChecks } from '../canonical-qualification.mjs';
import { parseTag, releaseRequiredChecks, releaseReviewStatus, repository } from '../release-policy.mjs';
import { rehearsalAdmissionSource } from '../release-rehearsal-admission.mjs';
import {
  fixtureAppId, fixtureRequestUrl, fixtureWriter, intendedFixture, provisionFixture,
  verifyFixtureEnvironment, verifyFixtureInventory, withFixtureToken,
} from '../release-rehearsal-fixture.mjs';
import { verifyFixtureTarget, verifyFixtureWorkflow } from '../release-rehearsal-target.mjs';
import {
  allPages, digest, packageNames, positiveRehearsal, readOnlyClient, rehearsalContext,
  rehearsalReadUrl, rehearsalWorkflow, snapshot, verifyUnchanged,
} from '../release-rehearsal.mjs';
import {
  cleanupUrl, probeRequestUrl, runDenialProbes, verifyGitDenial, verifyUploadDenial,
} from '../release-rehearsal-probes.mjs';

const sha = 'a'.repeat(40);
const anchor = 'c'.repeat(40);
const head = 'd'.repeat(40);
const tree = 'e'.repeat(40);
const blob = 'f'.repeat(40);
const child = '1'.repeat(40);
const env = {
  GITHUB_REPOSITORY: repository, GITHUB_EVENT_NAME: 'workflow_dispatch',
  GITHUB_REF: 'refs/heads/development', GITHUB_RUN_ATTEMPT: '1', GITHUB_RUN_ID: '42',
  GITHUB_WORKFLOW_REF: `${repository}/${rehearsalWorkflow}@refs/heads/development`,
  GITHUB_SHA: sha, GITHUB_WORKFLOW_SHA: sha, RELEASE_LEDGER_ANCHOR: anchor,
  RELEASE_APPROVAL_MODE: 'single-maintainer', REHEARSAL_CHANNEL: 'insider',
};
const context = rehearsalContext(env);
const response = (body, status = 200, headers = {}) =>
  new Response(status === 204 ? undefined : JSON.stringify(body), { status, headers });
const denied = operation => response({ message: `Repository rule violations found\n\n${
  operation === 'create' ? 'Cannot create ref due to creations being restricted.' :
    operation === 'update' ? 'Cannot update this protected ref.' :
      'Cannot update ref due to updates being restricted.'}` }, 422);

function fixture() {
  const calls = [];
  const { values, status } = canonicalAuthorizationFixture(sha, 'insider', 'single-maintainer');
  const completedAt = new Date(Date.now() - 8 * 60_000).toISOString();
  values.set('', { full_name: repository, default_branch: 'development', permissions: { push: true } });
  const checks = [...releaseRequiredChecks, ...canonicalValidationChecks].filter(name => name !== releaseReviewStatus);
  values.set(`commits/${sha}/check-runs?per_page=100`, {
    total_count: checks.length, check_runs: checks.map((name, index) => ({
      id: index + 1, name, head_sha: sha, status: 'completed', conclusion: 'success',
      started_at: completedAt, completed_at: completedAt,
      check_suite: { id: 100 }, app: { slug: 'github-actions', id: 99 },
      url: `https://api.github.com/repos/${repository}/check-runs/${index + 1}`,
    })),
  });
  values.set(`commits/${sha}/status?per_page=100`, { sha, total_count: 1, statuses: [status] });
  const rules = [
    { type: 'deletion' }, { type: 'non_fast_forward' },
    { type: 'pull_request', parameters: { require_code_owner_review: false,
      required_approving_review_count: 0, required_review_thread_resolution: true,
      require_last_push_approval: false, dismiss_stale_reviews_on_push: true } },
    { type: 'required_status_checks', parameters: { strict_required_status_checks_policy: true,
      required_status_checks: [...releaseRequiredChecks, ...canonicalValidationChecks].map(context => ({ context })) } },
  ].map(rule => ({ ...rule, ruleset_id: 5 }));
  values.set('rules/branches/development?per_page=100', rules);
  values.set('rulesets/5', { id: 5, name: 'protected-release-branches', enforcement: 'active', target: 'branch',
    rules, bypass_actors: [], conditions: { ref_name: {
      include: ['refs/heads/main', 'refs/heads/development'], exclude: [],
    } } });
  const rulesets = ['release-canonical-tags', 'release-ledger-continuity',
    'release-tag-creators', 'release-ledger-writer'].map((name, index) => ({
    id: index + 1, name, enforcement: 'active', target: index % 2 === 0 ? 'tag' : 'branch',
    conditions: { ref_name: { include: [index % 2 === 0 ? 'refs/tags/v*' : 'refs/heads/release-ledger'], exclude: [] } },
    bypass_actors: index < 2 ? [] : [{ actor_type: 'Integration', actor_id: 123 }],
    rules: (index === 0 ? ['update', 'deletion'] : index === 1 ? ['non_fast_forward', 'deletion'] :
      index === 2 ? ['creation'] : ['update']).map(type => ({ type })),
  }));
  values.set('rulesets?per_page=100', rulesets);
  for (const entry of rulesets) values.set(`rulesets/${entry.id}`, entry);
  values.set('environments/release-insider', { name: 'release-insider', can_admins_bypass: false,
    deployment_branch_policy: { custom_branch_policies: true },
    privateMarker: 'DO-NOT-EMIT-ENVIRONMENT',
    protection_rules: [{ type: 'required_reviewers', prevent_self_review: false,
      reviewers: [{ type: 'User', reviewer: { id: 7, login: 'jpapiez' } }] }] });
  values.set('environments/release-insider/deployment-branch-policies',
    { branch_policies: [{ name: 'development', type: 'branch' }] });
  values.set('environments/release-publisher-insider', {
    name: 'release-publisher-insider',
    can_admins_bypass: false,
    deployment_branch_policy: { custom_branch_policies: true, protected_branches: false },
    protection_rules: [],
  });
  values.set('environments/release-publisher-insider/deployment-branch-policies',
    { branch_policies: [{ name: 'development', type: 'branch' }] });
  for (const [branch, id] of [['development', sha], ['main', 'b'.repeat(40)], ['release-ledger', head]]) {
    values.set(`git/ref/heads/${branch}`, { ref: `refs/heads/${branch}`, object: { type: 'commit', sha: id } });
  }
  values.set('git/matching-refs/tags?per_page=100&page=1', [
    { ref: `refs/tags/${context.marker}-update`, object: { type: 'commit', sha: head } },
    { ref: 'refs/tags/v0.2.2', object: { type: 'tag', sha } },
    { ref: 'refs/tags/ios/v0.2.2-beta.1', object: { type: 'tag', sha } },
  ]);
  const state = { schema: 1, anchor, counter: '0', lastHistoricalStable: '0.2.2',
    reservations: {}, identities: {}, pointers: {}, stages: {}, qualifications: {} };
  values.set(`compare/${anchor}...${head}`, { status: 'ahead' });
  values.set(`git/commits/${head}`, { sha: head, tree: { sha: tree }, parents: [{ sha: anchor }] });
  values.set(`git/commits/${anchor}`, { sha: anchor, tree: { sha } });
  values.set(`git/commits/${child}`, { sha: child, tree: { sha: tree }, parents: [{ sha: head }] });
  values.set(`git/trees/${tree}`, { sha: tree, truncated: false,
    tree: [{ path: 'state.json', type: 'blob', sha: blob }] });
  values.set(`git/blobs/${blob}`, { encoding: 'base64', content: Buffer.from(JSON.stringify(state)).toString('base64') });
  values.set('releases?per_page=100&page=1',
    [{ id: 101, tag_name: 'v0.2.2', draft: false, prerelease: false, body: 'DO-NOT-EMIT-RELEASE-BODY' }]);
  values.set('releases/101/assets?per_page=100&page=1',
    [{ id: 102, name: 'artifact.zip', size: 100, digest: `sha256:${'2'.repeat(64)}` }]);
  for (const [index, name] of packageNames.entries()) {
    values.set(`packages/${name}`, { id: index + 1, name, package_type: 'container', version_count: 1 });
    values.set(`packages/${name}/versions?per_page=100&page=1`,
      [{ id: index + 1, name: `sha256:${'3'.repeat(64)}`, metadata: { container: { tags: ['historical'] } } }]);
  }
  values.set('actions/workflows/release-protection-rehearsal.yml',
    { id: 1234, path: rehearsalWorkflow, state: 'active' });
  values.set('actions/runs/42', { id: 42, workflow_id: 1234, path: rehearsalWorkflow, event: 'workflow_dispatch',
    run_attempt: 1, head_sha: sha, head_branch: 'development', status: 'in_progress',
    repository: { full_name: repository }, head_repository: { full_name: repository } });
  const fetcher = async (url, options) => {
    calls.push({ url, ...options });
    assert.equal(options.redirect, 'error');
    assert.ok(options.signal instanceof AbortSignal);
    const parsed = new URL(url);
    if (parsed.hostname === 'ghcr.io') {
      if (parsed.pathname === '/token') {
        assert.equal(parsed.searchParams.get('service'), 'ghcr.io');
        assert.ok(packageNames.some(name =>
          parsed.searchParams.get('scope') === `repository:olyforge3d/${name}:pull,push`));
        assert.ok(parsed.search.includes('repository%3Aolyforge3d%2F'));
        return response({ token: 'REGISTRY-TOKEN-SENTINEL' });
      }
      if (parsed.pathname.endsWith('/tags/list')) {
        return response({ name: parsed.pathname.slice(4, -10), tags: ['historical'] });
      }
      if (options.method === 'POST' && parsed.pathname.endsWith('/blobs/uploads/')) {
        assert.equal(options.body, undefined);
        return response({ errors: [{ code: 'DENIED', message: 'permission_denied: write_package' }] }, 403);
      }
      throw new Error('Unexpected registry route');
    }
    assert.equal(parsed.hostname, 'api.github.com');
    const endpoint = parsed.pathname.startsWith('/orgs/') ?
      `packages/${parsed.pathname.split('/container/')[1]}${parsed.search}` :
      parsed.pathname.replace(`/repos/${repository}`, '').replace(/^\//, '') + parsed.search;
    if (options.method === 'GET') {
      if (/actions\/workflows\/(?:consolidated-release|docker-publish).*status=/.test(endpoint)) {
        return response({ total_count: 0, workflow_runs: [] });
      }
      assert.ok(values.has(endpoint), `Unmocked GET ${endpoint}`);
      return response(values.get(endpoint));
    }
    if (endpoint === 'git/refs') return denied('create');
    if (endpoint === 'git/commits') return response({ sha: child }, 201);
    if (endpoint === `git/refs/tags/${context.marker}-update`) return denied('update');
    if (endpoint === 'git/refs/heads/release-ledger') return denied('ledger');
    throw new Error('Unexpected mutation');
  };
  return { calls, values, fetcher };
}

async function probeSettings(f) {
  return { requested: true, actor: 'jpapiez',
    positiveDigest: digest(await snapshot(readOnlyClient('generic', f.fetcher), context)) };
}

test('canonical context rejects fork, feature, PR, rerun, wrong workflow and SHA drift', () => {
  for (const overrides of [
    { GITHUB_REPOSITORY: 'outsider/fork' }, { GITHUB_REF: 'refs/heads/feature' },
    { GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_RUN_ATTEMPT: '2' },
    { GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/consolidated-release.yml@refs/heads/development` },
    { GITHUB_WORKFLOW_SHA: head }, { REHEARSAL_CHANNEL: 'stable' }, { GITHUB_SHA: 'bad' },
  ]) assert.throws(() => rehearsalContext({ ...env, ...overrides }));
  assert.throws(() => parseTag(context.marker));
  assert.throws(() => parseTag(`${context.marker}-update`));
});

test('App adapter forbids every mutation, unapproved host/path, and GET bodies before transport', async () => {
  const calls = [];
  const api = readOnlyClient('app', async (...args) => { calls.push(args); return response({}); });
  for (const path of ['git/refs', 'git/tags', 'git/commits', 'git/blobs', 'git/trees',
    'git/refs/heads/release-ledger', 'releases', 'statuses/' + sha, 'actions/workflows/docker-publish.yml/dispatches']) {
    for (const method of ['POST', 'PATCH', 'PUT', 'DELETE']) await assert.rejects(api(path, method), /GET-only/);
  }
  for (const path of ['https://evil.example', '//evil.example', '../git/refs', 'packages/unapproved',
    'git/ref/heads/feature', 'git/ref/heads/development\r\nX-Test: x']) await assert.rejects(api(path));
  await assert.rejects(api('git/ref/heads/development', 'GET', {}), /GET-only/);
  assert.equal(calls.length, 0);
  assert.throws(() => rehearsalReadUrl('packages/printfarmer-evil'));
});

test('App adapter rejects redirects, malformed data, auth, throttling and network errors without leaking details', async () => {
  for (const implementation of [
    async () => response({}, 302, { location: 'https://evil.example/SECRET' }),
    async () => new Response('SECRET-invalid-json'),
    async () => response({ message: 'SECRET' }, 401),
    async () => response({ message: 'SECRET' }, 429),
    async () => { throw new Error('SECRET-network'); },
  ]) {
    await assert.rejects(readOnlyClient('APP-SECRET', implementation)('git/ref/heads/development'),
      error => !error.message.includes('SECRET'));
  }
});

test('positive rehearsal executes real policy, ledger and qualification verifiers using GET only', async () => {
  const f = fixture();
  const receipt = await positiveRehearsal(readOnlyClient('app', f.fetcher),
    readOnlyClient('generic', f.fetcher), context, { appId: '123' });
  assert.equal(receipt.commitStatusReadObserved, true);
  assert.equal(receipt.commitStatusGrantEvidenceRequired, true);
  assert.equal(Object.hasOwn(receipt, 'statusesReadVerified'), false);
  assert.equal(receipt.inventoryDigest, digest(receipt.after));
  assert.deepEqual(receipt.before, receipt.after);
  assert.ok(f.calls.every(call => call.method === 'GET'));
  assert.ok(f.calls.some(call => call.headers.Authorization === 'Bearer app' && call.url.includes('/status?')));
  assert.doesNotMatch(JSON.stringify(receipt), /DO-NOT-EMIT|TOKEN-SENTINEL|authorization|allocationKey/);
});

test('denied App status read or weakened environment fails before any mutation', async () => {
  for (const fault of ['statuses', 'environment']) {
    const f = fixture();
    if (fault === 'environment') f.values.get('environments/release-insider').can_admins_bypass = true;
    const fetcher = async (url, options) =>
      fault === 'statuses' && options.headers.Authorization === 'Bearer app' && url.includes('/status?') ?
        response({}, 403) : f.fetcher(url, options);
    await assert.rejects(positiveRehearsal(readOnlyClient('app', fetcher),
      readOnlyClient('generic', fetcher), context, { appId: '123' }));
    assert.ok(f.calls.every(call => call.method === 'GET'));
  }
});

test('inventory pagination rejects repeated, malformed, untrusted and unprovably complete pages', async () => {
  const first = Array.from({ length: 100 }, (_, index) => ({ id: index + 1 }));
  const api = async path => path.endsWith('page=1') ? first : [{ id: 101 }];
  api.nextPages = new Map([['releases?per_page=100&page=1',
    `<${rehearsalReadUrl('releases?per_page=100&page=2')}>; rel="next"`]]);
  assert.equal((await allPages(api, 'releases')).length, 101);
  await assert.rejects(allPages(async () => first, 'releases'), /completeness/);
  const repeat = async () => first;
  repeat.nextPages = api.nextPages;
  await assert.rejects(allPages(repeat, 'releases'), /Repeated/);
  api.nextPages.set('releases?per_page=100&page=1', '<https://evil.example>; rel="next"');
  await assert.rejects(allPages(api, 'releases'), /Untrusted/);
  for (const value of [{}, [undefined], [{ noId: 1 }]]) await assert.rejects(allPages(async () => value, 'releases'));
});

test('inventories cover historical refs, ledger, releases/assets and every required package', async () => {
  const f = fixture();
  const before = await snapshot(readOnlyClient('generic', f.fetcher), context);
  assert.equal(before.tags.length, 3);
  assert.deepEqual(Object.keys(before.packages), packageNames);
  assert.equal(before.releases[0].assets.length, 1);
  for (const mutate of [
    value => value.tags.pop(), value => { value.tags[0].sha = child; },
    value => { value.ledger.head = child; }, value => { value.ledger.tree = child; },
    value => { value.ledger.state = 'changed'; }, value => value.releases.pop(),
    value => value.releases[0].assets.pop(),
    value => value.packages[packageNames[0]].versions.pop(),
  ]) {
    const after = structuredClone(before);
    mutate(after);
    assert.throws(() => verifyUnchanged(before, after), /changed/);
  }
  f.values.get(`packages/${packageNames[0]}`).version_count++;
  await assert.rejects(snapshot(readOnlyClient('generic', f.fetcher), context), /count mismatch/);
});

test('probe transport rejects arbitrary mutations, canonical tags, force and altered-tree commits', () => {
  const before = { ledger: { head, tree } };
  assert.equal(probeRequestUrl('git/refs', 'POST',
    { ref: `refs/tags/${context.marker}`, sha }, context, before), `https://api.github.com/repos/${repository}/git/refs`);
  for (const [path, method, body] of [
    ['git/refs', 'POST', { ref: 'refs/tags/v1.2.3', sha }],
    ['git/refs', 'POST', { ref: `refs/tags/${context.marker}`, sha: head }],
    ['git/refs/heads/main', 'PATCH', { sha: child, force: false }],
    ['git/refs/heads/release-ledger', 'PATCH', { sha: child, force: true }],
    ['git/refs/heads/release-ledger', 'PATCH', { sha: head, force: false }],
    ['git/commits', 'POST', { tree: sha, parents: [head] }],
    ['git/blobs', 'POST', { content: 'bad' }],
    ['releases', 'POST', {}], ['git/refs', 'DELETE', {}],
  ]) assert.throws(() => probeRequestUrl(path, method, body, context, before, child));
});

test('all nine target denials pass with unchanged inventories and one explicit same-tree Git object', async () => {
  const f = fixture();
  const settings = await probeSettings(f);
  f.calls.length = 0;
  const result = await runDenialProbes('generic', context, settings, f.fetcher);
  assert.equal(result.passed, true, JSON.stringify(result));
  assert.equal(result.denials.length, 9);
  assert.equal(result.uploads.length, packageNames.length);
  assert.equal(result.attempts.length, 4);
  assert.deepEqual(result.before, result.after);
  assert.equal(result.fixtures.filter(entry => entry.kind === 'same-tree-ledger-child').length, 1);
  const writes = f.calls.filter(call => call.method !== 'GET');
  assert.equal(writes.length, 10);
  assert.ok(writes.every(call => !/manifests|dispatches|releases|statuses|git\/tags/.test(call.url)));
  const gitCommit = writes.find(call => call.url.endsWith('/git/commits'));
  assert.deepEqual(JSON.parse(gitCommit.body).parents, [head]);
  assert.equal(JSON.parse(gitCommit.body).tree, tree);
  assert.doesNotMatch(JSON.stringify(result), /REGISTRY-TOKEN-SENTINEL|DO-NOT-EMIT/);
});

test('missing explicit request, mismatched digest, absent fixture and insufficient generic permissions never write', async () => {
  for (const fault of ['request', 'digest', 'fixture', 'permissions', 'active']) {
    const f = fixture();
    const settings = await probeSettings(f);
    if (fault === 'request') settings.requested = false;
    if (fault === 'digest') settings.positiveDigest = '0'.repeat(64);
    if (fault === 'fixture') {
      f.values.get('git/matching-refs/tags?per_page=100&page=1').shift();
      settings.positiveDigest = (await probeSettings(f)).positiveDigest;
    }
    if (fault === 'permissions') f.values.get('').permissions.push = false;
    const fetcher = async (url, options) => fault === 'active' && url.includes('status=waiting') ?
      response({ total_count: 1, workflow_runs: [{}] }) : f.fetcher(url, options);
    try {
      const result = await runDenialProbes('generic', context, settings, fetcher);
      assert.equal(result.passed, false);
    } catch (error) {
      assert.equal(fault, 'request', error.message);
    }
    assert.ok(f.calls.every(call => call.method === 'GET'));
  }
});

test('unexpected Git success at each target stops later probes and preserves after inventory', async () => {
  for (const target of ['/git/refs', `/git/refs/tags/${context.marker}-update`, '/git/refs/heads/release-ledger']) {
    const f = fixture();
    const settings = await probeSettings(f);
    const writes = [];
    const fetcher = async (url, options) => {
      if (options.method !== 'GET') {
        writes.push(url);
        if (url.endsWith(target)) return response({ ref: 'inert' }, 200);
      }
      return f.fetcher(url, options);
    };
    const result = await runDenialProbes('generic', context, settings, fetcher);
    assert.equal(result.passed, false);
    assert.equal(result.inventoriesComplete, true);
    assert.ok(writes.at(-1).endsWith(target));
    assert.equal(result.uploads.length, 0);
  }
});

test('malformed, auth, rate, conflict and network Git errors cannot count as denial', async () => {
  for (const status of [200, 401, 403, 404, 409, 429, 500]) {
    assert.throws(() => verifyGitDenial(response({}, status),
      { message: 'Repository rule violations found\nCannot create ref due to creations being restricted.' }, 'create'));
  }
  for (const message of ['Bad credentials', 'Reference already exists', 'Not a fast forward',
    'Repository rule violations found: rate limit', 'Validation failed']) {
    assert.throws(() => verifyGitDenial(response({}, 422), { message }, 'create'));
  }
  const f = fixture();
  const settings = await probeSettings(f);
  let writes = 0;
  const fetcher = async (url, options) => {
    if (options.method !== 'GET') { writes++; throw new Error('CREDENTIAL-SENTINEL'); }
    return f.fetcher(url, options);
  };
  const result = await runDenialProbes('generic', context, settings, fetcher);
  assert.equal(result.passed, false);
  assert.equal(writes, 1);
  assert.equal(result.attempts[0].outcome, 'unknown');
  assert.equal(result.inventoriesComplete, true);
  assert.doesNotMatch(JSON.stringify(result), /CREDENTIAL-SENTINEL/);
});

test('registry denial requires positive authentication and rejects auth, throttling, malformed and upload evidence', () => {
  for (const status of [200, 202, 401, 404, 429, 500]) {
    assert.throws(() => verifyUploadDenial(response({}, status),
      { errors: [{ code: 'DENIED', message: 'denied' }] }));
  }
  for (const headers of [{ location: '/upload' }, { 'docker-upload-uuid': 'id' }, { 'retry-after': '10' }]) {
    assert.throws(() => verifyUploadDenial(response({}, 403, headers),
      { errors: [{ code: 'DENIED', message: 'denied' }] }));
  }
  for (const message of ['invalid token', 'expired', 'rate limit', 'authentication required']) {
    assert.throws(() => verifyUploadDenial(response({}, 403),
      { errors: [{ code: 'DENIED', message }] }));
  }
  verifyUploadDenial(response({}, 401, { 'www-authenticate': 'Bearer error="insufficient_scope"' }),
    { errors: [{ code: 'UNAUTHORIZED', message: 'access denied' }] });
});

test('unexpected upload acceptance cancels only that session, verifies cancellation and remains FAIL', async () => {
  const f = fixture();
  const settings = await probeSettings(f);
  const url = `https://ghcr.io/v2/olyforge3d/${packageNames[0]}/blobs/uploads/session-42?_state=PRIVATE-STATE`;
  const methods = [];
  const fetcher = async (target, options) => {
    if (new URL(target).origin === 'https://ghcr.io') methods.push({ target, method: options.method });
    if (target.endsWith('/blobs/uploads/') && options.method === 'POST') {
      return response({}, 202, { location: url, 'docker-upload-uuid': 'session-42' });
    }
    if (target === url && options.method === 'DELETE') return response({}, 204);
    if (target === url && options.method === 'GET') return response({ errors: [{ code: 'BLOB_UPLOAD_UNKNOWN' }] }, 404);
    return f.fetcher(target, options);
  };
  const result = await runDenialProbes('generic', context, settings, fetcher);
  assert.equal(result.passed, false);
  assert.equal(result.uploads.length, 1);
  assert.equal(result.uploads[0].started, true);
  assert.equal(result.uploads[0].cancelled, true);
  assert.equal(methods.filter(call => call.method === 'POST').length, 1);
  assert.equal(methods.filter(call => call.method === 'DELETE').length, 1);
  assert.doesNotMatch(JSON.stringify(result), /PRIVATE-STATE|REGISTRY-TOKEN/);
});

test('cleanup cannot target another host, package, version or upload completion', () => {
  for (const location of [
    'https://evil.example/v2/olyforge3d/printfarmer-api/blobs/uploads/123',
    'https://ghcr.io.evil.example/v2/olyforge3d/printfarmer-api/blobs/uploads/123',
    'https://ghcr.io/v2/olyforge3d/printfarmer-frontend/blobs/uploads/123',
    'https://ghcr.io/v2/olyforge3d/printfarmer-api/manifests/latest',
    'https://ghcr.io/v2/olyforge3d/printfarmer-api/blobs/uploads/123?digest=sha256:bad',
    'https://user:password@ghcr.io/v2/olyforge3d/printfarmer-api/blobs/uploads/123',
  ]) assert.throws(() => cleanupUrl(location, 'printfarmer-api'));
});

const workflow = load(readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8'));
const admissionRun = `node --input-type=module <<'NODE'\n${rehearsalAdmissionSource}NODE\n`;
const admissionJobs = ['admission', 'positive', 'fixture', 'probes'];
const admittedEvidenceCondition = "always() && steps.admission.outcome == 'success'";

function assertAdmissionOrdering(document) {
  for (const key of ['env', 'defaults']) {
    assert.equal(Object.hasOwn(document, key), false, `No inherited ${key} before admission`);
  }
  for (const jobName of admissionJobs) {
    const job = document.jobs[jobName];
    for (const key of ['env', 'defaults', 'container', 'services', 'uses', 'secrets', 'continue-on-error']) {
      assert.equal(Object.hasOwn(job, key), false, `${jobName}: no pre-step ${key}`);
    }
    assert.equal(job['runs-on'], 'ubuntu-latest');
    assert.deepEqual(job.steps[0], {
      name: 'Reject untrusted dispatches and reruns',
      id: 'admission',
      shell: 'bash',
      env: { REHEARSAL_CHANNEL: '${{ inputs.channel }}' },
      run: admissionRun,
    }, `${jobName}: unconditional, credential-free first step must match audited source`);
    for (const [index, step] of job.steps.entries()) {
      if (index === 0) continue;
      assert.notEqual(step.id, 'admission', `${jobName}: no admission outcome replacement`);
      if (step.uses?.startsWith('actions/upload-artifact@')) {
        assert.equal(step.if, admittedEvidenceCondition, `${jobName}: upload cannot bypass admission`);
      } else {
        assert.equal(Object.hasOwn(step, 'if'), false, `${jobName}: later steps require implicit success()`);
      }
    }
    const sensitiveSteps = job.steps.filter(step => step.uses ||
      /GH_TOKEN|GITHUB_TOKEN|secrets\.|vars\.|github\.token|steps\..*token|environment|checkout|create-github-app-token/
        .test(JSON.stringify(step)));
    for (const step of sensitiveSteps) {
      assert.ok(job.steps.indexOf(step) > 0, `${jobName}: actions and credential exposure follow admission`);
    }
  }
}

test('all embedded admission scripts are byte-identical to the audited source, with no shell prefix or suffix', () => {
  for (const jobName of admissionJobs) {
    assert.equal(workflow.jobs[jobName].steps[0].run, admissionRun, jobName);
  }
});

test('parsed workflow guards every independently rerunnable job before any action or credential exposure', () => {
  assertAdmissionOrdering(workflow);
});

test('ordering guard rejects credential injection, action reordering, source drift and failure-path bypasses', () => {
  for (const jobName of ['positive', 'fixture', 'probes']) {
    for (const mutate of [
      job => job.steps.unshift({ uses: 'actions/checkout@untrusted' }),
      job => job.steps.unshift({ run: 'echo skipped', env: { GH_TOKEN: '${{ github.token }}' } }),
      job => { job.steps[0].env.PRIVATE_KEY = '${{ secrets.RELEASE_PUBLISHER_PRIVATE_KEY }}'; },
      job => { job.steps[0].env.APP_ID = '${{ vars.RELEASE_PUBLISHER_APP_ID }}'; },
      job => { job.steps[0].if = 'github.run_attempt == 1'; },
      job => { job.steps[0]['continue-on-error'] = true; },
      job => { job.steps[0].run = job.steps[0].run.replace("=== '1'", "=== '2'"); },
      job => { job.steps[1].if = 'always()'; },
      job => { job.steps.at(-1).if = 'always()'; },
      job => { job.steps.at(-1).if = "always() || steps.admission.outcome == 'success'"; },
      job => { job.env = { GH_TOKEN: '${{ github.token }}' }; },
      job => { job.container = { image: 'untrusted', credentials: { password: '${{ secrets.KEY }}' } }; },
      job => { job.services = { untrusted: { image: 'untrusted' } }; },
    ]) {
      const changed = structuredClone(workflow);
      mutate(changed.jobs[jobName]);
      assert.throws(() => assertAdmissionOrdering(changed), { name: 'AssertionError' });
    }
  }
  const changed = structuredClone(workflow);
  changed.env = { GH_TOKEN: '${{ github.token }}' };
  assert.throws(() => assertAdmissionOrdering(changed), { name: 'AssertionError' });
});

test('admission is unconditional and credential-free, and gates both protected jobs', () => {
  const admission = workflow.jobs.admission;
  assert.ok(admission);
  for (const key of ['if', 'needs', 'environment', 'uses', 'secrets', 'env']) {
    assert.equal(Object.hasOwn(admission, key), false, `Admission must not declare ${key}`);
  }
  assert.deepEqual(admission.permissions, {});
  assert.equal(admission['runs-on'], 'ubuntu-latest');
  assert.equal(admission.steps.length, 1);
  const [step] = admission.steps;
  assert.equal(Object.hasOwn(step, 'if'), false);
  assert.equal(Object.hasOwn(step, 'continue-on-error'), false);
  assert.equal(Object.hasOwn(admission, 'continue-on-error'), false);
  assert.equal(step.shell, 'bash');
  assert.deepEqual(step.env, { REHEARSAL_CHANNEL: '${{ inputs.channel }}' });
  assert.doesNotMatch(step.run, /secrets|github\.token|GH_TOKEN|fetch|https?:|checkout|\|\|\s*true/);
  assert.equal(workflow.jobs.positive.needs, 'admission');
  assert.equal(Object.hasOwn(workflow.jobs.positive, 'if'), false);
  assert.deepEqual(workflow.jobs.fixture.needs, ['admission', 'positive']);
  assert.equal(workflow.jobs.fixture.if, 'inputs.denial_probes');
  assert.deepEqual(workflow.jobs.probes.needs, ['admission', 'positive', 'fixture']);
  assert.equal(workflow.jobs.probes.if, 'inputs.denial_probes');
});

function runAdmission(overrides = {}, jobName = 'admission') {
  const run = workflow.jobs[jobName].steps[0].run;
  const match = /^node --input-type=module <<'NODE'\n([\s\S]+)\nNODE\n?$/.exec(run);
  assert.ok(match, 'Execute the exact admission script; no untested shell suffix');
  const result = spawnSync(process.execPath, ['--input-type=module', '--eval', match[1]], {
    env: { ...env, ...overrides }, encoding: 'utf8', timeout: 10_000,
  });
  assert.equal(result.error, undefined);
  assert.equal(result.signal, null);
  return result;
}

test('exact workflow admission succeeds for first-attempt insider and stable dispatches without credentials', () => {
  for (const jobName of admissionJobs) {
    assert.equal(runAdmission({}, jobName).status, 0);
    assert.equal(runAdmission({
      REHEARSAL_CHANNEL: 'stable', GITHUB_REF: 'refs/heads/main',
      GITHUB_WORKFLOW_REF: `${repository}/${rehearsalWorkflow}@refs/heads/main`,
    }, jobName).status, 0);
  }
});

test('exact workflow admission fails invalid refs and reruns instead of leaving an all-skipped green run', () => {
  for (const overrides of [
    { GITHUB_REPOSITORY: 'outsider/fork' },
    { GITHUB_EVENT_NAME: 'pull_request' }, { GITHUB_EVENT_NAME: 'workflow_call' },
    { GITHUB_REF: 'refs/heads/feature' }, { GITHUB_REF: 'refs/tags/v1.2.3' },
    { GITHUB_REF: 'refs/heads/main' }, { REHEARSAL_CHANNEL: 'stable' },
    { REHEARSAL_CHANNEL: 'unknown' }, { REHEARSAL_CHANNEL: '' },
    { GITHUB_RUN_ATTEMPT: '2' }, { GITHUB_RUN_ATTEMPT: '0' }, { GITHUB_RUN_ATTEMPT: '' },
    { GITHUB_WORKFLOW_REF: `${repository}/${rehearsalWorkflow}@refs/heads/feature` },
    { GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/consolidated-release.yml@refs/heads/development` },
    { GITHUB_WORKFLOW_REF: `outsider/fork/${rehearsalWorkflow}@refs/heads/development` },
    { GITHUB_WORKFLOW_SHA: head }, { GITHUB_WORKFLOW_SHA: '' },
    { GITHUB_SHA: 'bad', GITHUB_WORKFLOW_SHA: 'bad' },
    { GITHUB_SHA: '', GITHUB_WORKFLOW_SHA: '' }, { GITHUB_RUN_ID: '' },
  ]) {
    for (const jobName of admissionJobs) {
      const result = runAdmission(overrides, jobName);
      assert.equal(result.status, 1, `${jobName}: ${JSON.stringify(overrides)}`);
      assert.equal(result.stderr.trim(), 'Untrusted rehearsal dispatch; protected jobs blocked');
      assert.equal(result.stdout, '');
    }
  }
});

function laterStepRuns(step, priorSuccess, admissionOutcome) {
  if (!Object.hasOwn(step, 'if')) return priorSuccess;
  assert.equal(step.if, admittedEvidenceCondition, 'Only the structurally audited failure-path predicate is supported');
  return admissionOutcome === 'success';
}

test('single-job reruns reject before every later step even when standalone admission previously succeeded', () => {
  assertAdmissionOrdering(workflow);
  assert.equal(runAdmission().status, 0, 'Cached standalone admission succeeded on attempt 1');
  for (const jobName of ['positive', 'fixture', 'probes']) {
    for (const attempt of ['2', '3', '17']) {
      // Execute only the selected job, without re-executing its successful dependencies.
      const result = runAdmission({ GITHUB_RUN_ATTEMPT: attempt }, jobName);
      assert.equal(result.status, 1, `${jobName}: attempt ${attempt}`);
      const outcome = result.status === 0 ? 'success' : 'failure';
      const laterSteps = workflow.jobs[jobName].steps.slice(1)
        .filter(step => laterStepRuns(step, result.status === 0, outcome));
      assert.deepEqual(laterSteps, [], `${jobName}: no checkout, token, secret, probe or upload execution`);
    }
  }
});

test('admitted jobs still preserve evidence after a later failure, but failed or cancelled admission never uploads', () => {
  for (const jobName of ['positive', 'fixture', 'probes']) {
    const upload = workflow.jobs[jobName].steps.at(-1);
    assert.equal(laterStepRuns(upload, false, 'success'), true);
    for (const outcome of ['failure', 'cancelled', 'skipped', undefined]) {
      assert.equal(laterStepRuns(upload, false, outcome), false);
    }
  }
});

test('positive job and App token inputs forbid any write permission', () => {
  assert.deepEqual(workflow.permissions, { contents: 'read' });
  assert.deepEqual(workflow.jobs.positive.permissions, {
    contents: 'read', packages: 'read', actions: 'read', checks: 'read',
    statuses: 'read', 'pull-requests': 'read',
  });
  const appSteps = Object.values(workflow.jobs).flatMap(job => job.steps)
    .filter(step => step.uses?.startsWith('actions/create-github-app-token@'));
  assert.equal(appSteps.length, 1);
  const permissions = Object.fromEntries(Object.entries(appSteps[0].with)
    .filter(([name]) => name.startsWith('permission-')));
  assert.deepEqual(permissions, {
    'permission-contents': 'read', 'permission-checks': 'read', 'permission-statuses': 'read',
    'permission-administration': 'read', 'permission-actions': 'read',
  });
});

test('probe pass predicate explicitly requires one upload result for every production package', () => {
  const source = readFileSync('scripts/ci/release-rehearsal-probes.mjs', 'utf8');
  assert.match(source, /result\.uploads\.length === packageNames\.length && result\.uploads\.every/);
});

test('workflow separates App reads from generic writes without any publisher or signing entry point', () => {
  const workflow = readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8');
  const [positive, probes] = workflow.split('\n  probes:');
  for (const permission of ['contents', 'checks', 'statuses', 'administration', 'actions']) {
    assert.match(positive, new RegExp(`permission-${permission}: read`));
  }
  assert.match(positive, /repositories: PrintFarmer/);
  assert.match(positive, /owner: OlyForge3D/);
  assert.match(positive, /environment: release-\$\{\{ inputs.channel \}\}/);
  assert.match(probes, /environment: release-\$\{\{ inputs.channel \}\}/);
  assert.match(probes, /contents: write/);
  assert.match(probes, /packages: write/);
  assert.doesNotMatch(workflow, /RELEASE_REHEARSAL_APPROVED_SHA/);
  assert.match(workflow, /ref: \$\{\{ github.workflow_sha \}\}/);
  assert.equal((workflow.match(/persist-credentials: false/g) ?? []).length, 3);
  assert.doesNotMatch(probes, /secrets\.|create-github-app-token|REHEARSAL_APP_TOKEN/);
  assert.doesNotMatch(workflow, /id-token:|cosign|REGISTRY_TOKEN|REGISTRY_USER|schedule:|secrets: inherit|release-control\.mjs|docker-publish\.yml/);
  for (const source of ['release-rehearsal', 'release-rehearsal-probes', 'release-rehearsal-control']) {
    const text = readFileSync(`scripts/ci/${source}.mjs`, 'utf8');
    assert.doesNotMatch(text, /\b(?:reserve|ensureSourceTag|compareAndSet|advance|writeAuthorization|emitBuildIdentity)\s*\(/);
    assert.doesNotMatch(text, /from ['"].*release-control|execFile|spawnSync|sign-blob/);
  }
});

const testKeys = generateKeyPairSync('rsa', { modulusLength: 2048 });
const fixtureEnvironment = {
  ...env, GH_TOKEN: 'generic', RELEASE_PUBLISHER_APP_ID: fixtureAppId,
  REHEARSAL_DENIAL_PROBES: 'true',
  RELEASE_REHEARSAL_FIXTURE_PRIVATE_KEY: testKeys.privateKey,
};
const tagInventoryPath = 'git/matching-refs/tags?per_page=100&page=1';
const fixturePolicyPath = 'environments/release-rehearsal-fixture-insider';

async function provisionScenario() {
  const f = fixture();
  f.values.set(tagInventoryPath, f.values.get(tagInventoryPath).filter(tag => !tag.ref.endsWith('-update')));
  f.values.set(fixturePolicyPath, {
    ...structuredClone(f.values.get('environments/release-insider')),
    name: 'release-rehearsal-fixture-insider',
    deployment_branch_policy: { custom_branch_policies: true, protected_branches: false },
  });
  f.values.set(`${fixturePolicyPath}/deployment-branch-policies`,
    { branch_policies: [{ name: 'development', type: 'branch' }] });
  f.values.set(`git/commits/${sha}`, { sha, tree: { sha: tree } });
  const settings = { ...fixtureEnvironment,
    REHEARSAL_POSITIVE_DIGEST: digest(await snapshot(readOnlyClient('generic', f.fetcher), context)) };
  const calls = [];
  const grant = {
    token: 'APP_FIXTURE_TOKEN_SENTINEL', permissions: { contents: 'write', metadata: 'read' },
    repository_selection: 'selected',
    repositories: [{ id: 1044049720, full_name: repository }],
    expires_at: new Date(Date.now() + 3600_000).toISOString(),
  };
  const fetcher = async (url, options) => {
    calls.push({ url, ...options });
    assert.equal(options.redirect, 'error');
    assert.ok(options.signal instanceof AbortSignal);
    if (url === `https://api.github.com/repos/${repository}/installation`) {
      return response({ id: 17, app_id: Number(fixtureAppId), account: { login: 'OlyForge3D' },
        target_type: 'Organization' });
    }
    if (url === 'https://api.github.com/app/installations/17/access_tokens') return response(grant, 201);
    if (url === 'https://api.github.com/installation/token') return response(undefined, 204);
    if (options.method === 'POST' && url.endsWith('/git/refs')) {
      assert.equal(options.headers.Authorization, `Bearer ${grant.token}`);
      const { ref, sha } = JSON.parse(options.body);
      const created = { ref, object: { sha, type: 'commit' } };
      f.values.get(tagInventoryPath).push(created);
      f.values.set(`git/ref/tags/${context.marker}-update`, created);
      return response(created, 201);
    }
    assert.equal(options.headers.Authorization, 'Bearer generic', 'All inventory reads remain generic');
    return f.fetcher(url, options);
  };
  return { ...f, calls, grant, settings, fetcher, probeFetcher: f.fetcher };
}

test('fixture writer allows only one exact POST route/payload and never canonical refs or arbitrary targets', () => {
  const before = { ledger: { head }, tags: [] };
  const body = { ref: `refs/tags/${context.marker}-update`, sha: head };
  assert.equal(fixtureRequestUrl('git/refs', 'POST', body, context, before),
    `https://api.github.com/repos/${repository}/git/refs`);
  for (const [path, method, data] of [
    ['git/refs', 'PATCH', body], ['git/refs', 'DELETE', body], ['git/refs', 'PUT', body],
    ['git/refs', 'GET', body], ['git/refs/', 'POST', body],
    ['git/refs', 'POST', { ...body, force: false }], ['git/refs', 'POST', { ...body, sha }],
    ['git/refs', 'POST', { ...body, ref: 'refs/tags/v1.2.3' }],
    ['git/refs', 'POST', { ...body, ref: `refs/tags/${context.marker}` }],
    ['git/refs', 'POST', { ...body, ref: `refs/tags/${context.marker}-update/other` }],
    ...['git/commits', 'git/tags', 'git/blobs', 'git/trees', 'releases', 'issues', 'labels', 'statuses/' + sha,
      'git/refs/heads/release-ledger', 'actions/workflows/docker-publish.yml/dispatches',
      '../git/refs', 'https://evil.example/git/refs'].map(path => [path, 'POST', body]),
  ]) assert.throws(() => fixtureRequestUrl(path, method, data, context, before));
  assert.throws(() => intendedFixture({ ...context, run: '43' }, before));
  for (const ref of [body.ref, `refs/tags/${context.marker}`]) {
    assert.throws(() => intendedFixture(context, { ...before, tags: [{ ref, sha: head, type: 'commit' }] }));
  }
});

test('single-use fixture transport rejects arbitrary writes and concurrent or failed-write retries', async () => {
  const before = { ledger: { head }, tags: [] };
  const body = { ref: `refs/tags/${context.marker}-update`, sha: head };
  for (const outcome of ['success', 'denied', 'network']) {
    const calls = [];
    const write = fixtureWriter('app', context, before, async (url, options) => {
      calls.push({ url, ...options });
      if (outcome === 'network') throw new Error('Unknown outcome');
      return response({}, outcome === 'success' ? 201 : 422);
    });
    for (const [endpoint, method, payload] of [
      ['issues', 'POST', {}], ['labels', 'POST', {}],
      ['git/refs', 'DELETE', body], ['git/refs', 'POST', { ...body, sha }],
    ]) await assert.rejects(write(endpoint, method, payload));
    assert.equal(calls.length, 0);
    const first = write('git/refs', 'POST', body);
    const settled = Promise.allSettled([first]);
    await assert.rejects(write('git/refs', 'POST', body), /single-use/);
    const [result] = await settled;
    assert.equal(result.status, outcome === 'network' ? 'rejected' : 'fulfilled');
    await assert.rejects(write('git/refs', 'POST', body), /single-use/);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, `https://api.github.com/repos/${repository}/git/refs`);
    assert.equal(calls[0].method, 'POST');
    assert.deepEqual(JSON.parse(calls[0].body), body);
  }
});

test('fixture provisioning mints minimum repository-only App grant, creates once, revokes and accounts exactly', async () => {
  const f = await provisionScenario();
  const positive = await positiveRehearsal(readOnlyClient('app', f.probeFetcher),
    readOnlyClient('generic', f.probeFetcher), context, { appId: '123' });
  assert.equal(f.settings.REHEARSAL_POSITIVE_DIGEST, positive.inventoryDigest);
  const result = await provisionFixture(f.settings, f.fetcher);
  assert.equal(result.passed, true, JSON.stringify(result));
  assert.equal(result.workflowTriggersVerified, true);
  assert.equal(result.attempts.length, 1);
  assert.equal(result.attempts[0].outcome, 'created-and-read-verified');
  assert.equal(result.fixtures[0].sha, head, 'Target is ledger head, not qualified source');
  assert.equal(result.inventoryDigest, digest(result.after));
  assert.equal(result.after.tags.length, result.before.tags.length + 1);
  const writes = f.calls.filter(call => call.method !== 'GET');
  assert.deepEqual(writes.map(call => [new URL(call.url).pathname, call.method]), [
    ['/app/installations/17/access_tokens', 'POST'], [`/repos/${repository}/git/refs`, 'POST'],
    ['/installation/token', 'DELETE'],
  ]);
  assert.deepEqual(JSON.parse(writes[0].body), { repository_ids: [1044049720], permissions: { contents: 'write' } });
  const jwt = writes[0].headers.Authorization.slice('Bearer '.length);
  const [header, claims, signature] = jwt.split('.');
  assert.equal(JSON.parse(Buffer.from(claims, 'base64url')).iss, fixtureAppId);
  assert.equal(verify('RSA-SHA256', Buffer.from(`${header}.${claims}`),
    testKeys.publicKey, Buffer.from(signature, 'base64url')), true);
  assert.doesNotMatch(JSON.stringify(result), /SENTINEL|PRIVATE KEY|permissions|Authorization/);
  const probes = await runDenialProbes('generic', context, {
    requested: true, actor: 'jpapiez', positiveDigest: result.inventoryDigest,
  }, f.probeFetcher);
  assert.equal(probes.passed, true, JSON.stringify(probes));
  assert.deepEqual(probes.before, result.after);
});

test('fixture inventory exclusion is exact and cannot hide any additional write or missing fixture', () => {
  const before = { ledger: { head }, tags: [{ ref: 'refs/tags/v0.2.2', sha, type: 'commit' }], releases: [] };
  const intended = intendedFixture(context, before);
  const after = { ...before, tags: [...before.tags, intended] };
  verifyFixtureInventory(before, after, intended);
  for (const change of [
    value => value.tags.pop(),
    value => { value.tags[1].sha = sha; }, value => { value.tags[1].type = 'tag'; },
    value => value.tags.push(intended), value => value.tags.push({ ...intended, ref: intended.ref + '-other' }),
    value => { value.tags[0].sha = head; }, value => { value.ledger.head = child; },
    value => value.releases.push({ id: 1 }),
  ]) {
    const changed = structuredClone(after);
    change(changed);
    assert.throws(() => verifyFixtureInventory(before, changed, intended));
  }
});

test('fixture preflight refuses reruns, context drift, missing approval, conflicts and executable target before mint', async () => {
  for (const fault of ['rerun', 'workflow', 'channel', 'source', 'request', 'app', 'digest', 'existing',
    'policy', 'branch', 'qualify', 'target', 'generic-app', 'default-branch', 'repository']) {
    const f = await provisionScenario();
    if (fault === 'rerun') f.settings.GITHUB_RUN_ATTEMPT = '2';
    if (fault === 'workflow') f.settings.GITHUB_WORKFLOW_SHA = head;
    if (fault === 'channel') f.settings.REHEARSAL_CHANNEL = 'stable';
    if (fault === 'source') f.values.get('actions/runs/42').head_sha = head;
    if (fault === 'request') f.settings.REHEARSAL_DENIAL_PROBES = 'false';
    if (fault === 'app') f.settings.RELEASE_PUBLISHER_APP_ID = '123';
    if (fault === 'digest') f.settings.REHEARSAL_POSITIVE_DIGEST = '0'.repeat(64);
    if (fault === 'existing') {
      f.values.get(tagInventoryPath).push({ ref: `refs/tags/${context.marker}-update`, object: { type: 'commit', sha: head } });
      f.settings.REHEARSAL_POSITIVE_DIGEST = digest(await snapshot(readOnlyClient('generic', f.fetcher), context));
    }
    if (fault === 'policy') f.values.get(fixturePolicyPath).can_admins_bypass = true;
    if (fault === 'branch') f.values.get(`${fixturePolicyPath}/deployment-branch-policies`).branch_policies.push({ name: '*' });
    if (fault === 'qualify') f.values.set(`commits/${sha}/statuses?per_page=100`, []);
    if (fault === 'target') f.values.get(`git/trees/${tree}`).tree.push({ path: '.github', type: 'tree', sha });
    if (fault === 'generic-app') f.settings.REHEARSAL_APP_TOKEN = 'unapproved';
    if (fault === 'default-branch') f.values.get('').default_branch = 'main';
    if (fault === 'repository') f.values.get('').full_name = 'outsider/fork';
    let result;
    try { result = await provisionFixture(f.settings, f.fetcher); } catch { result = { passed: false }; }
    assert.equal(result.passed, false, fault);
    assert.equal(f.calls.some(call => call.url.includes('/installation')), false, fault);
    assert.ok(f.calls.every(call => call.method === 'GET'), fault);
  }
});

test('fixture rejects unrelated publisher credentials and owner records before any API request', async () => {
  for (const key of ['REHEARSAL_APP_TOKEN', 'RELEASE_PUBLISHER_TOKEN', 'RELEASE_REGISTRY_TOKEN',
    'RELEASE_PUBLISHER_PRIVATE_KEY', 'RELEASE_OWNER_APPROVED_REVIEWERS']) {
    const f = await provisionScenario();
    await assert.rejects(provisionFixture({ ...f.settings, [key]: 'unapproved' }, f.fetcher));
    assert.deepEqual(f.calls, [], key);
  }
});

test('default-branch changes after issuance revoke before fixture creation', async () => {
  const f = await provisionScenario();
  const fetcher = async (url, options) => {
    const result = await f.fetcher(url, options);
    if (url.endsWith('/access_tokens')) f.values.get('').default_branch = 'main';
    return result;
  };
  assert.equal((await provisionFixture(f.settings, fetcher)).passed, false);
  assert.equal(f.calls.filter(call => call.method === 'DELETE').length, 1);
  assert.equal(f.calls.some(call => call.url.endsWith('/git/refs')), false);
});

test('fixture environment rejects every alternate approver and branch/bypass misconfiguration', async () => {
  for (const mutate of [
    policy => { policy.name = 'release-insider'; },
    policy => { policy.can_admins_bypass = undefined; },
    policy => { policy.deployment_branch_policy.protected_branches = true; },
    policy => { policy.protection_rules = []; },
    policy => { policy.protection_rules[0].reviewers[0].reviewer.login = 'outsider'; },
    policy => { policy.protection_rules[0].reviewers.push({ type: 'Team', reviewer: { id: 5 } }); },
    policy => { policy.protection_rules[0].prevent_self_review = true; },
  ]) {
    const f = await provisionScenario();
    mutate(f.values.get(fixturePolicyPath));
    await assert.rejects(verifyFixtureEnvironment(readOnlyClient('generic', f.fetcher), context));
  }
});

test('malformed, excessive, expired or wrong-repository App grants revoke without any fixture write', async () => {
  for (const mutate of [
    grant => { grant.permissions.packages = 'write'; }, grant => { grant.permissions.contents = 'read'; },
    grant => { grant.permissions.actions = 'write'; }, grant => { grant.permissions.workflows = 'write'; },
    grant => { grant.repository_selection = 'all'; }, grant => { grant.repositories = []; },
    grant => { grant.repositories[0].id++; }, grant => { grant.repositories[0].full_name = 'outsider/fork'; },
    grant => { grant.expires_at = 'invalid'; }, grant => { grant.expires_at = '2000-01-01T00:00:00Z'; },
    grant => { grant.repositories.push({ id: 2, full_name: 'OlyForge3D/Other' }); },
  ]) {
    const f = await provisionScenario();
    mutate(f.grant);
    await assert.rejects(withFixtureToken(testKeys.privateKey, () => assert.fail('must not expose grant'), f.fetcher));
    assert.equal(f.calls.filter(call => call.method === 'DELETE').length, 1);
    assert.equal(f.calls.some(call => call.url.endsWith('/git/refs')), false);
  }
});

test('every malformed issued string token is revoked exactly once without fixture writes', async () => {
  for (const token of ['', 'invalid.token', 'invalid token', 'invalid\ntoken', 'invalid-token', 'token\n']) {
    const f = await provisionScenario();
    f.grant.token = token;
    const result = await provisionFixture(f.settings, f.fetcher);
    assert.equal(result.passed, false);
    assert.deepEqual(result.attempts, []);
    const writes = f.calls.filter(call => call.method !== 'GET');
    assert.deepEqual(writes.map(call => [new URL(call.url).pathname, call.method]), [
      ['/app/installations/17/access_tokens', 'POST'], ['/installation/token', 'DELETE'],
    ]);
    assert.equal(writes[1].headers.Authorization, `Bearer ${token}`);
    assert.deepEqual(result.before, result.after);
  }
});

test('fixture failures never retry or delete refs; unknown success, drift and failed revocation remain failed', async () => {
  for (const fault of ['conflict', 'denial', 'auth', 'rate', 'redirect', 'network', 'malformed', 'unexpected-200', 'wrong-ref',
    'wrong-sha', 'wrong-type', 'missing', 'drift', 'revoke']) {
    const f = await provisionScenario();
    const fetcher = async (url, options) => {
      if (url.endsWith('/installation/token') && fault === 'revoke') return response({ message: 'SENTINEL' }, 500);
      if (url.endsWith('/git/refs') && options.method === 'POST') {
        if (fault === 'conflict') return response({ message: 'Reference already exists' }, 422);
        if (fault === 'denial') return denied('create');
        if (fault === 'auth') return response({ message: 'SENTINEL' }, 401);
        if (fault === 'rate') return response({ message: 'SENTINEL' }, 429);
        if (fault === 'redirect') return response({}, 307, { location: 'https://evil.example/SENTINEL' });
        if (fault === 'network') throw new Error('SENTINEL-network');
        if (fault === 'missing') return response({ ref: `refs/tags/${context.marker}-update`, object: { sha: head, type: 'commit' } }, 201);
        const created = await f.fetcher(url, options);
        if (fault === 'malformed') return new Response('SENTINEL', { status: 201 });
        if (fault === 'unexpected-200') return response(await created.json(), 200);
        if (fault === 'drift') f.values.get('releases?per_page=100&page=1')[0].body = 'changed';
        if (fault.startsWith('wrong-')) {
          const data = await created.json();
          if (fault === 'wrong-ref') data.ref += '-other';
          if (fault === 'wrong-sha') data.object.sha = sha;
          if (fault === 'wrong-type') data.object.type = 'tag';
          return response(data, 201);
        }
        return created;
      }
      return f.fetcher(url, options);
    };
    const result = await provisionFixture(f.settings, fetcher);
    assert.equal(result.passed, false, fault);
    assert.equal(result.attempts.length, 1, fault);
    assert.equal(Object.hasOwn(result, 'inventoryDigest'), false);
    assert.doesNotMatch(JSON.stringify(result), /SENTINEL|evil\.example/);
    assert.ok(f.calls.filter(call => call.method === 'DELETE').every(call => call.url.endsWith('/installation/token')));
  }
});

test('fixture workflow uses separate owner environment, no generic writes, no token outputs, and chains exact inventory', () => {
  const job = workflow.jobs.fixture;
  assert.equal(job.environment, 'release-rehearsal-fixture-${{ inputs.channel }}');
  assert.deepEqual(job.permissions, workflow.jobs.positive.permissions);
  assert.deepEqual(job.outputs, { inventory_digest: '${{ steps.verify.outputs.inventory_digest }}' });
  const command = job.steps.find(step => step.id === 'verify');
  assert.equal(command.run, 'node scripts/ci/release-rehearsal-control.mjs fixture');
  assert.equal(command.env.RELEASE_REHEARSAL_FIXTURE_PRIVATE_KEY, '${{ secrets.RELEASE_REHEARSAL_FIXTURE_PRIVATE_KEY }}');
  assert.equal(command.env.REHEARSAL_POSITIVE_DIGEST, '${{ needs.positive.outputs.inventory_digest }}');
  assert.equal(job.steps.filter(step => JSON.stringify(step).includes('secrets.')).length, 1);
  assert.doesNotMatch(JSON.stringify(job), /create-github-app-token|permission-.*write|outputs\.token|id-token|packages.*write|RELEASE_PUBLISHER_PRIVATE_KEY|RELEASE_OWNER_APPROVED_REVIEWERS/);
  const probe = workflow.jobs.probes.steps.find(step => step.run?.endsWith('control.mjs probes'));
  assert.equal(probe.env.REHEARSAL_POSITIVE_DIGEST, '${{ needs.fixture.outputs.inventory_digest }}');
  assert.equal(Object.hasOwn(probe.env, 'RELEASE_REHEARSAL_APPROVED_SHA'), false);
  for (const file of ['control', 'probes']) {
    assert.doesNotMatch(readFileSync(`scripts/ci/release-rehearsal-${file}.mjs`, 'utf8'),
      /approvedSha|RELEASE_REHEARSAL_APPROVED_SHA|Owner must approve this exact rehearsal SHA/);
  }
  const source = readFileSync('scripts/ci/release-rehearsal-fixture.mjs', 'utf8');
  assert.doesNotMatch(source, /console\.|writeFile|appendFile|GITHUB_OUTPUT|GITHUB_ENV|execFile|spawn|process\.stdout/);
  assert.doesNotMatch(source, /release-control|ensureSourceTag|reserve\(|compareAndSet|sign-blob|ghcr\.io/);
});

function startsForFixtureTag(document, tag) {
  const on = document.on;
  if (typeof on === 'string') return ['push', 'create'].includes(on);
  if (Array.isArray(on)) return on.some(event => ['push', 'create'].includes(event));
  if (Object.hasOwn(on ?? {}, 'create')) return true;
  if (!Object.hasOwn(on ?? {}, 'push')) return false;
  const push = on.push;
  if (!push || typeof push !== 'object') return true;
  const patterns = {
    '*': /^[^/]*$/,
    'v*': /^v[^/]*$/,
    'v[0-9]+.[0-9]+.[0-9]+': /^v[0-9]+\.[0-9]+\.[0-9]+$/,
    'ios/v*-alpha*': /^ios\/v[^/]*-alpha[^/]*$/,
    'ios/v*-beta*': /^ios\/v[^/]*-beta[^/]*$/,
    'ios/v*-rc*': /^ios\/v[^/]*-rc[^/]*$/,
    'v-rehearsal-2668-*': /^v-rehearsal-2668-[^/]*$/,
  };
  const matches = pattern => {
    assert.ok(Object.hasOwn(patterns, pattern), `Audit new GitHub tag pattern: ${pattern}`);
    return patterns[pattern].test(tag);
  };
  if (push.tags) return push.tags.some(matches);
  if (push['tags-ignore']) return !push['tags-ignore'].some(matches);
  return !Object.hasOwn(push, 'branches') && !Object.hasOwn(push, 'branches-ignore');
}

test('all workflows including every push-capable writer match zero fixture push/create events', () => {
  const writers = [];
  const tags = [context.marker, `${context.marker}-update`, 'v-rehearsal-2668-999999999-1-update'];
  for (const file of readdirSync('.github/workflows').filter(file => /\.ya?ml$/.test(file))) {
    const source = readFileSync(`.github/workflows/${file}`, 'utf8');
    const document = load(source);
    verifyFixtureWorkflow(source);
    for (const tag of tags) assert.equal(startsForFixtureTag(document, tag), false, `${file}: ${tag}`);
    if (Object.hasOwn(document.on, 'push')) {
      const grants = [document.permissions, ...Object.values(document.jobs).map(job => job.permissions)];
      if (grants.some(grant => grant === 'write-all' ||
        Object.values(grant ?? {}).includes('write'))) writers.push(file);
    }
  }
  assert.deepEqual(writers, [
    'codeql.yml', 'devcontainer-multiarch.yml', 'orcaslicer-base-image.yml',
    'slicer-worker-security.yml', 'sync-squad-labels.yml', 'testflight-beta.yml',
  ], 'Audit every new write-capable push consumer, not just publishers');
});

test('label exclusion prevents secondary issue/label writes even though tag pushes ignore paths', () => {
  const source = readFileSync('.github/workflows/sync-squad-labels.yml', 'utf8');
  const document = load(source);
  assert.equal(document.permissions.issues, 'write');
  assert.deepEqual(document.on.push['tags-ignore'], ['v-rehearsal-2668-*']);
  assert.ok(document.on.push.paths.length > 0);
  assert.equal(startsForFixtureTag(document, `${context.marker}-update`), false);
  verifyFixtureWorkflow(source);
  const branchOnly = source.replace(/    tags-ignore:\n      - 'v-rehearsal-2668-\*'\n/, '');
  assert.notEqual(source, branchOnly);
  assert.equal(startsForFixtureTag(load(branchOnly), 'v1.2.3'), false,
    'Branch-only filters disable all tag pushes');
  verifyFixtureWorkflow(branchOnly);
  const unfiltered = branchOnly.replace(/    branches: \['\*\*'\]\n/, '');
  assert.notEqual(source, unfiltered);
  assert.equal(startsForFixtureTag(load(unfiltered), `${context.marker}-update`), true);
  assert.throws(() => verifyFixtureWorkflow(unfiltered), /Unfiltered/);
  assert.equal(startsForFixtureTag(document, 'v1.2.3'), true, 'Existing non-fixture tag behavior is preserved');
});

test('label sync retains all branch pushes with matching paths alongside fixture tag exclusion', () => {
  const source = readFileSync('.github/workflows/sync-squad-labels.yml', 'utf8');
  const document = load(source);
  const paths = ['.squad/team.md', '.github/workflows/sync-squad-labels.yml', 'scripts/ci/squad-routing.cjs'];
  assert.deepEqual(document.on.push.branches, ['**']);
  assert.deepEqual(document.on.push.paths, paths);
  assert.deepEqual(document.on.push['tags-ignore'], ['v-rehearsal-2668-*']);

  // GitHub disables branch pushes when only tag filters are specified; paths do not re-enable them.
  function startsForBranch(workflow, branch, changedPaths) {
    const push = workflow.on.push;
    if (!push.branches && (push.tags || push['tags-ignore'])) return false;
    return (!push.branches || push.branches.some(pattern => pattern === '**' || pattern === branch)) &&
      changedPaths.some(path => push.paths.includes(path));
  }

  const tagOnly = structuredClone(document);
  delete tagOnly.on.push.branches;
  for (const branch of ['main', 'development', 'feature/example', 'squad/parker-2668-fixture-provisioner']) {
    for (const path of paths) {
      assert.equal(startsForBranch(document, branch, [path]), true, `${branch}: ${path}`);
      assert.equal(startsForBranch(tagOnly, branch, [path]), false,
        'Removing the explicit branch filter must reproduce the blocker');
    }
    assert.equal(startsForBranch(document, branch, ['README.md']), false);
    assert.equal(startsForBranch(document, branch, ['README.md', paths[0]]), true);
  }
  for (const tag of [context.marker, `${context.marker}-update`]) {
    assert.equal(startsForFixtureTag(document, tag), false);
  }
  for (const tag of ['v1.2.3', 'v1.2.3-insider.42', 'ios/v1.2.3-beta.1']) {
    assert.equal(startsForFixtureTag(document, tag), true, 'Tag pushes ignore path filters');
  }
  verifyFixtureWorkflow(source);
});

test('workflow target rejects ambiguous YAML, arbitrary push/create/indirect events and ignores path filters for tags', () => {
  for (const source of [
    'on: push', 'on: [push]', 'on: {push: {}}', 'on: {create: {}}',
    'on: {push: {paths: [docs/**]}}',
    'on: {push: {tags: [v*]}}', 'on: {push: {tags-ignore: [ios/**], branches: [main]}}',
    'on: {push: {tags: [v-rehearsal-2668-*]}}',
    'on: {workflow_run: {workflows: [Sync Squad Labels]}}',
    'on: {workflow_run: {workflows: ["*"]}}',
    'on: {push: {branches: [main]}, push: {tags: ["*"]}}',
    'on: !!invalid {}',
    'on: {push: {tags: ["v[0-9]+.[0-9]+.[0-9]+"], tags-ignore: [v-rehearsal-2668-*]}}',
    'on: {push: {tags-ignore: [v-rehearsal-2668-42-*]}}',
    'on: {push: {tags-ignore: [v-rehearsal-2668-*, "!v-rehearsal-2668-42-1-update"]}}',
  ]) assert.throws(() => verifyFixtureWorkflow(source));
  verifyFixtureWorkflow('on: {push: {tags-ignore: [v-rehearsal-2668-*]}}');
});

test('unsafe issue/label consumers on either ledger or default tree block before mint or fixture writes', async () => {
  for (const target of ['ledger', 'default']) {
    for (const event of ['push: {paths: [docs/**]}', 'create: {}']) {
      const f = await provisionScenario();
      const rootSha = target === 'ledger' ? tree : '4'.repeat(40);
      const directorySha = '2'.repeat(40);
      const workflowSha = '3'.repeat(40);
      const source = Buffer.from(`on: {${event}}\npermissions: {issues: write}\njobs: {}\n`);
      const blobSha = createHash('sha1').update(`blob ${source.length}\0`).update(source).digest('hex');
      if (target === 'default') f.values.get(`git/commits/${sha}`).tree.sha = rootSha;
      const entries = target === 'ledger' ? f.values.get(`git/trees/${tree}`).tree : [];
      f.values.set(`git/trees/${rootSha}`, { sha: rootSha, truncated: false,
        tree: [...entries, { path: '.github', type: 'tree', sha: directorySha }] });
      f.values.set(`git/trees/${directorySha}`, { sha: directorySha, truncated: false,
        tree: [{ path: 'workflows', type: 'tree', sha: workflowSha }] });
      f.values.set(`git/trees/${workflowSha}`, { sha: workflowSha, truncated: false,
        tree: [{ path: 'labels.yml', type: 'blob', mode: '100644', sha: blobSha }] });
      f.values.set(`git/blobs/${blobSha}`, { sha: blobSha, size: source.length,
        encoding: 'base64', content: source.toString('base64') });
      const result = await provisionFixture(f.settings, f.fetcher);
      assert.equal(result.passed, false, `${target}: ${event}`);
      assert.deepEqual(result.attempts, []);
      assert.ok(f.calls.some(call => call.url.endsWith(`/git/blobs/${blobSha}`)), 'Unsafe blob was audited');
      assert.ok(f.calls.every(call => call.method === 'GET'));
      assert.equal(f.calls.some(call => call.url.includes('/installation')), false);
    }
  }
});

test('workflow tree walk verifies target blobs and fails truncated, missing or rewritten workflow data', async () => {
  const source = readFileSync('.github/workflows/sync-squad-labels.yml');
  const blobSha = createHash('sha1').update(`blob ${source.length}\0`).update(source).digest('hex');
  function data() {
    return new Map([
      [`git/trees/${tree}`, { sha: tree, truncated: false, tree: [{ path: '.github', type: 'tree', sha }] }],
      [`git/trees/${sha}`, { sha, truncated: false, tree: [{ path: 'workflows', type: 'tree', sha: head }] }],
      [`git/trees/${head}`, { sha: head, truncated: false,
        tree: [{ path: 'workflow.yml', type: 'blob', mode: '100644', sha: blobSha }] }],
      [`git/blobs/${blobSha}`, { sha: blobSha, size: source.length, encoding: 'base64', content: source.toString('base64') }],
    ]);
  }
  const values = data();
  await verifyFixtureTarget(async path => values.get(path), tree);
  for (const mutate of [
    values => { values.get(`git/trees/${tree}`).truncated = true; },
    values => { values.get(`git/trees/${sha}`).sha = head; },
    values => { values.get(`git/trees/${head}`).tree[0].mode = '120000'; },
    values => { values.get(`git/blobs/${blobSha}`).content = Buffer.from('on: push').toString('base64'); },
    values => { values.get(`git/blobs/${blobSha}`).size++; },
    values => { values.delete(`git/blobs/${blobSha}`); },
  ]) {
    const changed = data();
    mutate(changed);
    await assert.rejects(verifyFixtureTarget(async path => changed.get(path), tree));
  }
});

test('App installation and issuance errors never reach resource creation or expose provider content', async () => {
  for (const fault of ['wrong-app', 'wrong-owner', 'suspended', 'malformed', 'redirect', 'denied', 'mint-unknown']) {
    const f = await provisionScenario();
    const fetcher = async (url, options) => {
      if (url.endsWith('/installation')) {
        if (fault === 'malformed') return new Response('SENTINEL', { status: 200 });
        if (fault === 'redirect') return response({}, 302, { location: 'https://evil.example/SENTINEL' });
        if (fault === 'denied') return response({ message: 'SENTINEL' }, 403);
        const data = { id: 17, app_id: Number(fixtureAppId), target_type: 'Organization', account: { login: 'OlyForge3D' } };
        if (fault === 'wrong-app') data.app_id++;
        if (fault === 'wrong-owner') data.account.login = 'outsider';
        if (fault === 'suspended') data.suspended_at = '2026-01-01T00:00:00Z';
        return response(data);
      }
      if (url.endsWith('/access_tokens') && fault === 'mint-unknown') throw new Error('SENTINEL-network');
      return f.fetcher(url, options);
    };
    await assert.rejects(withFixtureToken(testKeys.privateKey, () => assert.fail('no resource writes'), fetcher),
      error => !error.message.includes('SENTINEL'));
    assert.equal(f.calls.some(call => call.url.endsWith('/git/refs')), false);
  }
});
