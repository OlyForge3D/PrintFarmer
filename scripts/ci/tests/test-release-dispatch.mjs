import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { load } from 'js-yaml';
import { assessOwnerDispatch, readDispatchEvent, validateDispatchAssessment,
  selectPublicationAccess, verifyPublicationAccess } from '../release-dispatch.mjs';
import { hash, publicationEnvironment } from '../release-policy.mjs';

const repository = 'OlyForge3D/PrintFarmer';
const workflowCommit = 'a'.repeat(40);
const sourceCommit = 'b'.repeat(40);

function fixture() {
  const actor = { login: 'jpapiez', id: 5460061, type: 'User' };
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
  const environment = { can_admins_bypass: false,
    deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
    protection_rules: [{ type: 'branch_policy' }] };
  const policies = { total_count: 1, branch_policies: [{ name: 'development', type: 'branch' }] };
  const requests = [];
  const api = async (endpoint, method = 'GET') => {
    requests.push(endpoint);
    assert.equal(method, 'GET', 'Assessment must never write to GitHub');
    if (endpoint === 'actions/runs/42') return structuredClone(run);
    if (endpoint === 'actions/workflows/consolidated-release.yml') return structuredClone(definition);
    if (endpoint === 'collaborators/jpapiez/permission') return structuredClone(permission);
    if (/^environments\/release-(stable|insider)$/.test(endpoint)) {
      return { name: endpoint.slice('environments/'.length), ...structuredClone(environment) };
    }
    if (endpoint.endsWith('/deployment-branch-policies')) return structuredClone(policies);
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
  return { env, event, run, definition, transaction, permission, requests, api, assess, environment, policies };
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

test('publication continues to ignore the abandonment-only target input', async () => {
  const f = fixture();
  const expected = await f.assess();
  for (const target of [' ', 'stray-value', 'c'.repeat(64)]) {
    f.event.inputs.reservation_target = f.env.RELEASE_ABANDONMENT_TARGET = target;
    assert.deepEqual(await f.assess(), expected);
  }
});

test('a reclaimed owner login does not match the pinned owner account', async () => {
  const f = fixture();
  for (const actor of [f.run.actor, f.run.triggering_actor, f.event.sender, f.permission.user]) {
    actor.id = 8;
  }
  f.env.GITHUB_ACTOR_ID = '8';
  await assert.rejects(f.assess(), /immutable owner/);
  assert.ok(!f.requests.includes('collaborators/jpapiez/permission'));
});

test('schedule, non-owner, reruns and separation reject without any environment access', async () => {
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
  ];
  for (const mutate of changes) {
    const f = fixture();
    mutate(f);
    await assert.rejects(f.assess());
    assert.ok(!f.requests.includes('collaborators/jpapiez/permission'));
    assert.ok(!f.requests.some(endpoint => endpoint.startsWith('environments/')));
  }
});

test('explicit abandonment is authorized only by the owner dispatch bound to its reservation', async () => {
  const f = fixture();
  f.env.RELEASE_OPERATION = f.event.inputs.operation = 'abandon';
  f.env.RELEASE_ABANDONMENT_TARGET = f.event.inputs.reservation_target = 'c'.repeat(64);
  f.transaction.channel = f.event.inputs.channel = 'insider'; f.transaction.sourceBranch = 'development';
  const assessment = await f.assess();
  assert.equal(assessment.ownerDispatchEligible, true);
  assert.equal(assessment.operation, 'abandon');
  assert.equal(assessment.reservationTarget, 'c'.repeat(64));
  assert.equal(publicationEnvironment(assessment), 'release-insider');
  f.event.inputs.channel = 'stable';
  await assert.rejects(f.assess(), /Dispatch channel/);
  f.event.inputs.channel = 'insider';
  f.env.RELEASE_ABANDONMENT_TARGET = 'd'.repeat(64);
  await assert.rejects(f.assess(), /Executing operation/);
});

test('original actor privileges cannot launder a non-owner rerun into owner consent', async () => {
  const f = fixture();
  f.env.GITHUB_RUN_ATTEMPT = '2'; f.run.run_attempt = 2;
  f.run.triggering_actor = { id: 8, login: 'maintainer', type: 'User' };
  f.env.GITHUB_TRIGGERING_ACTOR = 'maintainer';
  await assert.rejects(f.assess(), /Reruns/);
  f.env.GITHUB_TRIGGERING_ACTOR = 'jpapiez';
  await assert.rejects(f.assess(), /Reruns/);
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
    await assert.rejects(f.assess(), /administrator permission/);
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
    { operation: 'abandon', reservationTarget: '', channel: 'insider' },
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

test('real workflow gates every protected route with a blocking no-secret live dispatch boundary', () => {
  const workflow = load(readFileSync('.github/workflows/consolidated-release.yml', 'utf8'));
  const publisher = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  const job = publisher.jobs['dispatch-boundary'];
  const assessment = job.steps.find(step => step.run?.includes('release-dispatch.mjs'));
  assert.ok(assessment);
  assert.equal(job.environment, undefined);
  assert.ok(Object.values(job.permissions).every(value => value === 'read'));
  assert.doesNotMatch(JSON.stringify(job), /secrets\.|private-key|id-token/);
  assert.equal(job.if, undefined);
  assert.equal(job['continue-on-error'], undefined);
  assert.ok(job.steps.every(step => step['continue-on-error'] === undefined && step.if === undefined));
  assert.equal(assessment.env.RELEASE_TRANSACTION, '${{ inputs.transaction }}');
  assert.equal(assessment.env.RELEASE_OPERATION, '${{ inputs.operation }}');
  assert.equal(job.steps[0].with.ref, '${{ github.workflow_sha }}');
  assert.equal(job.outputs.environment, '${{ steps.dispatch.outputs.environment }}');
  assert.equal(publisher.jobs.publish.needs, 'dispatch-boundary');
  assert.equal(publisher.jobs.publish.if, undefined);
  assert.equal(publisher.jobs.publish.environment, '${{ needs.dispatch-boundary.outputs.environment }}');
  assert.equal(publisher.jobs.publish.env.RELEASE_PUBLICATION_ENVIRONMENT,
    '${{ needs.dispatch-boundary.outputs.environment }}');
  const steps = publisher.jobs.publish.steps;
  const recheck = steps.findIndex(step => step.run === 'node scripts/ci/release-dispatch.mjs verify');
  const mint = steps.findIndex(step => step.id === 'publisher');
  const abandonMint = steps.findIndex(step => step.id === 'abandonment_publisher');
  assert.ok(recheck >= 0 && recheck < mint);
  assert.equal(steps[recheck].if, undefined);
  assert.equal(steps[recheck]['continue-on-error'], undefined);
  assert.equal(steps[recheck].env.GH_TOKEN, '${{ github.token }}');
  assert.deepEqual(steps[mint].with, {
    owner: 'OlyForge3D', repositories: 'PrintFarmer',
    'app-id': '${{ vars.RELEASE_PUBLISHER_APP_ID }}',
    'private-key': '${{ secrets.RELEASE_PUBLISHER_PRIVATE_KEY }}',
    'permission-contents': 'write', 'permission-checks': 'read',
    'permission-statuses': 'read', 'permission-administration': 'write', 'permission-actions': 'read',
    'permission-workflows': 'write',
  });
  assert.equal(steps[mint]['continue-on-error'], undefined);
  assert.equal(steps[mint].if, "inputs.operation == 'publish'");
  assert.deepEqual(steps[abandonMint].with, {
    owner: 'OlyForge3D', repositories: 'PrintFarmer',
    'app-id': '${{ vars.RELEASE_PUBLISHER_APP_ID }}',
    'private-key': '${{ secrets.RELEASE_PUBLISHER_PRIVATE_KEY }}',
    'permission-contents': 'write', 'permission-checks': 'read',
    'permission-statuses': 'read', 'permission-administration': 'write', 'permission-actions': 'read',
  });
  assert.equal(steps[abandonMint].if, "inputs.operation == 'abandon'");
  assert.equal(steps[abandonMint]['continue-on-error'], undefined);
  assert.equal(workflow.jobs.publish.secrets, 'inherit');
  assert.doesNotMatch(JSON.stringify(workflow.jobs.admit), /secrets\./);
  assert.deepEqual(Object.keys(workflow.on), ['workflow_dispatch']);
  assert.deepEqual(Object.keys(publisher.on), ['workflow_call']);
  assert.doesNotMatch(JSON.stringify(publisher), /RELEASE_OWNER_APPROVED_REVIEWERS|Recover immutable authorization bytes/);
  assert.doesNotMatch(JSON.stringify(workflow), /Recover immutable original transaction|schedule/);
});

test('the selected environment must match freshly recomputed authority, not caller claims or old evidence', async () => {
  const f = fixture();
  const assess = await f.assess();
  assert.equal(publicationEnvironment(assess), 'release-stable');
  const env = { ...f.env, RELEASE_TRANSACTION: JSON.stringify(f.transaction),
    RELEASE_PUBLICATION_ENVIRONMENT: publicationEnvironment(assess) };
  assert.deepEqual(await verifyPublicationAccess(env, f.api, f.event), assess);
  for (const environment of [undefined, '', 'release-stable-owner-dispatch', 'release-insider', 'untrusted']) {
    await assert.rejects(verifyPublicationAccess({ ...env, RELEASE_PUBLICATION_ENVIRONMENT: environment },
      f.api, f.event), /Publication environment differs/);
  }
  f.permission.permission = f.permission.role_name = 'write';
  await assert.rejects(verifyPublicationAccess(env, f.api, f.event), /administrator permission/);
});

test('extra environment migration is removed rather than copying credentials', () => {
  assert.equal(existsSync('.github/release-owner-dispatch-environments.json'), false);
});

test('registry mutation CLIs reject unsupported runtime context before signature or registry commands', () => {
  for (const operation of ['tag', 'alias']) {
    for (const overrides of [
      { GITHUB_RUN_ATTEMPT: '2' },
      { GITHUB_EVENT_NAME: 'schedule' },
      { GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/untrusted.yml@refs/heads/development` },
    ]) {
      const f = fixture();
      const result = spawnSync(process.execPath, ['scripts/ci/release-set.mjs', operation], {
        encoding: 'utf8',
        env: { ...process.env, ...f.env, RELEASE_TRANSACTION: JSON.stringify(f.transaction), ...overrides },
      });
      assert.equal(result.status, 1);
      assert.match(result.stderr, /Reruns are unsupported|does not belong to the executing trusted workflow/);
      assert.doesNotMatch(result.stderr, /cosign|docker|ENOENT/);
    }
  }
  const workflow = load(readFileSync('.github/workflows/docker-publish.yml', 'utf8'));
  for (const operation of ['tag', 'alias']) {
    const step = workflow.jobs.publish.steps.find(step => step.run === `node scripts/ci/release-set.mjs ${operation}`);
    assert.equal(step.env.GH_TOKEN, '${{ github.token }}');
    assert.equal(step.env.RELEASE_PUBLISHER_TOKEN, '${{ steps.publisher.outputs.token }}');
    assert.equal(step.env.RELEASE_PUBLISHER_APP_ID, '${{ vars.RELEASE_PUBLISHER_APP_ID }}');
    assert.equal(step.env.RELEASE_LEDGER_ANCHOR, '${{ vars.RELEASE_LEDGER_ANCHOR }}');
    assert.equal(step.env.RELEASE_SOURCE_COMMIT, '${{ fromJSON(inputs.transaction).sourceCommit }}');
  }
});

test('live environment configuration must be safe before selecting a credential-bearing deployment', async () => {
      for (const mutate of [
        f => { delete f.environment.can_admins_bypass; },
        f => { f.environment.can_admins_bypass = true; },
        f => { f.environment.deployment_branch_policy.custom_branch_policies = false; },
        f => { f.environment.deployment_branch_policy.protected_branches = true; },
        f => { f.policies.branch_policies[0].name = '*'; },
        f => { f.policies.branch_policies[0].type = 'tag'; },
        f => { f.policies.branch_policies.push({ name: 'feature/*', type: 'branch' }); },
        f => { delete f.policies.total_count; },
        f => { f.policies.total_count = 2; },
        f => { delete f.policies.branch_policies; },
        f => { delete f.environment.protection_rules; },
        f => { f.environment.protection_rules = []; },
        f => { f.environment.protection_rules.push({ type: 'required_reviewers' }); },
        f => { f.environment.protection_rules.push({ type: 'wait_timer' }); },
      ]) {
        const f = fixture(); mutate(f);
        await assert.rejects(selectPublicationAccess({
          ...f.env, RELEASE_TRANSACTION: JSON.stringify(f.transaction),
        }, f.api, f.event), /Owner blocker/);
      }
});
