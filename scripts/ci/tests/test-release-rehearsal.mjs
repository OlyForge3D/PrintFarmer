import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { load } from 'js-yaml';
import { canonicalAuthorizationFixture } from './fixtures/canonical-qualification.mjs';
import { canonicalValidationChecks } from '../canonical-qualification.mjs';
import { parseTag, releaseRequiredChecks, releaseReviewStatus, repository } from '../release-policy.mjs';
import { rehearsalAdmissionSource } from '../release-rehearsal-admission.mjs';
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
  values.set('', { full_name: repository, default_branch: 'development', permissions: { push: true } });
  const checks = [...releaseRequiredChecks, ...canonicalValidationChecks].filter(name => name !== releaseReviewStatus);
  values.set(`commits/${sha}/check-runs?per_page=100`, {
    total_count: checks.length, check_runs: checks.map((name, index) => ({
      id: index + 1, name, head_sha: sha, status: 'completed', conclusion: 'success',
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
      if (parsed.pathname === '/token') return response({ token: 'REGISTRY-TOKEN-SENTINEL' });
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
  return { approvedSha: sha, requested: true, actor: 'jpapiez',
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

test('missing owner approval, mismatched digest, absent fixture and insufficient generic permissions never write', async () => {
  for (const fault of ['owner', 'digest', 'fixture', 'permissions', 'active']) {
    const f = fixture();
    const settings = await probeSettings(f);
    if (fault === 'owner') settings.approvedSha = head;
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
      assert.equal(fault, 'owner', error.message);
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
    if (target.startsWith('https://ghcr.io')) methods.push({ target, method: options.method });
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
    'https://ghcr.io/v2/olyforge3d/printfarmer-frontend/blobs/uploads/123',
    'https://ghcr.io/v2/olyforge3d/printfarmer-api/manifests/latest',
    'https://ghcr.io/v2/olyforge3d/printfarmer-api/blobs/uploads/123?digest=sha256:bad',
    'https://user:password@ghcr.io/v2/olyforge3d/printfarmer-api/blobs/uploads/123',
  ]) assert.throws(() => cleanupUrl(location, 'printfarmer-api'));
});

const workflow = load(readFileSync('.github/workflows/release-protection-rehearsal.yml', 'utf8'));
const admissionRun = `node --input-type=module <<'NODE'\n${rehearsalAdmissionSource}NODE\n`;
const admissionJobs = ['admission', 'positive', 'probes'];
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
  for (const jobName of ['positive', 'probes']) {
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
  assert.deepEqual(workflow.jobs.probes.needs, ['admission', 'positive']);
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
  for (const jobName of ['positive', 'probes']) {
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
  for (const jobName of ['positive', 'probes']) {
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
  assert.match(probes, /RELEASE_REHEARSAL_APPROVED_SHA/);
  assert.match(workflow, /ref: \$\{\{ github.workflow_sha \}\}/);
  assert.equal((workflow.match(/persist-credentials: false/g) ?? []).length, 2);
  assert.doesNotMatch(probes, /secrets\.|create-github-app-token|REHEARSAL_APP_TOKEN/);
  assert.doesNotMatch(workflow, /id-token:|cosign|REGISTRY_TOKEN|REGISTRY_USER|workflow_call|schedule:|secrets: inherit|release-control\.mjs|docker-publish\.yml/);
  for (const source of ['release-rehearsal', 'release-rehearsal-probes', 'release-rehearsal-control']) {
    const text = readFileSync(`scripts/ci/${source}.mjs`, 'utf8');
    assert.doesNotMatch(text, /\b(?:reserve|ensureSourceTag|compareAndSet|advance|writeAuthorization|emitBuildIdentity)\s*\(/);
    assert.doesNotMatch(text, /from ['"].*release-control|execFile|spawnSync|sign-blob/);
  }
});
