import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { load } from 'js-yaml';
import { assessOwnerDispatch, readDispatchEvent, validateDispatchAssessment } from '../release-dispatch.mjs';
import { hash } from '../release-policy.mjs';

const repository = 'OlyForge3D/PrintFarmer';
const workflowCommit = 'a'.repeat(40);
const sourceCommit = 'b'.repeat(40);

function fixture() {
  const actor = { login: 'jpapiez', id: 7, type: 'User' };
  const transaction = {
    kind: 'release-transaction', schema: 2, repository,
    channel: 'stable', sourceBranch: 'main', sourceCommit, observedBranchHead: sourceCommit,
    workflowIdentity: `${repository}/.github/workflows/consolidated-release.yml@refs/heads/development`,
    workflowCommit, runId: '42', runAttempt: '1', approvalMode: 'single-maintainer',
  };
  const env = {
    GITHUB_REPOSITORY: repository, GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REF: 'refs/heads/development', GITHUB_SHA: workflowCommit,
    GITHUB_WORKFLOW_SHA: workflowCommit, GITHUB_WORKFLOW_REF: transaction.workflowIdentity,
    GITHUB_RUN_ID: '42', GITHUB_RUN_ATTEMPT: '1',
    GITHUB_ACTOR: actor.login, GITHUB_ACTOR_ID: String(actor.id), GITHUB_TRIGGERING_ACTOR: actor.login,
    RELEASE_APPROVAL_MODE: transaction.approvalMode, RELEASE_OPERATION: 'publish',
  };
  const event = { ref: 'development', repository: { full_name: repository, id: 10 },
    sender: structuredClone(actor), inputs: { channel: 'stable', operation: 'publish', source_sha: '' } };
  const run = {
    id: 42, run_attempt: 1, workflow_id: 9, path: '.github/workflows/consolidated-release.yml',
    repository: { full_name: repository, id: 10 }, head_repository: { full_name: repository, id: 10 },
    head_branch: 'development', head_sha: workflowCommit, event: 'workflow_dispatch', status: 'in_progress',
    actor: structuredClone(actor), triggering_actor: structuredClone(actor),
  };
  const definition = { id: 9, path: run.path, state: 'active' };
  const permission = { permission: 'admin', role_name: 'admin', user: structuredClone(actor) };
  const requests = [];
  const api = async (endpoint, method = 'GET') => {
    requests.push(endpoint);
    assert.equal(method, 'GET', 'Assessment must never write to GitHub');
    if (endpoint === 'actions/runs/42') return structuredClone(run);
    if (endpoint === 'actions/workflows/consolidated-release.yml') return structuredClone(definition);
    if (endpoint === 'collaborators/jpapiez/permission') return structuredClone(permission);
    if (endpoint === 'git/ref/heads/development') return { object: { sha: workflowCommit } };
    if (endpoint === 'git/ref/heads/main') return { object: { sha: sourceCommit } };
    if (endpoint === `compare/${sourceCommit}...${workflowCommit}`) {
      return { status: 'ahead', merge_base_commit: { sha: sourceCommit } };
    }
    throw new Error(`Unexpected fixture endpoint: ${endpoint}`);
  };
  const assess = (client = api) => assessOwnerDispatch({
    ...env, RELEASE_TRANSACTION: JSON.stringify(transaction),
  }, client, event);
  return { env, event, run, definition, transaction, permission, requests, api, assess };
}

test('initial owner dispatch assessment binds exact source, control, operation, run and attempt', async () => {
  for (const channel of ['stable', 'insider']) {
    const f = fixture();
    f.transaction.channel = f.event.inputs.channel = channel;
    f.transaction.sourceBranch = channel === 'stable' ? 'main' : 'development';
    const result = await f.assess();
    assert.equal(result.ownerDispatchEligible, true);
    assert.equal(result.transactionSha256, hash(f.transaction));
    assert.equal(result.workflowCommit, workflowCommit);
    assert.equal(result.sourceCommit, sourceCommit);
    assert.equal(result.channel, channel);
    assert.equal(result.operation, 'publish');
    assert.equal(result.runId, '42');
    assert.equal(result.executionAttempt, '1');
    assert.equal(result.kind, 'release-dispatch-assessment');
    assert.ok(f.requests.includes('collaborators/jpapiez/permission'));
    assert.doesNotMatch(JSON.stringify(result), /jpapiez|reviewers|actor_id|permission|token/);
    validateDispatchAssessment(result);
  }
});

test('explicit source and fully qualified control ref produce the same bound assessment', async () => {
  const f = fixture();
  const expected = await f.assess();
  f.event.ref = 'refs/heads/development';
  f.event.inputs.source_sha = sourceCommit;
  assert.deepEqual(await f.assess(), expected);
});

test('schedule, non-owner, reruns, abandonment and separation require the existing approval path', async () => {
  const changes = [
    f => {
      f.env.GITHUB_EVENT_NAME = f.run.event = 'schedule';
      f.transaction.channel = 'insider';
      f.transaction.sourceBranch = 'development';
      delete f.event.inputs;
    },
    f => {
      for (const actor of [f.run.actor, f.run.triggering_actor, f.event.sender]) {
        actor.login = 'maintainer'; actor.id = 8;
      }
      f.env.GITHUB_ACTOR = f.env.GITHUB_TRIGGERING_ACTOR = 'maintainer';
      f.env.GITHUB_ACTOR_ID = '8';
    },
    f => { f.env.GITHUB_RUN_ATTEMPT = '2'; f.run.run_attempt = 2; },
    f => {
      f.env.GITHUB_RUN_ATTEMPT = '2'; f.run.run_attempt = 2;
      f.run.triggering_actor = { id: 8, login: 'maintainer', type: 'User' };
      f.env.GITHUB_TRIGGERING_ACTOR = 'maintainer';
    },
    f => {
      f.transaction.approvalMode = f.env.RELEASE_APPROVAL_MODE = 'separation-of-duties';
    },
    f => {
      f.env.RELEASE_OPERATION = f.event.inputs.operation = 'abandon';
      f.env.RELEASE_ABANDONMENT_TARGET = f.event.inputs.reservation_target = 'c'.repeat(64);
      f.transaction.channel = 'insider'; f.transaction.sourceBranch = 'development';
    },
  ];
  for (const mutate of changes) {
    const f = fixture();
    mutate(f);
    const assessment = await f.assess();
    assert.equal(assessment.ownerDispatchEligible, false);
    assert.ok(!f.requests.includes('collaborators/jpapiez/permission'));
  }
});

test('original actor privileges cannot launder a non-owner rerun into owner consent', async () => {
  const f = fixture();
  f.env.GITHUB_RUN_ATTEMPT = '2'; f.run.run_attempt = 2;
  f.run.triggering_actor = { id: 8, login: 'maintainer', type: 'User' };
  f.env.GITHUB_TRIGGERING_ACTOR = 'maintainer';
  assert.equal((await f.assess()).ownerDispatchEligible, false);
  f.env.GITHUB_TRIGGERING_ACTOR = 'jpapiez';
  await assert.rejects(f.assess(), /Runner actor claims/);
});

test('actor spoofing, workflow substitution and changed transaction inputs fail closed', async () => {
  const changes = [
    f => { f.env.GITHUB_ACTOR = 'spoofed'; },
    f => { f.env.GITHUB_ACTOR_ID = '8'; },
    f => { f.env.GITHUB_TRIGGERING_ACTOR = 'spoofed'; },
    f => { f.run.actor.id = 8; },
    f => { delete f.run.triggering_actor; },
    f => { f.event.sender.id = 8; },
    f => { f.event.repository.id = 11; },
    f => { f.event.ref = 'refs/heads/main'; },
    f => { f.event.inputs.actor = 'jpapiez'; },
    f => { f.run.head_repository.full_name = 'stranger/fork'; },
    f => { f.run.head_repository.id = 11; },
    f => { f.run.path = '.github/workflows/untrusted.yml'; },
    f => { f.run.workflow_id = 10; },
    f => { f.run.head_sha = sourceCommit; },
    f => { f.run.head_branch = 'main'; },
    f => { f.run.status = 'completed'; },
    f => { f.run.event = 'workflow_call'; },
    f => { f.run.run_attempt = 2; },
    f => { f.run.id = 43; },
    f => { f.definition.state = 'disabled_manually'; },
    f => { f.event.inputs.channel = 'insider'; },
    f => { f.event.inputs.operation = 'abandon'; },
    f => { f.env.RELEASE_OPERATION = 'abandon'; },
    f => { f.event.inputs.source_sha = workflowCommit; },
    f => { f.transaction.sourceCommit = workflowCommit; },
    f => { f.transaction.approvalMode = 'separation-of-duties'; },
    f => { f.event.inputs.reservation_target = 'c'.repeat(64); },
    f => { f.event.inputs.source_sha = {}; },
  ];
  for (const mutate of changes) {
    const f = fixture();
    mutate(f);
    await assert.rejects(f.assess(), undefined, mutate.toString());
  }
});

test('live owner demotion denies eligibility; missing, spoofed or inaccessible role evidence rejects', async () => {
  for (const role of ['read', 'triage', 'write', 'maintain']) {
    const f = fixture();
    f.permission.permission = f.permission.role_name = role;
    assert.equal((await f.assess()).ownerDispatchEligible, false);
  }
  for (const mutate of [
    f => { delete f.permission.user; },
    f => { f.permission.user.id = 8; },
    f => { f.permission.user.type = 'Bot'; },
    f => { delete f.permission.permission; },
  ]) {
    const f = fixture(); mutate(f);
    await assert.rejects(f.assess(), /identity|permission evidence/);
  }
  const f = fixture();
  await assert.rejects(f.assess(async endpoint => {
    if (endpoint === 'collaborators/jpapiez/permission') throw new Error('HTTP 403');
    return f.api(endpoint);
  }), /HTTP 403/);
});

test('assessment is closed and cannot claim inherited dispatch consent', async () => {
  const original = await fixture().assess();
  for (const update of [
    { schema: 2 }, { executionAttempt: '2' }, { event: 'schedule' },
    { approvalMode: 'separation-of-duties' }, { actor: 'jpapiez' },
    { transactionSha256: '' }, { sourceCommit: '' }, { runId: '0' },
    { operation: 'abandon', reservationTarget: 'c'.repeat(64), channel: 'insider' },
  ]) {
    assert.throws(() => validateDispatchAssessment({ ...original, ...update }));
  }
});

test('only the bounded runner event file can supply dispatch inputs', () => {
  const root = mkdtempSync(join(tmpdir(), 'printfarmer-dispatch-'));
  const eventPath = join(root, '_github_workflow', 'event.json');
  mkdirSync(join(root, '_github_workflow'));
  try {
    const event = fixture().event;
    writeFileSync(eventPath, JSON.stringify(event));
    const env = { RUNNER_TEMP: root, GITHUB_EVENT_PATH: eventPath };
    assert.deepEqual(readDispatchEvent(env), event);
    assert.throws(() => readDispatchEvent({ ...env, GITHUB_EVENT_PATH: join(root, 'caller.json') }));
    writeFileSync(eventPath, '');
    assert.throws(() => readDispatchEvent(env), /empty/);
    writeFileSync(eventPath, '{');
    assert.throws(() => readDispatchEvent(env), /malformed/);
    writeFileSync(eventPath, 'x'.repeat(1024 * 1024 + 1));
    assert.throws(() => readDispatchEvent(env), /oversized/);
  } finally {
    rmSync(eventPath, { force: true });
    rmSync(join(root, '_github_workflow'), { recursive: true });
    rmSync(root, { recursive: true });
  }
});

test('real workflow collects assessment without secrets or bypassing existing environment approval', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const job = workflow.jobs.admit;
  const assessment = job.steps.find(step => step.run?.includes('release-dispatch.mjs'));
  assert.ok(assessment);
  assert.equal(job.environment, undefined);
  assert.ok(Object.values(job.permissions).every(value => value === 'read'));
  assert.doesNotMatch(JSON.stringify(job), /secrets\.|private-key|id-token/);
  assert.equal(assessment.env.RELEASE_TRANSACTION, '${{ steps.select.outputs.transaction }}');
  assert.equal(assessment.env.RELEASE_OPERATION, "${{ inputs.operation || 'publish' }}");
  assert.ok(job.steps.findIndex(step => step.id === 'select') < job.steps.indexOf(assessment));
  assert.match(publisher.jobs.publish.environment, /release-stable/);
  assert.match(publisher.jobs.publish.environment, /release-insider/);
  assert.doesNotMatch(JSON.stringify(publisher), /ownerDispatchEligible/);
  assert.equal(publisher.jobs.publish.steps.find(step => step.id === 'publisher')
    .with['permission-administration'], 'read');
});
