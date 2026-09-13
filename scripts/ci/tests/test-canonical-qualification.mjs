import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { load } from 'js-yaml';
import { repository, releaseBuildChecks, releaseReviewStatus } from '../release-policy.mjs';
import { confirmationBody, qualificationTitle, parseQualificationTitle, qualificationRequestUrl,
  qualificationWorkflow, evidenceWorkflow, verifyQualification, recordQualification,
  verifyCanonicalReleaseEvidence, qualificationDescription } from '../canonical-qualification.mjs';

const stableSha = 'a'.repeat(40);
const defaultSha = 'b'.repeat(40);
const now = Date.parse('2026-09-13T08:00:00Z');
const url = id => `https://github.com/${repository}/actions/runs/${id}`;
const read = path => readFileSync(fileURLToPath(new URL(`../../../${path}`, import.meta.url)), 'utf8');

function fixture(channel = 'stable', mode = 'single-maintainer') {
  const branch = channel === 'stable' ? 'main' : 'development';
  const sha = channel === 'stable' ? stableSha : defaultSha;
  const definitions = {
    'ci.yml': { id: 1, path: '.github/workflows/ci.yml', state: 'active' },
    'qualify-canonical-release.yml': { id: 2, path: qualificationWorkflow, state: 'active' },
    'record-canonical-qualification.yml': { id: 3, path: evidenceWorkflow, state: 'active' },
  };
  function run(id, file, runBranch, runSha, event = 'workflow_dispatch') {
    return { id, path: definitions[file].path, workflow_id: definitions[file].id, event,
      repository: { full_name: repository }, head_repository: { full_name: repository },
      head_branch: runBranch, head_sha: runSha, run_attempt: 1, html_url: url(id),
      status: 'completed', conclusion: 'success', actor: { login: 'jpapiez' },
      triggering_actor: { login: 'jpapiez' },
      created_at: '2026-09-13T05:00:00Z', run_started_at: '2026-09-13T05:00:00Z',
      updated_at: '2026-09-13T06:00:00Z' };
  }
  const ci = run(10, 'ci.yml', branch, sha);
  ci.check_suite_id = 100;
  const qualifier = run(20, 'qualify-canonical-release.yml', 'development', defaultSha);
  qualifier.created_at = qualifier.run_started_at = '2026-09-13T07:00:00Z';
  qualifier.updated_at = '2026-09-13T07:05:00Z';
  qualifier.display_title = qualificationTitle(channel, '10', '50', mode === 'single-maintainer' ? '0' : '60', mode);
  const writer = run(30, 'record-canonical-qualification.yml', 'development', defaultSha, 'workflow_run');
  writer.created_at = writer.run_started_at = '2026-09-13T07:06:00Z';
  writer.updated_at = '2026-09-13T07:10:00Z';
  writer.display_title = 'Canonical evidence for 20';
  const comment = { id: 50, commit_id: sha, user: { login: mode === 'single-maintainer' ? 'jpapiez' : 'reviewer', type: 'User' },
    body: confirmationBody(sha, '10', mode), created_at: '2026-09-13T06:30:00Z', updated_at: '2026-09-13T06:30:00Z' };
  function jobs(run, names) {
    return names.map((name, i) => ({ id: run.id * 100 + i, name, run_id: run.id, run_attempt: 1,
      head_sha: run.head_sha, status: 'completed', conclusion: 'success',
      check_run_url: `https://api.github.com/repos/${repository}/check-runs/${run.id * 100 + i}` }));
  }
  const data = {
    repo: { full_name: repository, default_branch: 'development' },
    heads: { main: stableSha, development: defaultSha }, definitions,
    runs: { 10: ci, 20: qualifier, 30: writer },
    ciRuns: [ci], qualifications: [qualifier], comments: [comment],
    jobs: {
      10: jobs(ci, [...releaseBuildChecks, 'Select affected tests', 'CI summary',
        'Dependency license & provenance validation', '.NET provider tests (DbHeavy)', '.NET test (example)']),
      20: jobs(qualifier, ['Verify canonical qualification']),
      30: jobs(writer, ['Record canonical evidence']),
    },
    permissions: { jpapiez: 'admin', reviewer: 'write' },
    owners: '* @reviewer\n',
    pr: { number: 60, head: { sha, repo: { full_name: repository } },
      base: { ref: branch, repo: { full_name: repository } }, user: { login: 'jpapiez' }, draft: false },
    reviews: [{ id: 70, user: { login: 'reviewer' }, state: 'APPROVED', commit_id: sha,
      submitted_at: '2026-09-13T06:15:00Z' }],
    statuses: [{ id: 80, context: releaseReviewStatus, state: 'success', creator: { login: 'github-actions[bot]' },
      target_url: url(30), created_at: '2026-09-13T07:08:00Z',
      description: qualificationDescription({ approvalMode: mode, sourceCommit: sha }) }],
  };
  data.rules = [{ type: 'required_status_checks', parameters: { strict_required_status_checks_policy: true,
    required_status_checks: [...releaseBuildChecks, releaseReviewStatus].map(context => ({ context })) } }];
  data.checks = data.jobs[10].map(job => ({ name: job.name, head_sha: sha, status: 'completed',
    conclusion: 'success', app: { id: 15368, slug: 'github-actions' }, check_suite: { id: 100 }, url: job.check_run_url }));
  const calls = [];
  const posts = [];
  const count = (items, field) => ({ total_count: items.length, [field]: structuredClone(items) });
  const api = async (endpoint, method = 'GET', body) => {
    qualificationRequestUrl(endpoint, method, true);
    calls.push(endpoint);
    if (method === 'POST') { posts.push({ endpoint, body }); return {}; }
    if (!endpoint) return structuredClone(data.repo);
    if (endpoint.startsWith('git/ref/heads/')) {
      const name = endpoint.split('/').at(-1);
      return { ref: `refs/heads/${name}`, object: { type: 'commit', sha: data.heads[name] } };
    }
    let match = /^actions\/runs\/(\d+)(\/attempts\/1\/jobs\?per_page=100)?$/.exec(endpoint);
    if (match) return match[2] ? count(data.jobs[match[1]], 'jobs') : structuredClone(data.runs[match[1]]);
    match = /^actions\/workflows\/([^/]+)$/.exec(endpoint);
    if (match) return structuredClone(data.definitions[match[1]]);
    if (endpoint.startsWith('actions/workflows/ci.yml/runs?')) return count(data.ciRuns, 'workflow_runs');
    if (endpoint.startsWith('actions/workflows/qualify-canonical-release.yml/runs?')) return count(data.qualifications, 'workflow_runs');
    if (endpoint.startsWith('rules/branches/')) return structuredClone(data.rules);
    if (endpoint.endsWith('/check-runs?per_page=100')) return count(data.checks, 'check_runs');
    if (endpoint.endsWith('/comments?per_page=100')) return structuredClone(data.comments);
    if (endpoint.endsWith('/statuses?per_page=100')) return structuredClone(data.statuses);
    match = /^collaborators\/([^/]+)\/permission$/.exec(endpoint);
    if (match) return { user: { login: match[1] }, permission: data.permissions[match[1]] };
    if (endpoint.startsWith('contents/')) return { encoding: 'base64', content: Buffer.from(data.owners).toString('base64') };
    if (endpoint === 'pulls/60') return structuredClone(data.pr);
    if (endpoint === 'pulls/60/reviews?per_page=100') return structuredClone(data.reviews);
    throw new Error(`Unexpected fixture route: ${endpoint}`);
  };
  const env = { GITHUB_REPOSITORY: repository, GITHUB_EVENT_NAME: 'workflow_run', GITHUB_RUN_ATTEMPT: '1',
    GITHUB_RUN_ID: '30', GITHUB_REF: 'refs/heads/development', GITHUB_SHA: defaultSha,
    GITHUB_WORKFLOW_SHA: defaultSha, GITHUB_WORKFLOW_REF: `${repository}/${evidenceWorkflow}@refs/heads/development`,
    RELEASE_APPROVAL_MODE: mode };
  return { data, api, calls, posts, env, sha, channel, mode };
}

for (const channel of ['stable', 'insider']) {
  for (const mode of ['single-maintainer', 'separation-of-duties']) {
    test(`${channel}/${mode}: fresh canonical review and CI qualify without publication`, async () => {
      const f = fixture(channel, mode);
      const evidence = await verifyQualification(f.api, '20', mode, now);
      assert.equal(evidence.sourceCommit, f.sha);
      assert.equal(evidence.approvalMode, mode);
      assert.equal(f.posts.length, 0);
      assert.doesNotMatch(JSON.stringify(evidence), /jpapiez|reviewer|APPROVED|permission/);
      f.data.runs[30].status = 'in_progress';
      await recordQualification(f.api, '20', f.env, now);
      assert.deepEqual(f.posts, [{ endpoint: `statuses/${f.sha}`, body: {
        context: releaseReviewStatus, state: 'success', target_url: url(30),
        description: qualificationDescription(evidence),
      } }]);
      f.data.runs[30].status = 'completed';
      assert.equal((await verifyCanonicalReleaseEvidence(f.api, f.sha, channel, mode, now)).sourceCommit, f.sha);
    });
  }
}

const invalid = [
  ['non-canonical SHA', f => { f.data.runs[10].head_sha = 'c'.repeat(40); }],
  ['swapped branch', f => { f.data.runs[10].head_branch = 'development'; }],
  ['feature workflow', f => { f.data.runs[20].head_branch = 'feature'; }],
  ['old default workflow', f => { f.data.runs[20].head_sha = 'c'.repeat(40); }],
  ['fork CI', f => { f.data.runs[10].head_repository.full_name = 'outsider/PrintFarmer'; }],
  ['forged workflow ID', f => { f.data.runs[10].workflow_id = 7; }],
  ['disabled workflow', f => { f.data.definitions['ci.yml'].state = 'disabled_manually'; }],
  ['repository dispatch', f => { f.data.runs[20].event = 'repository_dispatch'; }],
  ['push qualification', f => { f.data.runs[20].event = 'push'; }],
  ['workflow-run qualification', f => { f.data.runs[20].event = 'workflow_run'; }],
  ['PR CI replay', f => { f.data.runs[10].event = 'pull_request'; }],
  ['push CI not fresh manual execution', f => { f.data.runs[10].event = 'push'; }],
  ['CI rerun', f => { f.data.runs[10].run_attempt = 2; }],
  ['qualifier rerun', f => { f.data.runs[20].run_attempt = 2; }],
  ['changed actor', f => { f.data.runs[20].triggering_actor.login = 'other'; }],
  ['failed CI', f => { f.data.runs[10].conclusion = 'failure'; }],
  ['cancelled qualification', f => { f.data.runs[20].conclusion = 'cancelled'; }],
  ['partial checks', f => { f.data.jobs[10].pop(); f.data.jobs[10].shift(); }],
  ['skipped required checks', f => { f.data.jobs[10][0].conclusion = 'skipped'; }],
  ['failed optional check', f => { f.data.jobs[10].at(-1).conclusion = 'failure'; }],
  ['foreign job SHA', f => { f.data.jobs[10][0].head_sha = 'c'.repeat(40); }],
  ['job attempt reuse', f => { f.data.jobs[10][0].run_attempt = 2; }],
  ['duplicate job names', f => { f.data.jobs[10].push(f.data.jobs[10][0]); }],
  ['missing required branch policy', f => { f.data.rules = []; }],
  ['non-strict checks', f => { f.data.rules[0].parameters.strict_required_status_checks_policy = false; }],
  ['missing mandatory review policy', f => { f.data.rules[0].parameters.required_status_checks.pop(); }],
  ['additional missing configured check', f => { f.data.rules[0].parameters.required_status_checks.push({ context: 'extra' }); }],
  ['insufficient check integration evidence', f => { f.data.rules[0].parameters.required_status_checks[0].integration_id = 123; }],
  ['copied checks from another run', f => { f.data.checks[0].check_suite.id = 999; }],
  ['check URL not bound to CI job', f => { f.data.checks[0].url = 'https://example.com'; }],
  ['expired CI', f => { f.data.runs[10].created_at = '2026-09-11T05:00:00Z'; }],
  ['CI newer than review run', f => { f.data.runs[10].updated_at = '2026-09-13T07:30:00Z'; }],
  ['newer failed CI', f => { f.data.ciRuns.push({ ...f.data.runs[10], id: 11, conclusion: 'failure' }); }],
  ['missing comment', f => { f.data.comments = []; }],
  ['stale comment SHA', f => { f.data.comments[0].commit_id = 'c'.repeat(40); }],
  ['stale comment run', f => { f.data.comments[0].body = confirmationBody(f.sha, '9', f.mode); }],
  ['copied PR verdict', f => { f.data.comments[0].body = `<!-- squad-verdict -->\nSquad-Head-SHA: ${f.sha}`; }],
  ['edited confirmation', f => { f.data.comments[0].updated_at = '2026-09-13T06:40:00Z'; }],
  ['predated confirmation', f => { f.data.comments[0].created_at = f.data.comments[0].updated_at = '2026-09-13T05:30:00Z'; }],
  ['quoted confirmation', f => { f.data.comments[0].body = `> ${f.data.comments[0].body}`; }],
  ['non-owner confirmation', f => { f.data.comments[0].user.login = 'reviewer'; }],
  ['bot confirmation', f => { f.data.comments[0].user.type = 'Bot'; }],
  ['lost admin permission', f => { f.data.permissions.jpapiez = 'write'; }],
  ['no permission', f => { f.data.permissions.jpapiez = 'read'; }],
  ['replayed CI', f => { f.data.qualifications.push({ ...f.data.runs[20], id: 19 }); }],
  ['superseding qualification', f => { f.data.qualifications.push({ ...f.data.runs[20], id: 21,
    display_title: qualificationTitle(f.channel, '11', '51', '0', f.mode) }); }],
];
for (const [name, mutate] of invalid) {
  test(`reject ${name}`, async () => {
    const f = fixture();
    mutate(f);
    await assert.rejects(verifyQualification(f.api, '20', f.mode, now));
    assert.equal(f.posts.length, 0);
  });
}

for (const mode of [undefined, '', 'unknown', 'SINGLE-MAINTAINER']) {
  test(`missing/unknown mode ${String(mode)} fails before API or writes`, async () => {
    const f = fixture();
    await assert.rejects(verifyQualification(f.api, '20', mode, now));
    assert.equal(f.calls.length, 0);
  });
}

for (const [name, mutate] of [
  ['self-authored native PR', f => { f.data.pr.user.login = 'reviewer'; }],
  ['native reviewer is initiator', f => { f.data.runs[10].actor.login = 'reviewer'; }],
  ['squash predecessor', f => { f.data.pr.head.sha = 'c'.repeat(40); }],
  ['stale native review', f => { f.data.reviews[0].commit_id = 'c'.repeat(40); }],
  ['native approval before CI', f => { f.data.reviews[0].submitted_at = '2026-09-13T05:00:00Z'; }],
  ['native approval after confirmation', f => { f.data.reviews[0].submitted_at = '2026-09-13T06:45:00Z'; }],
  ['missing native review', f => { f.data.reviews = []; }],
  ['non-code-owner', f => { f.data.owners = '* @someone-else'; }],
  ['unsupported complex code ownership', f => { f.data.owners = '/src/ @reviewer'; }],
  ['native change request', f => { f.data.reviews.push({ ...f.data.reviews[0], id: 71, state: 'CHANGES_REQUESTED' }); }],
  ['dismissed approval', f => { f.data.reviews.push({ ...f.data.reviews[0], id: 71, state: 'DISMISSED' }); }],
  ['no native reviewer permission', f => { f.data.permissions.reviewer = 'triage'; }],
]) {
  test(`separation-of-duties rejects ${name}`, async () => {
    const f = fixture('stable', 'separation-of-duties');
    mutate(f);
    await assert.rejects(verifyQualification(f.api, '20', f.mode, now));
  });
}

test('HEAD movement during review fails closed', async () => {
  const f = fixture();
  const api = async (...args) => {
    const response = await f.api(...args);
    if (args[0].endsWith('/comments?per_page=100')) f.data.heads.main = 'c'.repeat(40);
    return response;
  };
  await assert.rejects(verifyQualification(api, '20', f.mode, now), /HEAD moved/);
});

test('failed/cancelled validation replaces success with bounded failure, never publication', async () => {
  const f = fixture();
  f.data.runs[30].status = 'in_progress';
  f.data.runs[20].conclusion = 'cancelled';
  await assert.rejects(recordQualification(f.api, '20', f.env, now));
  assert.equal(f.posts.length, 1);
  assert.equal(f.posts[0].body.state, 'failure');
  assert.equal(f.posts[0].endpoint, `statuses/${f.sha}`);
});

test('movement during final POST retracts the status', async () => {
  const f = fixture();
  f.data.runs[30].status = 'in_progress';
  const api = async (...args) => {
    const response = await f.api(...args);
    if (args[1] === 'POST' && args[2].state === 'success') f.data.heads.main = 'c'.repeat(40);
    return response;
  };
  await assert.rejects(recordQualification(api, '20', f.env, now), /HEAD moved/);
  assert.deepEqual(f.posts.map(post => post.body.state), ['success', 'failure']);
});

for (const [name, mutate] of [
  ['forged status creator', f => { f.data.statuses[0].creator.login = 'someone'; }],
  ['forged status URL', f => { f.data.statuses[0].target_url = 'https://example.com'; }],
  ['PR verdict replay', f => { f.data.statuses[0].description = `REVIEWED (self-attested) @ ${f.sha.slice(0, 12)} by bishop`; }],
  ['wrong writer path', f => { f.data.runs[30].path = '.github/workflows/ci.yml'; }],
  ['cancelled writer after POST', f => { f.data.runs[30].conclusion = 'cancelled'; }],
  ['writer still running', f => { f.data.runs[30].status = 'in_progress'; }],
  ['writer rerun', f => { f.data.runs[30].run_attempt = 2; }],
  ['writer from feature', f => { f.data.runs[30].head_branch = 'feature'; }],
  ['status outside writer lifetime', f => { f.data.statuses[0].created_at = '2026-09-13T05:00:00Z'; }],
  ['missing writer job', f => { f.data.jobs[30] = []; }],
]) {
  test(`release consumption rejects ${name}`, async () => {
    const f = fixture();
    mutate(f);
    await assert.rejects(verifyCanonicalReleaseEvidence(f.api, f.sha, f.channel, f.mode, now));
  });
}

test('API errors and truncated evidence do not qualify', async () => {
  const f = fixture();
  for (const api of [
    async () => { throw new Error('HTTP 403'); },
    async (...args) => args[0].includes('/jobs?') ? { total_count: 100, jobs: [] } : f.api(...args),
  ]) await assert.rejects(verifyQualification(api, '20', f.mode, now));
});

test('qualification API cannot reserve, deploy, publish, dispatch, or write arbitrary statuses', () => {
  for (const [route, method] of [
    ['git/refs', 'POST'], ['git/refs/heads/release-ledger', 'PATCH'], ['releases', 'POST'],
    ['deployments', 'POST'], ['actions/workflows/ci.yml/dispatches', 'POST'],
    ['statuses/not-a-sha', 'POST'], [`statuses/${stableSha}`, 'GET'],
    ['../actions', 'GET'], ['https://example.com', 'GET'],
  ]) assert.throws(() => qualificationRequestUrl(route, method, true));
  assert.throws(() => qualificationRequestUrl(`statuses/${stableSha}`, 'POST'));
});

test('malformed run titles and unknown/native mode combinations fail closed', () => {
  assert.throws(() => parseQualificationTitle('Squad review record for PR #1'));
  assert.throws(() => qualificationTitle('stable', '10\n', '50', '0', 'single-maintainer'));
  assert.throws(() => qualificationTitle('stable', '10', '50', '0', 'separation-of-duties'));
  assert.throws(() => qualificationTitle('insider', '10', '50', '60', 'single-maintainer'));
});

test('workflow trust/permissions and release gates remain separate from publishing', () => {
  const qualify = load(read('.github/workflows/qualify-canonical-release.yml'));
  const writer = load(read('.github/workflows/record-canonical-qualification.yml'));
  assert.deepEqual(Object.keys(qualify.on), ['workflow_dispatch']);
  assert.deepEqual(Object.keys(writer.on), ['workflow_run']);
  assert.equal(qualify.permissions.contents, 'read');
  assert.equal(qualify.jobs.verify.permissions.statuses, undefined);
  assert.deepEqual(Object.entries(writer.jobs.record.permissions).filter(([, value]) => value === 'write'),
    [['statuses', 'write']]);
  for (const [path, workflow] of [[qualificationWorkflow, qualify], [evidenceWorkflow, writer]]) {
    assert.doesNotMatch(read(path), /secrets\.|secrets:|environment:|create-github-app-token|download-artifact|npm |packages:|id-token:/);
    for (const job of Object.values(workflow.jobs)) {
      const checkout = job.steps.find(step => step.uses?.startsWith('actions/checkout@'));
      assert.equal(checkout.with.ref, '${{ github.workflow_sha }}');
      assert.equal(checkout.with['persist-credentials'], false);
    }
  }
  const control = read('scripts/ci/release-control.mjs');
  assert.equal((control.match(/await verifyCanonicalReleaseEvidence/g) ?? []).length, 2);
  assert.match(control, /qualificationClient\(env\.GH_TOKEN\)/);
  assert.match(read('.github/workflows/ci.yml'), /test-canonical-qualification\.mjs/);
});
