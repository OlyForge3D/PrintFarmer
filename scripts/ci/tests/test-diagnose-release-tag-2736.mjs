import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { load } from 'js-yaml';
import { githubClient } from '../release-github.mjs';
import { diagnostic, diagnosticFailure, preflightDiagnostic, preflightProtectedDiagnostic } from '../diagnose-release-tag-2736.mjs';
import { protectionFixture } from './fixtures/release-protection.mjs';

let instance = 0;
const fresh = () => import(`../diagnose-release-tag-2736.mjs?test=${instance++}`);
const historyPath = 'actions/workflows/diagnose-release-tag-2736.yml/runs?per_page=100';
const priorJobsPath = `actions/runs/${diagnostic.priorRun}/attempts/1/jobs?per_page=100`;
const tagPath = 'git/ref/tags/v0.2.3-insider.1';
const repository = 'OlyForge3D/PrintFarmer';
const control = 'a'.repeat(40);
const sentinel = 'private-url-token-policy-sentinel';

function fixture() {
  const actor = { id: 5460061, login: 'jpapiez', type: 'User' };
  const repo = { full_name: repository, id: 1044049720 };
  const env = {
    GITHUB_REPOSITORY: repository, GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REF: 'refs/heads/development', GITHUB_SHA: control, GITHUB_WORKFLOW_SHA: control,
    GITHUB_WORKFLOW_REF: `${repository}/${diagnostic.workflow}@refs/heads/development`,
    GITHUB_RUN_ID: '42', GITHUB_RUN_NUMBER: '2', GITHUB_RUN_ATTEMPT: '1',
    GITHUB_ACTOR: actor.login, GITHUB_ACTOR_ID: String(actor.id), GITHUB_TRIGGERING_ACTOR: actor.login,
    RELEASE_APPROVAL_MODE: 'single-maintainer',
    RELEASE_PUBLICATION_ENVIRONMENT: 'release-insider',
    RELEASE_PUBLISHER_INSTALLATION_ID: diagnostic.installationId,
  };
  const run = { id: 42, run_number: 2, run_attempt: 1, workflow_id: diagnostic.workflowId, path: diagnostic.workflow,
    head_sha: control, head_branch: 'development', repository: repo, head_repository: repo,
    event: 'workflow_dispatch', status: 'in_progress', actor, triggering_actor: actor };
  const event = { ref: 'development', repository: repo, sender: actor, inputs: {} };
  const definition = { id: diagnostic.workflowId, path: diagnostic.workflow, state: 'active' };
  const prior = { ...run, id: diagnostic.priorRun, run_number: 1, head_sha: diagnostic.priorSource,
    status: 'completed', conclusion: 'failure' };
  const jobs = [
    { id: 105005241763, run_id: diagnostic.priorRun, run_attempt: 1, head_sha: diagnostic.priorSource,
      name: 'Verify owner and first-run boundary without publisher credentials',
      status: 'completed', conclusion: 'failure', steps: [
        [1, 'Set up job', 'success'],
        [2, 'Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'success'],
        [3, 'Run actions/setup-node@820762786026740c76f36085b0efc47a31fe5020', 'success'],
        [4, 'Read-only admission', 'failure'],
        [7, 'Post Run actions/setup-node@820762786026740c76f36085b0efc47a31fe5020', 'skipped'],
        [8, 'Post Run actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'success'],
        [9, 'Complete job', 'success'],
      ].map(([number, name, conclusion]) => ({ number, name, conclusion, status: 'completed' })) },
    { id: 105005305468, run_id: diagnostic.priorRun, run_attempt: 1, head_sha: diagnostic.priorSource,
      name: 'Attempt the single fixed tag request and stop',
      status: 'completed', conclusion: 'skipped', steps: [] },
  ];
  const original = { ...run, id: Number(diagnostic.failedRun),
    path: '.github/workflows/consolidated-release.yml', head_sha: diagnostic.source,
    status: 'completed', conclusion: 'failure' };
  const tag = { sha: diagnostic.tagObject, tag: 'v0.2.3-insider.1',
    object: { type: 'commit', sha: diagnostic.source }, message: sentinel };
  const policy = protectionFixture('insider', 'single-maintainer', Number(diagnostic.appId));
  const requests = [];
  const overrides = new Map();
  let created = false;
  const client = label => githubClient('fixture-only', async (url, options) => {
    const endpoint = decodeURIComponent(new URL(url).pathname.split(`/repos/${repository}/`)[1]) + new URL(url).search;
    requests.push({ label, endpoint, method: options.method, body: options.body });
    if (overrides.has(endpoint)) {
      const override = overrides.get(endpoint);
      const response = typeof override === 'function' ? await override(options, requests) : override;
      return response instanceof Response ? response : Response.json(response);
    }
    if (options.method !== 'GET') {
      assert.equal(label, 'publisher');
      assert.equal(endpoint, 'git/refs');
      assert.equal(options.method, 'POST');
      assert.deepEqual(JSON.parse(options.body), { ref: diagnostic.ref, sha: diagnostic.tagObject });
      assert.equal(created, false);
      created = true;
      return Response.json({ ref: diagnostic.ref, object: { type: 'tag', sha: diagnostic.tagObject } }, { status: 201 });
    }
    let data;
    if (endpoint === 'actions/runs/42') data = run;
    else if (endpoint === `actions/runs/${diagnostic.priorRun}`) data = prior;
    else if (endpoint === priorJobsPath) data = { total_count: 2, jobs };
    else if (endpoint === `actions/runs/${diagnostic.failedRun}`) data = original;
    else if (endpoint === 'actions/workflows/diagnose-release-tag-2736.yml') data = definition;
    else if (endpoint === historyPath) data = { total_count: 2, workflow_runs: [run, prior] };
    else if (endpoint.startsWith('actions/workflows/consolidated-release.yml/runs?')) {
      data = { total_count: 0, workflow_runs: [] };
    } else if (endpoint === 'collaborators/jpapiez/permission') {
      data = { permission: 'admin', role_name: 'admin', user: actor };
    } else if (endpoint === 'git/ref/heads/development') data = { object: { sha: control } };
    else if (endpoint === `compare/${diagnostic.source}...${control}`) {
      data = { status: 'ahead', merge_base_commit: { sha: diagnostic.source } };
    } else if (endpoint === `git/tags/${diagnostic.tagObject}`) data = tag;
    else if (endpoint === tagPath) {
      if (!created) return Response.json({ message: 'Not Found' }, { status: 404 });
      data = { ref: diagnostic.ref, object: { type: 'tag', sha: diagnostic.tagObject } };
    } else if (endpoint === 'rulesets?per_page=100&includes_parents=true') {
      data = [...policy.rulesets, policy.branchRuleset];
    } else data = await policy.api(endpoint);
    return Response.json(data);
  });
  return { env, run, prior, jobs, event, original, definition, tag, policy, overrides, requests,
    get protectedEnv() { return { ...env, RELEASE_PUBLISHER_APP_ID: diagnostic.appId }; },
    read: client('read'), publisher: client('publisher') };
}

test('actual YAML is owner boundary then one fixed request using existing credentials and concurrency', () => {
  const yaml = readFileSync('.github/workflows/diagnose-release-tag-2736.yml', 'utf8');
  const workflow = load(yaml);
  const canonical = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  assert.deepEqual(Object.keys(workflow.on), ['workflow_dispatch']);
  assert.equal(workflow.on.workflow_dispatch, null);
  assert.deepEqual(workflow.permissions, { contents: 'read', actions: 'read' });
  assert.deepEqual(workflow.concurrency, { group: 'release-tag-diagnostic-2736', 'cancel-in-progress': false });
  assert.equal(canonical.concurrency.group, 'release-${{ inputs.channel }}');
  assert.equal(canonical.concurrency['cancel-in-progress'], false);
  assert.deepEqual(Object.keys(workflow.jobs), ['boundary', 'request']);
  assert.equal(workflow.jobs.boundary.environment, undefined);
  assert.equal(workflow.env.RELEASE_PUBLISHER_APP_ID, undefined);
  assert.equal(workflow.jobs.boundary.env?.RELEASE_PUBLISHER_APP_ID, undefined);
  assert.doesNotMatch(JSON.stringify(workflow.jobs.boundary), /secrets\.|id-token|PUBLISHER_TOKEN/);
  const request = workflow.jobs.request;
  assert.equal(request.needs, 'boundary');
  assert.equal(request.environment, 'release-insider');
  assert.equal(request.env.RELEASE_PUBLISHER_APP_ID, '${{ vars.RELEASE_PUBLISHER_APP_ID }}');
  assert.deepEqual(request.concurrency, { group: 'release-insider', 'cancel-in-progress': false });
  assert.equal(request.concurrency.group, canonical.concurrency.group.replace('${{ inputs.channel }}', 'insider'));
  assert.equal(publisher.concurrency['cancel-in-progress'], false);
  const mint = request.steps.findIndex(step => step.id === 'publisher');
  assert.equal(workflow.jobs.boundary.steps.at(-1).run, 'node scripts/ci/diagnose-release-tag-2736.mjs preflight');
  assert.equal(request.steps[mint - 1].run, 'node scripts/ci/diagnose-release-tag-2736.mjs protected-preflight');
  assert.equal(request.steps[mint - 1].env.RELEASE_PUBLISHER_APP_ID, undefined);
  assert.deepEqual(request.steps[mint].with, publisher.jobs.publish.steps.find(step => step.id === 'publisher').with);
  assert.equal(request.steps[mint].uses, publisher.jobs.publish.steps.find(step => step.id === 'publisher').uses);
  assert.equal(request.steps.length, mint + 2);
  assert.equal(request.steps.at(-1).run, 'node scripts/ci/diagnose-release-tag-2736.mjs request');
  assert.equal((yaml.match(/secrets\./g) ?? []).length, 1);
  assert.doesNotMatch(yaml, /workflow_call|id-token|release-control\.mjs|cosign|docker build|npm |dotnet |qualification|upload-artifact/);
  for (const job of Object.values(workflow.jobs)) {
    assert.equal(job['timeout-minutes'], 5);
    for (const step of job.steps) {
      assert.equal(step['continue-on-error'], undefined);
      if (step.uses) assert.match(step.uses, /@[a-f0-9]{40}$/);
      if (step.uses?.startsWith('actions/checkout@')) {
        assert.equal(step.with.ref, '${{ github.workflow_sha }}');
        assert.equal(step.with['persist-credentials'], false);
      }
    }
  }
  assert.match(readFileSync('.github/workflows/ci.yml', 'utf8'), /test-diagnose-release-tag-2736\.mjs/);
});

test('read-only boundary binds original run and annotation without ledger or protected credentials', async () => {
  const f = fixture();
  assert.equal(f.env.RELEASE_PUBLISHER_APP_ID, undefined);
  await preflightDiagnostic(f.env, f.read, f.event);
  assert.ok(f.requests.every(item => item.label === 'read' && item.method === 'GET'));
  assert.ok(f.requests.some(item => item.endpoint === `git/tags/${diagnostic.tagObject}`));
  assert.doesNotMatch(JSON.stringify(f.requests), /release-ledger|git\/blobs|git\/trees/);
});

test('separate job scopes admit absent repository App ID but reject missing/wrong protected values before mint', async () => {
  const workflow = load(readFileSync('.github/workflows/diagnose-release-tag-2736.yml', 'utf8'));
  const f = fixture();
  await preflightDiagnostic(f.env, f.read, f.event);
  for (const value of [diagnostic.appId, undefined, '', '123']) {
    const protectedEnv = { ...f.env };
    for (const [key, expression] of Object.entries(workflow.jobs.request.env)) {
      assert.equal(expression, '${{ vars.RELEASE_PUBLISHER_APP_ID }}');
      protectedEnv[key] = value;
    }
    let minted = false;
    const beforeMint = async () => {
      await preflightProtectedDiagnostic(protectedEnv, f.read, f.event);
      minted = true;
    };
    if (value === diagnostic.appId) {
      await beforeMint();
      assert.equal(minted, true);
    } else {
      await assert.rejects(beforeMint(), /approved App/);
      assert.equal(minted, false);
      const module = await fresh();
      await assert.rejects(module.executeDiagnostic(protectedEnv, f.read, f.publisher, f.event), /approved App/);
    }
    assert.ok(f.requests.every(item => item.method === 'GET' && item.label === 'read'));
  }
});

test('negative owner, workflow, control, original source, tag and environment bindings fail before credentials', async () => {
  const changes = [
    f => { f.env.GITHUB_ACTOR = 'other'; },
    f => { f.env.GITHUB_ACTOR_ID = '1'; },
    f => { f.env.GITHUB_TRIGGERING_ACTOR = 'other'; },
    f => { f.run.actor = { id: 2, login: 'jpapiez', type: 'User' }; },
    f => { f.run.triggering_actor = { id: 2, login: 'other', type: 'User' }; },
    f => { f.event.sender = { id: 2, login: 'jpapiez', type: 'User' }; },
    f => { f.env.GITHUB_REPOSITORY = 'other/repo'; },
    f => { f.env.GITHUB_REF = 'refs/heads/main'; },
    f => { f.env.GITHUB_WORKFLOW_REF = 'other'; },
    f => { f.env.GITHUB_WORKFLOW_SHA = diagnostic.source; },
    f => { f.run.head_sha = diagnostic.source; },
    f => { f.definition.id = 10; },
    f => { f.definition.state = 'disabled_manually'; },
    f => { f.run.path = '.github/workflows/consolidated-release.yml'; },
    f => { f.event.ref = 'refs/heads/main'; },
    f => { f.run.status = 'completed'; f.run.conclusion = 'cancelled'; },
    f => { f.env.GITHUB_EVENT_NAME = f.run.event = 'schedule'; },
    f => { f.env.RELEASE_APPROVAL_MODE = 'separation-of-duties'; },
    f => { f.event.inputs = { source_sha: diagnostic.source }; },
    f => { f.original.run_attempt = 2; },
    f => { f.original.head_sha = control; },
    f => { f.original.conclusion = 'success'; },
    f => { f.tag.sha = control; },
    f => { f.tag.tag = 'v0.2.3-insider.2'; },
    f => { f.tag.object.type = 'tag'; },
    f => { f.tag.object.sha = control; },
    f => { f.policy.environment.can_admins_bypass = true; },
    f => { f.overrides.set('collaborators/jpapiez/permission',
      { permission: 'write', role_name: 'write', user: f.run.actor }); },
    f => { f.overrides.set(tagPath, { ref: diagnostic.ref, object: { type: 'tag', sha: diagnostic.tagObject } }); },
    f => { f.overrides.set(tagPath, Response.json({ message: sentinel }, { status: 403 })); },
  ];
  for (const change of changes) {
    const f = fixture(); change(f);
    await assert.rejects(preflightDiagnostic(f.env, f.read, f.event), undefined, change.toString());
    assert.ok(f.requests.every(item => item.label === 'read' && item.method === 'GET'));
  }
});

test('first/third runs, attempts two and recreated workflow identities are denied before history', async () => {
  for (const mutate of [
    f => { f.env.GITHUB_RUN_ATTEMPT = '2'; f.run.run_attempt = 2; },
    f => { f.env.GITHUB_RUN_NUMBER = '1'; f.run.run_number = 1; },
    f => { f.env.GITHUB_RUN_NUMBER = '3'; f.run.run_number = 3; },
    f => { f.run.run_number = 1; },
    f => { f.run.run_attempt = 2; },
    f => { f.definition.id = f.run.workflow_id = 10; },
    f => { delete f.run.run_number; },
  ]) {
    const f = fixture(); mutate(f);
    await assert.rejects(preflightDiagnostic(f.env, f.read, f.event));
    assert.equal(f.requests.some(item => item.endpoint === historyPath), false);
    assert.ok(f.requests.every(item => item.method === 'GET'));
  }
});

test('complete history includes prior failures/cancellations and denies extra, missing, malformed or inaccessible history', async () => {
  for (const history of [
    { total_count: 0, workflow_runs: [] },
    { total_count: 2, workflow_runs: [] },
    { total_count: 1 },
    { total_count: 1, workflow_runs: [{ id: 43 }] },
    Response.json({ message: sentinel }, { status: 403 }),
  ]) {
    const f = fixture(); f.overrides.set(historyPath, history);
    await assert.rejects(preflightDiagnostic(f.env, f.read, f.event));
  }
  for (const history of [
    f => ({ total_count: 1, workflow_runs: [f.run] }),
    f => ({ total_count: 2, workflow_runs: [f.run, f.run] }),
    f => ({ total_count: 2, workflow_runs: [f.run, { ...f.prior, run_attempt: 2 }] }),
    f => ({ total_count: 3, workflow_runs: [f.run, f.prior, { ...f.run, id: 43 }] }),
    f => ({ total_count: 2, workflow_runs: [f.run, { ...f.prior, actor: undefined }] }),
    f => ({ total_count: 2, workflow_runs: [f.run, { ...f.prior, repository: undefined }] }),
  ]) {
    const f = fixture(); f.overrides.set(historyPath, history(f));
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event), /history|evidence/);
    assert.ok(f.requests.every(item => item.method === 'GET' && item.label === 'read'));
  }
  for (const conclusion of ['cancelled', 'failure', 'success', 'skipped', 'timed_out']) {
    const f = fixture();
    f.overrides.set(historyPath, { total_count: 2,
      workflow_runs: [f.run, { ...f.run, id: 41, status: 'completed', conclusion }] });
    await assert.rejects(preflightDiagnostic(f.env, f.read, f.event), /history/);
  }
});

test('prior source/run/job/step evidence is exact, complete and proves no request; every rejection makes zero writes', async () => {
  const changes = [
    f => { f.prior.id++; },
    f => { f.prior.head_sha = control; },
    f => { f.prior.run_attempt = 2; },
    f => { f.prior.run_number = 2; },
    f => { f.prior.workflow_id++; },
    f => { f.prior.path = '.github/workflows/other.yml'; },
    f => { f.prior.repository = { full_name: repository, id: 1 }; },
    f => { f.prior.actor = { ...f.prior.actor, id: 1 }; },
    ...['cancelled', 'success', 'skipped'].map(conclusion => f => { f.prior.conclusion = conclusion; }),
    f => { f.prior.status = 'in_progress'; },
    ...['failure', 'success', 'cancelled'].map(conclusion => f => { f.jobs[1].conclusion = conclusion; }),
    f => { f.jobs[1].status = 'in_progress'; },
    f => { f.jobs[1].steps = [{ name: 'Obtain existing protected publisher token' }]; },
    f => { f.jobs[0].conclusion = 'success'; },
    f => { f.jobs[0].steps[3].conclusion = 'success'; },
    f => { f.jobs[0].steps[2].conclusion = 'failure'; },
    f => { f.jobs[0].steps[3].name = 'Other failure'; },
    f => { f.jobs[0].steps[3].status = 'in_progress'; },
    f => { f.jobs[0].steps.pop(); },
    f => { f.jobs[0].steps.push({ name: 'Unexpected request' }); },
    ...[0, 1].flatMap(index => [
      f => { f.jobs[index].id++; },
      f => { f.jobs[index].name = 'Unrelated job'; },
      f => { f.jobs[index].head_sha = control; },
      f => { f.jobs[index].run_id++; },
      f => { f.jobs[index].run_attempt = 2; },
      f => { delete f.jobs[index].steps; },
    ]),
    ...[
      {}, { total_count: 2, jobs: [] }, { total_count: 0, jobs: [] },
      { total_count: 2, jobs: [undefined, {}] },
    ].map(response => f => { f.overrides.set(priorJobsPath, response); }),
    f => { f.jobs.push({ ...f.jobs[1], id: 123 }); },
    f => { f.overrides.set(`actions/runs/${diagnostic.priorRun}`,
      Response.json({ message: sentinel }, { status: 404 })); },
    f => { f.overrides.set(priorJobsPath, Response.json({ message: sentinel }, { status: 403 })); },
  ];
  for (const change of changes) {
    const f = fixture(); change(f);
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event),
      undefined, change.toString());
    assert.ok(f.requests.every(item => item.method === 'GET' && item.label === 'read'));
  }
});

test('history pagination is collected completely and a hidden earlier cancellation still blocks', async () => {
  const f = fixture();
  const first = Array.from({ length: 100 }, (_, i) => ({ ...f.run, id: i + 42 }));
  f.overrides.set(historyPath, () => Response.json({ total_count: 101, workflow_runs: first }, { headers: {
    link: `<https://api.github.com/repos/${repository}/${historyPath}&page=2>; rel="next"`,
  } }));
  f.overrides.set(`${historyPath}&page=2`, { total_count: 101,
    workflow_runs: [{ ...f.run, id: 41, status: 'completed', conclusion: 'cancelled' }] });
  await assert.rejects(preflightDiagnostic(f.env, f.read, f.event), /history/);
  assert.ok(f.requests.some(item => item.endpoint === `${historyPath}&page=2`));
  f.overrides.set(historyPath, { total_count: 101, workflow_runs: first });
  await assert.rejects(preflightDiagnostic(f.env, f.read, f.event), /next page/);
});

test('canonical publishers of every active status block; unknown or unreadable collections fail closed', async () => {
  for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
    for (const response of [
      ...['stable', 'insider'].map(channel => ({ total_count: 1, workflow_runs: [{ id: 7, status, name: channel }] })),
      { total_count: 0 },
      Response.json({ message: sentinel }, { status: 403 }),
    ]) {
      const f = fixture();
      f.overrides.set(`actions/workflows/consolidated-release.yml/runs?status=${status}&per_page=100`, response);
      await assert.rejects(preflightDiagnostic(f.env, f.read, f.event));
    }
  }
});

test('wrong installation, environment, weakened or changing live protection prevents POST', async () => {
  for (const mutate of [
    f => { f.env.RELEASE_PUBLISHER_INSTALLATION_ID = '1'; },
    f => { f.env.RELEASE_PUBLICATION_ENVIRONMENT = 'other'; },
    f => { f.policy.rulesets[0].rules = []; },
    f => { f.policy.rulesets[2].bypass_actors[0].actor_id = 1; },
    f => { f.policy.branchRuleset.bypass_actors.push({ actor_id: 1 }); },
    f => { f.overrides.set('rulesets?per_page=100&includes_parents=true',
      [...f.policy.rulesets, { id: 99, name: sentinel, target: 'tag', enforcement: 'active' }]); },
    f => { let n = 0; f.overrides.set('rulesets?per_page=100&includes_parents=true', () =>
      [...f.policy.rulesets, { id: 99, name: `changed-${n++}`, target: 'branch', enforcement: 'active' }]); },
  ]) {
    const f = fixture(); mutate(f);
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event));
    assert.ok(f.requests.every(item => item.method === 'GET'));
  }
});

test('new history or publisher immediately before POST prevents all mutation', async () => {
  for (const path of [historyPath, 'actions/workflows/consolidated-release.yml/runs?status=in_progress&per_page=100']) {
    const f = fixture();
    let calls = 0;
    f.overrides.set(path, () => {
      calls++;
      return path === historyPath
        ? { total_count: calls === 1 ? 2 : 3, workflow_runs: calls === 1 ? [f.run, f.prior] : [f.run, f.prior, { ...f.run, id: 43 }] }
        : { total_count: calls === 1 ? 0 : 1, workflow_runs: calls === 1 ? [] : [{ id: 43 }] };
    });
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event));
    assert.ok(f.requests.every(item => item.method === 'GET'));
  }
});

test('prior rerun or request-job evidence changing immediately before POST prevents mutation', async () => {
  for (const path of [`actions/runs/${diagnostic.priorRun}`, priorJobsPath]) {
    const f = fixture();
    let calls = 0;
    f.overrides.set(path, () => {
      calls++;
      return path === priorJobsPath
        ? { total_count: 2, jobs: [f.jobs[0], { ...f.jobs[1], conclusion: calls === 1 ? 'skipped' : 'success' }] }
        : { ...f.prior, run_attempt: calls === 1 ? 1 : 2 };
    });
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event), /prior/);
    assert.equal(calls, 2);
    assert.ok(f.requests.every(item => item.method === 'GET'));
  }
});

test('success makes only the exact POST, verifies the ref and stops without release recovery', async () => {
  const f = fixture();
  f.overrides.set(historyPath, { total_count: 2, workflow_runs: [f.prior, f.run] });
  const module = await fresh();
  const result = await module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event);
  assert.match(result, /Tag created only/);
  assert.match(result, /Reservation remains incomplete/);
  const writes = f.requests.filter(item => item.method !== 'GET');
  assert.deepEqual(writes, [{ label: 'publisher', endpoint: 'git/refs', method: 'POST',
    body: JSON.stringify({ ref: diagnostic.ref, sha: diagnostic.tagObject }) }]);
  assert.equal(f.requests.at(-1).endpoint, tagPath);
  await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event), /already spent/);
  assert.equal(f.requests.filter(item => item.method !== 'GET').length, 1);
});

test('422, transport failure, ambiguous success and failed verification never retry or leak raw failure', async () => {
  for (const kind of ['422', 'transport', 'wrong-ref', 'wrong-object', 'wrong-type', 'verification-403']) {
    const f = fixture();
    if (kind === '422') f.overrides.set('git/refs', () =>
      Response.json({ message: 'Validation Failed', errors: [{ message: sentinel }] }, { status: 422 }));
    if (kind === 'transport') f.overrides.set('git/refs', () => { throw new Error(sentinel); });
    if (kind.startsWith('wrong') || kind === 'verification-403') {
      f.overrides.set(tagPath, (_, requests) => {
        if (!requests.some(item => item.method === 'POST')) return Response.json({ message: 'Not Found' }, { status: 404 });
        if (kind === 'verification-403') return Response.json({ message: sentinel }, { status: 403 });
        return { ref: kind === 'wrong-ref' ? 'refs/tags/wrong' : diagnostic.ref,
          object: { type: kind === 'wrong-type' ? 'commit' : 'tag',
            sha: kind === 'wrong-object' ? control : diagnostic.tagObject } };
      });
    }
    const module = await fresh();
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event), error => {
      const report = diagnosticFailure(error);
      assert.doesNotMatch(report, new RegExp(sentinel));
      assert.match(report, /no retry/);
      if (kind === '422') assert.match(report, /HTTP 422.*validation-failed/);
      return true;
    });
    const count = f.requests.length;
    await assert.rejects(module.executeDiagnostic(f.protectedEnv, f.read, f.publisher, f.event), /already spent/);
    assert.equal(f.requests.length, count);
    assert.equal(f.requests.filter(item => item.method === 'POST').length, 1);
  }
});
